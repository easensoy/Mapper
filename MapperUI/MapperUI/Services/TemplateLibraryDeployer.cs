using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Models;

namespace MapperUI.Services
{
    public static class TemplateLibraryDeployer
    {
        static readonly Dictionary<string, string[]> CatDependencies = new()
        {
            { "Five_State_Actuator_CAT", new[] { "FiveStateActuator" } },
            { "Sensor_Bool_CAT",         new[] { "Sensor_Bool" } },
            { "Actuator_Fault_CAT",      new[] { "FaultLatch" } },
            { "Robot_Task_CAT",          new[] { "Robot_Task_Core" } },
            { "Seven_State_Actuator_CAT",new[] { "SevenStateActuator2" } },
            { "Station_CAT",             new[] { "Station_Core", "Station_Fault", "Station_Status" } },
            { "Process1_Generic",        new[] { "ProcessRuntime_Generic_v1", "ProcessStateBusHandler" } },
        };

        static readonly Dictionary<string, string> ComponentTypeToCat = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Actuator_5",  "Five_State_Actuator_CAT" },
            { "Actuator_7",  "Seven_State_Actuator_CAT" },
            { "Sensor_2",    "Sensor_Bool_CAT" },
            { "Process_Any", "Process1_Generic" },
        };

        static readonly string[] UniversalCats = new[]
        {
            "Five_State_Actuator_CAT", "Sensor_Bool_CAT", "Process1_Generic"
        };

        static readonly string[] UniversalComposites = new[]
        {
            "Area", "Station", "CaSAdptrTerminator", "faultDetection"
        };

        static readonly string[] UniversalAdapters = new[]
        {
            "CaSAdptr", "AreaHMIAdptr", "StationHMIAdptr", "stateRptCmdAdptr"
        };

        static readonly string[] UniversalBasics = new[]
        {
            "FiveStateActuator", "Sensor_Bool",
            "Station_Core", "Station_Fault", "Station_Status",
            "ProcessRuntime_Generic_v1", "ProcessStateBusHandler",
            "FaultLatch", "actuatorStateEvents",
            "updateComponentState", "updateComponentState_Sensor",
            "No_Sensor_Handler"
        };

        static readonly string[] UniversalHmiCats = new[]
        {
            "Area_CAT", "Station_CAT"
        };

        // .dt files that the universal architecture references but no template zip ships.
        // Sourced from `Template Library/DataType/` and copied to `<eaeProject>/IEC61499/DataType/`.
        // Without these the compiler reports ERR_NO_SUCH_TYPE for Component_State on every FB
        // (ProcessStateBusHandler, updateComponentState, stateRptCmdAdptr, ProcessRuntime_Generic_v1).
        static readonly string[] UniversalDataTypes = new[]
        {
            "Component_State",
            "Component_State_Msg"
        };

        public static DeployResult DeployUniversalArchitecture(MapperConfig cfg)
        {
            var result = new DeployResult();
            var libPath = cfg.TemplateLibraryPath;
            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var eaeProjectDir = DeriveEaeProjectDir(cfg);
            if (string.IsNullOrWhiteSpace(eaeProjectDir))
                throw new InvalidOperationException("Cannot determine EAE project directory from syslay path.");

            foreach (var name in UniversalBasics)
                DeployArtifact(libPath, "Basic", name, eaeProjectDir, result, isBasic: true);

            foreach (var name in UniversalAdapters)
                DeployArtifact(libPath, "Adapter", name, eaeProjectDir, result, isBasic: true);

            foreach (var name in UniversalComposites)
                DeployArtifact(libPath, "Composite", name, eaeProjectDir, result, isBasic: false);

            foreach (var name in UniversalHmiCats)
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            foreach (var name in UniversalCats)
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            DeployDataTypes(libPath, eaeProjectDir, result);
            PatchKnownArraySizeBugs(eaeProjectDir, result);

            GenerateCfgFiles(eaeProjectDir, result);
            RegisterInDfbproj(eaeProjectDir, result);

            VerifyArraySizeConsistency(eaeProjectDir, result);

            // M262 deployment target. The Soft-dPAC topology path was deleted —
            // EcoRT_0 is now an embedded M262_dPAC device, not a Workstation runtime.
            try
            {
                var sysdev = M262SysdevEmitter.Emit(cfg);
                result.SysdevPath = sysdev.SysdevPath;
                result.SystemFilePath = sysdev.SystemFilePath;
                result.MappingsAdded = sysdev.MappingsAdded;
                MapperLogger.Info($"[Deploy] sysdev rewritten as M262_dPAC; {sysdev.MappingsAdded} APP→RES0 mapping(s) ensured");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"M262 sysdev emit failed: {ex.Message}");
            }

