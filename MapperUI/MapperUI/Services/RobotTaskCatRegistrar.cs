// MapperUI/MapperUI/Services/RobotTaskCatRegistrar.cs
// ─────────────────────────────────────────────────────────────────────────────
// Generates the Robot_Task_CAT type folder (all 11 companion files) inside the
// target EAE project and registers every file in IEC61499.dfbproj.
//
// Called by MainForm.btnGenerateRobotWrapper_Click.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace MapperUI.Services
{
    public static class RobotTaskCatRegistrar
    {
        // ── File names that make up a complete Robot_Task_CAT type folder ─────
        private const string CatName = "Robot_Task_CAT";
        private const string HmiName = "Robot_Task_CAT_HMI";

        // ── Entry point ───────────────────────────────────────────────────────

        /// <summary>
        /// Copies Robot_Task_CAT template files into the target project and
        /// ensures every file is registered in IEC61499.dfbproj.
        /// Returns a human-readable result message.
        /// </summary>
        public static string Register(MapperConfig cfg, string dfbprojPath)
        {
            // ── 1. Validate config ────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(cfg.RobotTemplatePath))
                throw new InvalidOperationException(
                    "RobotTemplatePath is not set in mapper_config.json.\n" +
                    "Point it to the Robot_Task_CAT.fbt from the template project.");

            if (!File.Exists(cfg.RobotTemplatePath))
                throw new FileNotFoundException(
                    $"Robot template not found:\n{cfg.RobotTemplatePath}");

            if (!File.Exists(dfbprojPath))
                throw new FileNotFoundException(
                    $"IEC61499.dfbproj not found:\n{dfbprojPath}");

            // ── 2. Derive paths ───────────────────────────────────────────────
            var templateDir = Path.GetDirectoryName(cfg.RobotTemplatePath)!;
            var iec61499Dir = Path.GetDirectoryName(dfbprojPath)!;          // …/IEC61499/
            var targetCatDir = Path.Combine(iec61499Dir, CatName);

            Directory.CreateDirectory(targetCatDir);

            MapperLogger.Info($"[Robot_Task_CAT] Source template dir : {templateDir}");
            MapperLogger.Info($"[Robot_Task_CAT] Target CAT dir       : {targetCatDir}");

            // ── 3. Copy / generate each file ──────────────────────────────────
            int copied = 0;
            int skipped = 0;
            int generated = 0;

            // 3a. Robot_Task_CAT.fbt  (copy from template, keep as-is)
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{CatName}.fbt", null,
                           ref copied, ref skipped, ref generated);

            // 3b. Robot_Task_CAT.meta.xml  (empty is correct per spec)
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{CatName}.meta.xml",
                           () => string.Empty,
                           ref copied, ref skipped, ref generated);

            // 3c. Robot_Task_CAT.doc.xml
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{CatName}.doc.xml",
                           GenerateDocXml,
                           ref copied, ref skipped, ref generated);

            // 3d. Robot_Task_CAT.cfg  (XML-aware: update Name + Plugin paths)
            CopyOrGenerateCfg(templateDir, targetCatDir,
                              ref copied, ref skipped, ref generated);

            // 3e. Robot_Task_CAT_CAT.offline.xml
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{CatName}_CAT.offline.xml",
                           GenerateOfflineXml,
                           ref copied, ref skipped, ref generated);

            // 3f. Robot_Task_CAT_CAT.opcua.xml
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{CatName}_CAT.opcua.xml",
                           GenerateCatOpcuaXml,
                           ref copied, ref skipped, ref generated);

            // 3g. Robot_Task_CAT_HMI.fbt
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{HmiName}.fbt", null,
                           ref copied, ref skipped, ref generated);

            // 3h. Robot_Task_CAT_HMI.meta.xml  (empty)
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{HmiName}.meta.xml",
                           () => string.Empty,
                           ref copied, ref skipped, ref generated);

            // 3i. Robot_Task_CAT_HMI.doc.xml
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{HmiName}.doc.xml",
                           GenerateDocXml,
                           ref copied, ref skipped, ref generated);

            // 3j. Robot_Task_CAT_HMI.offline.xml
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{HmiName}.offline.xml",
                           GenerateOfflineXml,
                           ref copied, ref skipped, ref generated);

            // 3k. Robot_Task_CAT_HMI.opcua.xml  (richer: exposes current_state_to_process)
            CopyOrGenerate(templateDir, targetCatDir,
                           $"{HmiName}.opcua.xml",
                           GenerateHmiOpcuaXml,
                           ref copied, ref skipped, ref generated);

            // ── 4. Register in dfbproj ─────────────────────────────────────────
            int registered = RegisterInDfbproj(dfbprojPath, iec61499Dir);

            var sb = new StringBuilder();
            sb.AppendLine($"Robot_Task_CAT folder populated in:");
            sb.AppendLine($"  {targetCatDir}");
            sb.AppendLine();
            sb.AppendLine($"  Files copied    : {copied}");
            sb.AppendLine($"  Files generated : {generated}");
            sb.AppendLine($"  Files skipped   : {skipped} (already present)");
            sb.AppendLine($"  dfbproj entries : {registered} added / already present");
            sb.AppendLine();
            sb.AppendLine("Robot_Task_CAT is now registered in IEC61499.dfbproj.");

            return sb.ToString();
        }

        // ── File helpers ──────────────────────────────────────────────────────

        private static void CopyOrGenerate(
            string templateDir, string targetDir, string fileName,
            Func<string>? generator,
            ref int copied, ref int skipped, ref int generated)
        {
            var target = Path.Combine(targetDir, fileName);
            if (File.Exists(target)) { skipped++; return; }

            var source = Path.Combine(templateDir, fileName);
            if (File.Exists(source) && generator == null)
            {
                File.Copy(source, target, overwrite: false);
                copied++;
                MapperLogger.Info($"[Robot_Task_CAT]  Copied    {fileName}");
            }
            else if (File.Exists(source))
            {
                File.Copy(source, target, overwrite: false);
                copied++;
                MapperLogger.Info($"[Robot_Task_CAT]  Copied    {fileName}");
            }
            else if (generator != null)
            {
                File.WriteAllText(target, generator(), Encoding.UTF8);
                generated++;
                MapperLogger.Info($"[Robot_Task_CAT]  Generated {fileName}");
            }
            else
            {
                MapperLogger.Info($"[Robot_Task_CAT]  WARNING: {fileName} not found in template dir and no generator – skipping.");
            }
        }

        private static void CopyOrGenerateCfg(
            string templateDir, string targetDir,
            ref int copied, ref int skipped, ref int generated)
        {
            var fileName = $"{CatName}.cfg";
            var target = Path.Combine(targetDir, fileName);
            if (File.Exists(target)) { skipped++; return; }

            var source = Path.Combine(templateDir, fileName);
            if (File.Exists(source))
            {
                // XML-aware copy: Name attr stays "Robot_Task_CAT" (already correct)
                // but Plugin paths must reference the folder, not a flat path.
                var cfgXml = PatchCfgPluginPaths(source);
                File.WriteAllText(target, cfgXml, Encoding.UTF8);
                copied++;
                MapperLogger.Info($"[Robot_Task_CAT]  Copied+Patched {fileName}");
            }
            else
            {
                File.WriteAllText(target, GenerateCfgXml(), Encoding.UTF8);
                generated++;
                MapperLogger.Info($"[Robot_Task_CAT]  Generated {fileName}");
            }
        }

        // ── dfbproj registration ──────────────────────────────────────────────

        private static int RegisterInDfbproj(string dfbprojPath, string iec61499Dir)
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            int adds = 0;

            // Helper: add element if Include path not already present
            void EnsureEntry(XElement parent, XElement entry)
            {
                var inc = (string?)entry.Attribute("Include") ?? string.Empty;
                bool exists = parent.Elements()
                    .Any(e => string.Equals(
                             (string?)e.Attribute("Include"), inc,
                             StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    parent.Add(entry);
                    adds++;
                    MapperLogger.Info($"[Robot_Task_CAT]  dfbproj += {inc}");
                }
            }

            // Find the first <ItemGroup> that already has <Compile Include> entries
            var compileGroup = xml.Root!
                .Elements(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "Compile").Any())
                ?? throw new InvalidOperationException("No <Compile> ItemGroup in .dfbproj");

            // Find the first <ItemGroup> that already has <None Include> entries
            var noneGroup = xml.Root!
                .Elements(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "None").Any())
                ?? compileGroup;   // fallback: same group

            // ── Compile entries (fbt files) ───────────────────────────────────

            EnsureEntry(compileGroup,
                new XElement(ns + "Compile",
                    new XAttribute("Include", $@"{CatName}\{CatName}.fbt"),
                    new XElement(ns + "IEC61499Type", "CAT")));

            EnsureEntry(compileGroup,
                new XElement(ns + "Compile",
                    new XAttribute("Include", $@"{CatName}\{HmiName}.fbt"),
                    new XElement(ns + "IEC61499Type", "CAT"),
                    new XElement(ns + "DependentUpon", $@"{CatName}\{CatName}.fbt"),
                    new XElement(ns + "Usage", "Private")));

            // ── None entries (companion plugin / meta files) ──────────────────

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{CatName}.cfg"),
                    new XElement(ns + "DependentUpon", $@"{CatName}.fbt"),
                    new XElement(ns + "IEC61499Type", "CAT")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{CatName}_CAT.offline.xml"),
                    new XElement(ns + "DependentUpon", $@"{CatName}.fbt"),
                    new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                    new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{CatName}_CAT.opcua.xml"),
                    new XElement(ns + "DependentUpon", $@"{CatName}.fbt"),
                    new XElement(ns + "Plugin", "OPCUAConfigurator"),
                    new XElement(ns + "IEC61499Type", "CAT_OPCUA")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{HmiName}.meta.xml"),
                    new XElement(ns + "DependentUpon", $@"{HmiName}.fbt")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{HmiName}.offline.xml"),
                    new XElement(ns + "DependentUpon", $@"{CatName}.fbt"),
                    new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                    new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{HmiName}.opcua.xml"),
                    new XElement(ns + "DependentUpon", $@"{CatName}.fbt"),
                    new XElement(ns + "Plugin", "OPCUAConfigurator"),
                    new XElement(ns + "IEC61499Type", "CAT_OPCUA")));

            // ── Save ──────────────────────────────────────────────────────────
            xml.Save(dfbprojPath);
            MapperLogger.Info($"[Robot_Task_CAT] dfbproj saved. {adds} new entries added.");
            return adds;
        }

        // ── .cfg patch: ensure Plugin paths use folder-relative paths ─────────

        private static string PatchCfgPluginPaths(string sourceCfgPath)
        {
            var doc = XDocument.Load(sourceCfgPath);
            var root = doc.Root!;
            XNamespace ns = root.GetDefaultNamespace();

            // Only change the Name attribute on the root if it differs
            root.SetAttributeValue("Name", CatName);

            // Rewrite Plugin Value paths to always start with "Robot_Task_CAT\"
            foreach (var plugin in root.Elements(ns + "Plugin").Concat(root.Elements("Plugin")))
            {
                var val = (string?)plugin.Attribute("Value");
                if (val == null) continue;

                var flat = Path.GetFileName(val);   // strip any existing folder prefix
                plugin.SetAttributeValue("Value", $@"{CatName}\{flat}");
            }

            using var sw = new StringWriter();
            doc.Save(sw);
            return sw.ToString();
        }

        // ── Content generators (used when template file is absent) ────────────

        private static string GenerateDocXml() =>
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <section xmlns="http://docbook.org/ns/docbook"
                     xmlns:xi="http://www.w3.org/2001/XInclude"
                     xmlns:xlink="http://www.w3.org/1999/xlink">
              <info>
                <author>
                  <personname>
                    <firstname>Firstname</firstname>
                    <surname>Surname</surname>
                  </personname>
                  <email>name@company.com</email>
                </author>
                <abstract>
                  <para>Summary</para>
                </abstract>
              </info>
              <para></para>
            </section>
            """;

        private static string GenerateOfflineXml() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <OfflineParameterModel xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                                   xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                                   IsDefaultEventSelectionDialogsHidden="0" />
            """;

        private static string GenerateCatOpcuaXml() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <OPCUAObject xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" />
            """;

        /// <summary>
        /// HMI OPC-UA file exposes the key output variable 'current_state_to_process'
        /// (mirrors the Robot_Task_CAT_HMI.fbt interface from the SMC Rig template).
        /// </summary>
        private static string GenerateHmiOpcuaXml() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <OPCUAObject xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <OPCUAVariable UID="F0E4B21755073D3D" Enabled="false">
                <OPCUAAttribute Name="Exposed" Value="True" Locked="false"
                                AttributeMask="True;True|False;True" />
                <OPCUAAttribute Name="AccessLevel" Value="1" Locked="true"
                                AttributeMask="CurrentRead;True" />
                <Extensions>
                  <Extension>
                    <RTAddress>V1;${VariableFullPath}</RTAddress>
                  </Extension>
                </Extensions>
              </OPCUAVariable>
            </OPCUAObject>
            """;

        /// <summary>
        /// Fallback .cfg generator used when no template .cfg exists.
        /// Mirrors the Five_State_Actuator_CAT.cfg structure exactly,
        /// substituting Robot_Task_CAT everywhere.
        /// </summary>
        private static string GenerateCfgXml() =>
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CAT xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                 xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                 Name="{CatName}"
                 CATFile="{CatName}\{CatName}.fbt"
                 SymbolDefFile="..\HMI\{CatName}\{CatName}.def.cs"
                 SymbolEventFile="..\HMI\{CatName}\{CatName}.event.cs"
                 DesignFile="..\HMI\{CatName}\{CatName}.Design.resx"
                 xmlns="http://www.nxtcontrol.com/IEC61499.xsd">
              <HMIInterface Name="IThis"
                            FileName="{CatName}\{HmiName}.fbt"
                            UsedInCAT="true"
                            Usage="Private">
                <Symbol Name="sDefault"
                        FileName="..\HMI\{CatName}\{CatName}_sDefault.cnv.cs">
                  <DependentFiles>..\HMI\{CatName}\{CatName}_sDefault.cnv.Designer.cs</DependentFiles>
                  <DependentFiles>..\HMI\{CatName}\{CatName}_sDefault.cnv.resx</DependentFiles>
                  <DependentFiles>..\HMI\{CatName}\{CatName}_sDefault.cnv.xml</DependentFiles>
                </Symbol>
              </HMIInterface>
              <Plugin Name="Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"
                      Project="IEC61499"
                      Value="{CatName}\{CatName}_CAT.offline.xml" />
              <Plugin Name="Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"
                      Project="IEC61499"
                      Value="{CatName}\{CatName}_CAT.opcua.xml" />
              <Plugin Name="Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"
                      Project="IEC61499"
                      Value="{CatName}\{HmiName}.offline.xml" />
              <Plugin Name="Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"
                      Project="IEC61499"
                      Value="{CatName}\{HmiName}.opcua.xml" />
              <HWConfiguration xsi:nil="true" />
            </CAT>
            """;
    }
}