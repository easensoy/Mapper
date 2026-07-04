using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;

namespace CodeGen.Services
{
    // Low-level template-artefact deployment: locate a CAT/FB/DataType package in the Template Library,
    // copy/extract it into the EAE project (copy-if-absent), generate .cfg files, register it in the
    // .dfbproj, and sweep retired types. No FBT XML patching lives here.
    internal static class TemplateArtifactDeployer
    {
        internal static void SweepRetiredType(string eaeProjectDir, string typeName, DeployResult result)
        {
            try
            {
                var iec = Path.Combine(eaeProjectDir, "IEC61499");
                int filesGone = 0;
                foreach (var p in new[]
                {
                    Path.Combine(iec, typeName + ".fbt"),
                    Path.Combine(iec, typeName + ".doc.xml"),
                    Path.Combine(iec, typeName + ".meta.xml"),
                    Path.Combine(eaeProjectDir, typeName + ".Basic.export"),
                })
                    if (File.Exists(p)) { File.Delete(p); filesGone++; }

                var dfbproj = Path.Combine(iec, "IEC61499.dfbproj");
                int entriesGone = 0;
                if (File.Exists(dfbproj))
                {
                    var doc = System.Xml.Linq.XDocument.Load(dfbproj, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    foreach (var el in doc.Descendants()
                        .Where(e => (e.Name.LocalName == "Compile" || e.Name.LocalName == "None")
                            && ((string?)e.Attribute("Include"))?.StartsWith(typeName + ".", StringComparison.Ordinal) == true)
                        .ToList())
                    { el.Remove(); entriesGone++; }
                    if (entriesGone > 0) doc.Save(dfbproj);
                }
                if (filesGone > 0 || entriesGone > 0)
                    result.PatchesApplied.Add($"retired {typeName}: {filesGone} file(s) + {entriesGone} dfbproj entry(ies) removed");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"retire {typeName} failed: {ex.Message}");
            }
        }

        internal static void DeployArtifact(string libPath, string subfolder, string name,
            string eaeProjectDir, DeployResult result, bool isBasic, bool isCat = false)
        {
            var folder = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(folder))
            {
                result.Warnings.Add($"Library subfolder missing: {subfolder}");
                return;
            }

            var zipPath = FindArtifactZip(folder, name);
            if (zipPath != null)
            {
                ExtractToEae(zipPath, eaeProjectDir, result);
            }
            else
            {
                var dirPath = FindArtifactDir(folder, name);
                if (dirPath != null)
                {
                    CopyDirToEae(dirPath, eaeProjectDir, result);
                }
                else
                {
                    result.Warnings.Add($"Artifact not found: {subfolder}/{name}");
                    return;
                }
            }

            if (isCat) result.CATsDeployed.Add(name);
            else if (string.Equals(subfolder, "Adapter", StringComparison.OrdinalIgnoreCase))
                result.AdaptersDeployed.Add(name);
            else if (string.Equals(subfolder, "Composite", StringComparison.OrdinalIgnoreCase))
                result.CompositesDeployed.Add(name);
            else if (isBasic) result.BasicFBsDeployed.Add(name);
        }