            try
            {
                var hcf = M262HwConfigCopier.Copy(cfg);
                result.HcfPath = hcf.HcfPath;
                result.HcfParametersOverwritten.AddRange(hcf.ParametersOverwritten);
                foreach (var w in hcf.Warnings)
                    result.Warnings.Add($"HCF: {w}");
                MapperLogger.Info($"[Deploy] hcf copied from baseline; {hcf.ParametersOverwritten.Count} channel parameter(s) overwritten");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"M262 hcf copy failed: {ex.Message}");
            }

            result.Success = true;
            return result;
        }

        /// <summary>
        /// Copies every file in `Template Library/DataType/` into the EAE project's
        /// `IEC61499/DataType/` folder. Idempotent: skips files that already exist.
        /// </summary>
        static void DeployDataTypes(string libPath, string eaeProjectDir, DeployResult result)
        {
            var srcDir = Path.Combine(libPath, "DataType");
            if (!Directory.Exists(srcDir))
            {
                result.Warnings.Add("Library DataType folder missing — Component_State*.dt won't be deployed.");
                return;
            }
            var destDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
            Directory.CreateDirectory(destDir);

            foreach (var name in UniversalDataTypes)
            {
                var src = Path.Combine(srcDir, name + ".dt");
                if (!File.Exists(src))
                {
                    result.Warnings.Add($"DataType source missing: {name}.dt");
                    continue;
                }
                var dst = Path.Combine(destDir, name + ".dt");
                if (!File.Exists(dst) ||
                    new FileInfo(src).Length != new FileInfo(dst).Length)
                {
                    File.Copy(src, dst, overwrite: true);
                    result.FilesExtracted++;
                }
                result.DataTypesDeployed.Add(name);
                MapperLogger.Info($"[Deploy] DataType: {name}");
            }
        }

        /// <summary>
        /// Patches known-bad ArraySize declarations in deployed .fbt files.
        /// The canonical ProcessRuntime_Generic_v1.fbt in the Jyotsna baseline declares
        /// state_table with ArraySize="1" but the connected ProcessStateBusHandler
        /// declares ArraySize="20", which causes EAE's compiler to reject the connection
        /// with "Cannot convert from type 'ARRAY[0..19] OF DINT' to type 'ARRAY[0..0] OF DINT'".
        /// Per spec we must NOT modify the canonical baseline, so we rewrite the deployed
        /// copy in place. Idempotent — safe to run after every deployment.
        /// </summary>
        static void PatchKnownArraySizeBugs(string eaeProjectDir, DeployResult result)
        {
            var fbtPath = Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            if (!File.Exists(fbtPath)) return;

            var text = File.ReadAllText(fbtPath);
            const string oldDecl =
                "<VarDeclaration Name=\"state_table\" Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"1\" />";
            const string newDecl =
                "<VarDeclaration Name=\"state_table\" Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"20\" />";

            if (text.Contains(newDecl)) return; // already patched, idempotent
            if (!text.Contains(oldDecl))
            {
                result.Warnings.Add(
                    "ProcessRuntime_Generic_v1.fbt: state_table declaration not found in expected " +
                    "shape (ArraySize=\"1\"). Skipping ArraySize patch — verify by hand.");
                return;
            }
            File.WriteAllText(fbtPath, text.Replace(oldDecl, newDecl));
            result.PatchesApplied.Add("ProcessRuntime_Generic_v1.state_table ArraySize 1 -> 20");
            MapperLogger.Info("[Deploy] Patched ProcessRuntime_Generic_v1.state_table ArraySize 1 -> 20");
        }

        /// <summary>
        /// Walks every .fbt under IEC61499 once and, for each VarDeclaration that names a
        /// vector that is also referenced by some other FB's connection, records its
        /// ArraySize keyed by FB type + var name. Any disagreement between source and
        /// destination of a Connection is reported as a warning.
        /// This is a verification pass only — it does not auto-fix beyond the targeted
        /// PatchKnownArraySizeBugs above. Helps catch regressions before EAE compile.
        /// </summary>
        static void VerifyArraySizeConsistency(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var iec = Path.Combine(eaeProjectDir, "IEC61499");
                if (!Directory.Exists(iec)) return;

                // (FB type, var name) -> ArraySize text
                var sizes = new Dictionary<(string, string), string>(
                    EqualityComparer<(string, string)>.Default);

                foreach (var fbt in Directory.EnumerateFiles(iec, "*.fbt", SearchOption.AllDirectories))
                {
                    System.Xml.Linq.XDocument doc;
                    try { doc = System.Xml.Linq.XDocument.Load(fbt); }
                    catch { continue; }
                    var fbType = Path.GetFileNameWithoutExtension(fbt);
                    foreach (var vd in doc.Descendants().Where(e => e.Name.LocalName == "VarDeclaration"))
                    {
                        var name = (string?)vd.Attribute("Name");
                        var arr = (string?)vd.Attribute("ArraySize");
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(arr)) continue;
                        sizes[(fbType, name)] = arr;
                    }
                }

                // For each composite, walk Connections and compare ArraySize on each side.
                foreach (var fbt in Directory.EnumerateFiles(iec, "*.fbt", SearchOption.AllDirectories))
                {
                    System.Xml.Linq.XDocument doc;
                    try { doc = System.Xml.Linq.XDocument.Load(fbt); }
                    catch { continue; }
                    // Build local map of (instanceName -> fbType) for resolving Connection ports.
                    var instances = doc.Descendants()
                        .Where(e => e.Name.LocalName == "FB")
                        .ToDictionary(
                            e => (string?)e.Attribute("Name") ?? "",
                            e => (string?)e.Attribute("Type") ?? "",
                            StringComparer.Ordinal);

                    foreach (var conn in doc.Descendants().Where(e => e.Name.LocalName == "Connection"))
                    {
                        var src = ((string?)conn.Attribute("Source") ?? "").Split('.', 2);
                        var dst = ((string?)conn.Attribute("Destination") ?? "").Split('.', 2);
                        if (src.Length != 2 || dst.Length != 2) continue;
                        if (!instances.TryGetValue(src[0], out var srcType)) continue;
                        if (!instances.TryGetValue(dst[0], out var dstType)) continue;

                        sizes.TryGetValue((srcType, src[1]), out var srcSize);
                        sizes.TryGetValue((dstType, dst[1]), out var dstSize);
                        if (srcSize == null || dstSize == null) continue;
                        if (!string.Equals(srcSize, dstSize, StringComparison.Ordinal))
                        {
                            var msg = $"ArraySize mismatch in {Path.GetFileName(fbt)}: " +
                                $"{src[0]}.{src[1]} (size {srcSize}) -> {dst[0]}.{dst[1]} (size {dstSize})";
                            result.Warnings.Add(msg);
                            MapperLogger.Warn("[Verify] " + msg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ArraySize verification crashed: {ex.Message}");
            }
        }

        static void DeployArtifact(string libPath, string subfolder, string name,
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

        static string? FindArtifactZip(string folder, string name)
        {
            foreach (var f in Directory.GetFiles(folder, "*.zip"))
            {
                var fn = Path.GetFileName(f);
                if (fn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    fn.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fn, name + ".zip", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            foreach (var f in Directory.GetFiles(folder, "*.zip"))
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

        static void CopyDirToEae(string sourceDir, string eaeProjectDir, DeployResult result)
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

        public static DeployResult Deploy(MapperConfig cfg, List<VueOneComponent> components)
        {
            var result = new DeployResult();
            var libPath = cfg.TemplateLibraryPath;

            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var eaeProjectDir = DeriveEaeProjectDir(cfg);
            if (string.IsNullOrWhiteSpace(eaeProjectDir))
                throw new InvalidOperationException(
                    "Cannot determine EAE project directory from syslay path.");

            var neededCats = ResolveNeededCats(components);
            var neededBasics = ResolveNeededBasics(neededCats);

            foreach (var basic in neededBasics)
            {
                var zipPath = FindPackage(libPath, "Basic", basic, ".Basic");
                if (zipPath == null)
                {
                    result.Warnings.Add($"Basic package not found: {basic}");
                    continue;
                }
                ExtractToEae(zipPath, eaeProjectDir, result);
                result.BasicFBsDeployed.Add(basic);
                MapperLogger.Info($"[Deploy] Basic: {basic}");
            }

            foreach (var cat in neededCats)
            {
                var zipPath = FindPackage(libPath, "CAT", cat, ".cat");
                if (zipPath == null)
                {
                    result.Warnings.Add($"CAT package not found: {cat}");
                    continue;
                }
                ExtractToEae(zipPath, eaeProjectDir, result);
                result.CATsDeployed.Add(cat);
                MapperLogger.Info($"[Deploy] CAT: {cat}");
            }

            GenerateCfgFiles(eaeProjectDir, result);
            RegisterInDfbproj(eaeProjectDir, result);

            result.Success = true;
            return result;
        }

        static HashSet<string> ResolveNeededCats(List<VueOneComponent> components)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in components)
            {
                var key = $"{c.Type}_{c.States.Count}";
                if (ComponentTypeToCat.TryGetValue(key, out var cat))
                    cats.Add(cat);

                if (string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                    ComponentTypeToCat.TryGetValue("Process_Any", out var procCat))
                    cats.Add(procCat);
            }
            return cats;
        }

        static HashSet<string> ResolveNeededBasics(HashSet<string> cats)
        {
            var basics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in cats)
            {
                if (CatDependencies.TryGetValue(cat, out var deps))
                    foreach (var dep in deps)
                        basics.Add(dep);
            }
            return basics;
        }

        static string? FindPackage(string libPath, string subfolder, string name, string extension)
        {
            var dir = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(dir)) return null;

            foreach (var file in Directory.GetFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                    fileName.Contains(extension, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }

        static void ExtractToEae(string zipPath, string eaeProjectDir, DeployResult result)
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

        static void GenerateCfgFiles(string eaeProjectDir, DeployResult result)
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

                // EAE expects {cat}\{cat}_HMI.meta.xml to exist (registered in .dfbproj as a
                // <DependentUpon> of the HMI .fbt). The template zips don't ship one — EAE
                // creates and writes it lazily — so we drop a zero-byte placeholder so EAE
                // stops complaining "Missing Project Files" on first load. It will get
                // populated by EAE itself the first time the HMI is edited.
                var metaPath = Path.Combine(catDir, $"{hmi}.meta.xml");
                if (!File.Exists(metaPath))
                {
                    File.WriteAllBytes(metaPath, Array.Empty<byte>());
                    result.FilesExtracted++;
                    MapperLogger.Info($"[Deploy] Created empty {hmi}.meta.xml placeholder");
                }
            }
        }

        static void RegisterInDfbproj(string eaeProjectDir, DeployResult result)
        {
            var iec61499Dir = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec61499Dir)) return;

            var dfbproj = Directory.GetFiles(iec61499Dir, "*.dfbproj").FirstOrDefault();
            if (dfbproj == null) return;

            foreach (var cat in result.CATsDeployed)
                DfbprojRegistrar.RegisterCat(dfbproj, cat);

            foreach (var basic in result.BasicFBsDeployed)
                DfbprojRegistrar.RegisterBasicFb(dfbproj, basic + ".fbt");

            foreach (var adapter in result.AdaptersDeployed)
                DfbprojRegistrar.RegisterBasicFb(dfbproj, adapter + ".adp", "Adapter");

            foreach (var composite in result.CompositesDeployed)
                DfbprojRegistrar.RegisterBasicFb(dfbproj, composite + ".fbt", "Composite");

            foreach (var dt in result.DataTypesDeployed)
                DfbprojRegistrar.RegisterDataType(dfbproj, $@"DataType\{dt}.dt");

            // M262 boot-scaffold types: DPAC_FULLINIT lives in SE.DPAC, plcStart in
            // SE.AppBase. The syslay/sysres reference them by Type+Namespace; without
            // a matching <Reference> entry in the dfbproj the compile fails with
            // ERR_NO_SUCH_TYPE. SE.IoTMx covers BMTM3/TM262L01MDESE8T/TM3DI16_G/
            // TM3DQ16T_G — pin the same versions used by the validated baseline.
            DfbprojRegistrar.RegisterReference(dfbproj, "SE.DPAC",   "24.1.0.33");
            DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppBase", "24.1.0.21");
            DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoTMx",   "24.1.0.19");

            // Safety net: any .dt/.adp/.fbt that ended up on disk but isn't in the
            // explicit lists above (e.g. dropped manually, or from a future template
            // bundle) gets picked up here.
            DfbprojRegistrar.SweepIec61499Folder(dfbproj, iec61499Dir);

            File.SetLastWriteTime(dfbproj, DateTime.Now);
            MapperLogger.Info($"[Deploy] dfbproj updated: {Path.GetFileName(dfbproj)}");
        }

        static string? DeriveEaeProjectDir(MapperConfig cfg)
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

    public class DeployResult
    {
        public bool Success { get; set; }
        public List<string> BasicFBsDeployed { get; set; } = new();
        public List<string> CATsDeployed { get; set; } = new();
        public List<string> AdaptersDeployed { get; set; } = new();
        public List<string> CompositesDeployed { get; set; } = new();
        public List<string> DataTypesDeployed { get; set; } = new();
        public List<string> PatchesApplied { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int FilesExtracted { get; set; }
        public int FilesSkipped { get; set; }

        public string? SysdevPath { get; set; }
        public string? SystemFilePath { get; set; }
        public int MappingsAdded { get; set; }

        public string? HcfPath { get; set; }
        public List<string> HcfParametersOverwritten { get; set; } = new();
    }
}