        // Skip ".subcats.zip" wrappers (they leave the CAT folder uncreated while the dfbproj still
        // registers <name>.cfg). Prefer the newest by filename (deterministic across dated versions).
        static string? FindArtifactZip(string folder, string name)
        {
            var zips = Directory.GetFiles(folder, "*.zip")
                .Where(f => !Path.GetFileName(f)
                    .Contains(".subcats.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var f in zips)
            {
                var fn = Path.GetFileName(f);
                if (fn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    fn.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fn, name + ".zip", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            foreach (var f in zips)
            {
                if (Path.GetFileName(f).Contains(name + ".", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            return null;
        }

        static string? FindArtifactDir(string folder, string name)
        {
            foreach (var d in Directory.GetDirectories(folder))
            {
                var dn = Path.GetFileName(d);
                if (dn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dn, name, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        internal static void CopyDirToEae(string sourceDir, string eaeProjectDir, DeployResult result)
        {
            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var parts = rel.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    rel = string.Join("/", parts.Skip(1));

                var targetPath = Path.Combine(eaeProjectDir, rel);
                var targetDir = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                if (!File.Exists(targetPath))
                {
                    File.Copy(file, targetPath);
                    result.FilesExtracted++;
                }
                else
                {
                    result.FilesSkipped++;
                }
            }
        }

        // Copy-if-absent: existing files are not overwritten (I-7 deploy-revert trap).
        internal static void ExtractToEae(string zipPath, string eaeProjectDir, DeployResult result)
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };
            string? prefixToStrip = null;

            var firstFile = zip.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
            if (firstFile != null)
            {
                var parts = firstFile.FullName.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    prefixToStrip = parts[0] + "/";
            }

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var relativePath = entry.FullName;
                if (prefixToStrip != null && relativePath.StartsWith(prefixToStrip, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(prefixToStrip.Length);

                var targetPath = Path.Combine(eaeProjectDir, relativePath);
                var targetDir = Path.GetDirectoryName(targetPath)!;

                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                if (!File.Exists(targetPath))
                {
                    entry.ExtractToFile(targetPath);
                    result.FilesExtracted++;
                }
                else
                {
                    result.FilesSkipped++;
                }
            }
        }

        internal static void GenerateCfgFiles(string eaeProjectDir, DeployResult result)
        {
            var iec61499Dir = Path.Combine(eaeProjectDir, "IEC61499");
            foreach (var cat in result.CATsDeployed)
            {
                var catDir = Path.Combine(iec61499Dir, cat);
                var cfgPath = Path.Combine(catDir, $"{cat}.cfg");
                if (File.Exists(cfgPath)) continue;
                if (!Directory.Exists(catDir)) continue;

                var hmi = cat + "_HMI";
                var cfg = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<CAT xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" Name=""{cat}"" CATFile=""{cat}\{cat}.fbt"" SymbolDefFile=""..\HMI\{cat}\{cat}.def.cs"" SymbolEventFile=""..\HMI\{cat}\{cat}.event.cs"" DesignFile=""..\HMI\{cat}\{cat}.Design.resx"" xmlns=""http://www.nxtcontrol.com/IEC61499.xsd"">
  <HMIInterface Name=""IThis"" FileName=""{cat}\{hmi}.fbt"" UsedInCAT=""true"" Usage=""Private"">
    <Symbol Name=""sDefault"" FileName=""..\HMI\{cat}\{cat}_sDefault.cnv.cs"">
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.Designer.cs</DependentFiles>
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.resx</DependentFiles>
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.xml</DependentFiles>
    </Symbol>
  </HMIInterface>
  <Plugin Name=""Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{cat}_CAT.offline.xml"" />
  <Plugin Name=""Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{cat}_CAT.opcua.xml"" />
  <Plugin Name=""Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{hmi}.offline.xml"" />
  <Plugin Name=""Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{hmi}.opcua.xml"" />
  <HWConfiguration xsi:nil=""true"" />
</CAT>";
                File.WriteAllText(cfgPath, cfg);
                result.FilesExtracted++;
                MapperLogger.Info($"[Deploy] Generated {cat}.cfg");

                var metaPath = Path.Combine(catDir, $"{hmi}.meta.xml");
                if (!File.Exists(metaPath))
                {
                    File.WriteAllBytes(metaPath, Array.Empty<byte>());
                    result.FilesExtracted++;
                    MapperLogger.Info($"[Deploy] Created empty {hmi}.meta.xml placeholder");
                }
            }
        }

        internal static void RegisterInDfbproj(string eaeProjectDir, DeployResult result)
        {
            var iec61499Dir = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec61499Dir)) return;

            var dfbproj = Directory.GetFiles(iec61499Dir, "*.dfbproj").FirstOrDefault();
            if (dfbproj == null) return;

            int changed = 0;
            foreach (var cat in result.CATsDeployed)
                changed += DfbprojRegistrar.RegisterCat(dfbproj, cat);

            foreach (var basic in result.BasicFBsDeployed)
                changed += DfbprojRegistrar.RegisterBasicFb(dfbproj, basic + ".fbt", "Basic");

            foreach (var adapter in result.AdaptersDeployed)
                changed += DfbprojRegistrar.RegisterBasicFb(dfbproj, adapter + ".adp", "Adapter");

            foreach (var composite in result.CompositesDeployed)
                changed += DfbprojRegistrar.RegisterBasicFb(dfbproj, composite + ".fbt", "Composite");

            foreach (var dt in result.DataTypesDeployed)
                changed += DfbprojRegistrar.RegisterDataType(dfbproj, $@"DataType\{dt}.dt");

            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.DPAC",   "24.1.0.33");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppBase", "24.1.0.21");
            // SE.IoTMx / SE.IoX80 declare the TM3 / X80 module type libraries the M262 / M580 .hcf need,
            // else EAE shows the Hardware Configurator empty / refuses the .hcf import.
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoTMx",   "24.1.0.19");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoX80",   "24.1.0.19");

            // BX1 physical-device libraries: the topology server resolves every Equipment catalogReference
            // against these; if any is unreferenced the WHOLE topology import fails.
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.HwCommon",                  "24.1.0.19");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.FieldDevice",               "24.1.0.31");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoNet",                     "24.1.0.11");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "Standard.IoEtherNetIP",        "24.1.0.27");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoATV",                     "24.1.0.26");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.ModbusGateway",             "24.1.0.17");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "Standard.IoModbus",            "24.1.0.32");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "Standard.IoModbusSlave",       "24.1.0.25");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "Standard.OPCUAClient",         "24.1.0.8");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppCommonProcess",          "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppConveying",              "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppSequence",               "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppStateManagement",        "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppLiquidFood",             "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppSingleLinePowerMonitoring", "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppWWW",                    "24.1.0.21");

            changed += DfbprojRegistrar.SweepIec61499Folder(dfbproj, iec61499Dir);

            // Only bump mtime when registration actually changed something, so an idempotent re-run
            // writes nothing and does not trigger a spurious EAE "Reload Solution".
            if (changed > 0)
            {
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Info($"[Deploy] dfbproj updated ({changed} entr(y/ies)): {Path.GetFileName(dfbproj)}");
            }
            else
            {
                MapperLogger.Info($"[Deploy] dfbproj already up to date; no write: {Path.GetFileName(dfbproj)}");
            }
        }

        internal static string ReadSysdevId(string sysdevPath)
        {
            if (string.IsNullOrEmpty(sysdevPath) || !File.Exists(sysdevPath)) return string.Empty;
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(sysdevPath);
                return (string?)doc.Root?.Attribute("ID") ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        internal static string? DeriveEaeProjectDir(MapperConfig cfg)
        {
            var syslayPath = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(syslayPath)) return null;

            var dir = Path.GetDirectoryName(syslayPath);
            while (dir != null)
            {
                var parent = Path.GetDirectoryName(dir);
                if (parent != null && Directory.Exists(Path.Combine(dir, "..")))
                {
                    var iec = Path.Combine(dir);
                    var checkDir = dir;
                    while (checkDir != null)
                    {
                        if (Directory.GetFiles(checkDir, "*.dfbproj").Any())
                            return Path.GetDirectoryName(checkDir);
                        checkDir = Path.GetDirectoryName(checkDir);
                    }
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}
