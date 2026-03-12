// MapperUI/MapperUI/Services/TemplatePackager.cs
// ─────────────────────────────────────────────────────────────────────────────
// Copies validated CAT template folders and their companion Basic FBs from a
// source project (Station1) into a blank target project (Demonstrator).
// Registers all files in the target's IEC61499.dfbproj.
//
// This is NOT code generation. It's template deployment.
// The templates are pre-built by the automation team and never modified.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace MapperUI.Services
{
    public static class TemplatePackager
    {
        // CAT folder name → Basic FB filename at IEC61499 root
        private static readonly Dictionary<string, string> CatToBasicFb = new()
        {
            { "Five_State_Actuator_CAT", "FiveStateActuator.fbt" },
            { "Sensor_Bool_CAT", "Sensor_Bool.fbt" },
        };

        // Basic FB → IEC61499Type for dfbproj registration
        private static readonly Dictionary<string, string> BasicFbTypes = new()
        {
            { "FiveStateActuator.fbt", "Basic" },
            { "Sensor_Bool.fbt", "Basic" },
        };

        /// <summary>
        /// Copies CAT template folders and Basic FBs from source into target project.
        /// Registers everything in the target dfbproj.
        ///
        /// sourceIec61499Dir = Station1's IEC61499 folder (where templates live)
        /// targetIec61499Dir = Demonstrator's IEC61499 folder (blank target)
        /// dfbprojPath = Demonstrator's IEC61499.dfbproj
        /// sourceHmiDir = Station1's HMI folder (for CAT HMI files)
        /// targetHmiDir = Demonstrator's HMI folder
        /// </summary>
        public static string Package(
            string sourceIec61499Dir,
            string targetIec61499Dir,
            string dfbprojPath,
            string sourceHmiDir,
            string targetHmiDir)
        {
            if (!Directory.Exists(sourceIec61499Dir))
                throw new DirectoryNotFoundException($"Source IEC61499 not found: {sourceIec61499Dir}");
            if (!File.Exists(dfbprojPath))
                throw new FileNotFoundException($"Target dfbproj not found: {dfbprojPath}");

            var sb = new StringBuilder();
            int catsCopied = 0, basicsCopied = 0, hmiCopied = 0, registered = 0;

            // ── 1. Copy CAT folders ───────────────────────────────────────
            foreach (var (catName, basicFb) in CatToBasicFb)
            {
                var sourceDir = Path.Combine(sourceIec61499Dir, catName);
                var targetDir = Path.Combine(targetIec61499Dir, catName);

                if (!Directory.Exists(sourceDir))
                {
                    MapperLogger.Warn($"[TemplatePackager] Source CAT folder not found: {sourceDir}");
                    continue;
                }

                if (Directory.Exists(targetDir))
                {
                    MapperLogger.Info($"[TemplatePackager] {catName} already exists in target — skipped.");
                }
                else
                {
                    CopyDirectory(sourceDir, targetDir);
                    catsCopied++;
                    MapperLogger.Info($"[TemplatePackager] Copied {catName}\\ ({Directory.GetFiles(targetDir).Length} files)");
                }

                // ── 2. Copy Basic FB ──────────────────────────────────────
                var sourceFb = Path.Combine(sourceIec61499Dir, basicFb);
                var targetFb = Path.Combine(targetIec61499Dir, basicFb);

                if (File.Exists(sourceFb) && !File.Exists(targetFb))
                {
                    File.Copy(sourceFb, targetFb);
                    basicsCopied++;
                    MapperLogger.Info($"[TemplatePackager] Copied {basicFb}");

                    // Also copy companion files (.doc.xml, .meta.xml) if they exist
                    var fbBase = Path.GetFileNameWithoutExtension(basicFb);
                    foreach (var suffix in new[] { ".doc.xml", ".meta.xml" })
                    {
                        var src = Path.Combine(sourceIec61499Dir, fbBase + suffix);
                        var dst = Path.Combine(targetIec61499Dir, fbBase + suffix);
                        if (File.Exists(src) && !File.Exists(dst))
                            File.Copy(src, dst);
                    }
                }

                // ── 3. Copy HMI folder ────────────────────────────────────
                if (!string.IsNullOrEmpty(sourceHmiDir) && !string.IsNullOrEmpty(targetHmiDir))
                {
                    var sourceHmiCat = Path.Combine(sourceHmiDir, catName);
                    var targetHmiCat = Path.Combine(targetHmiDir, catName);

                    if (Directory.Exists(sourceHmiCat) && !Directory.Exists(targetHmiCat))
                    {
                        CopyDirectory(sourceHmiCat, targetHmiCat);
                        hmiCopied++;
                        MapperLogger.Info($"[TemplatePackager] Copied HMI\\{catName}\\ ({Directory.GetFiles(targetHmiCat).Length} files)");
                    }
                }
            }

            // ── 4. Register in dfbproj ────────────────────────────────────
            registered = RegisterInDfbproj(dfbprojPath);

            // ── 5. Touch dfbproj ──────────────────────────────────────────
            File.SetLastWriteTime(dfbprojPath, DateTime.Now);

            sb.AppendLine("Template packaging complete.");
            sb.AppendLine($"  CAT folders copied : {catsCopied}");
            sb.AppendLine($"  Basic FBs copied   : {basicsCopied}");
            sb.AppendLine($"  HMI folders copied : {hmiCopied}");
            sb.AppendLine($"  dfbproj entries    : {registered} added");

            MapperLogger.Info(sb.ToString());
            return sb.ToString();
        }

        // ── dfbproj registration ──────────────────────────────────────────────

        private static int RegisterInDfbproj(string dfbprojPath)
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();

            var compileGroup = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "Compile").Any());
            if (compileGroup == null)
            {
                compileGroup = new XElement(ns + "ItemGroup");
                // Insert before the last Import element
                var import = xml.Root.Elements(ns + "Import").LastOrDefault();
                if (import != null) import.AddBeforeSelf(compileGroup);
                else xml.Root.Add(compileGroup);
            }

            var noneGroup = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "None").Any());
            if (noneGroup == null)
            {
                noneGroup = new XElement(ns + "ItemGroup");
                compileGroup.AddAfterSelf(noneGroup);
            }

            int adds = 0;

            void Ensure(XElement group, XElement entry)
            {
                var inc = (string?)entry.Attribute("Include") ?? "";
                if (!group.Elements().Any(x => string.Equals(
                    (string?)x.Attribute("Include"), inc, StringComparison.OrdinalIgnoreCase)))
                {
                    group.Add(entry);
                    adds++;
                }
            }

            // Register each CAT type
            foreach (var (catName, basicFb) in CatToBasicFb)
            {
                var hmiName = catName + "_HMI";

                // Basic FB at root
                Ensure(compileGroup, new XElement(ns + "Compile",
                    new XAttribute("Include", basicFb),
                    new XElement(ns + "IEC61499Type", "Basic")));

                // CAT .fbt
                Ensure(compileGroup, new XElement(ns + "Compile",
                    new XAttribute("Include", $@"{catName}\{catName}.fbt"),
                    new XElement(ns + "IEC61499Type", "CAT")));

                // CAT _HMI.fbt
                Ensure(compileGroup, new XElement(ns + "Compile",
                    new XAttribute("Include", $@"{catName}\{hmiName}.fbt"),
                    new XElement(ns + "IEC61499Type", "CAT"),
                    new XElement(ns + "DependentUpon", $@"{catName}\{catName}.fbt"),
                    new XElement(ns + "Usage", "Private")));

                // .cfg
                Ensure(noneGroup, new XElement(ns + "None",
                    new XAttribute("Include", $@"{catName}\{catName}.cfg"),
                    new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                    new XElement(ns + "IEC61499Type", "CAT")));

                // _CAT.offline.xml
                Ensure(noneGroup, new XElement(ns + "None",
                    new XAttribute("Include", $@"{catName}\{catName}_CAT.offline.xml"),
                    new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                    new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                    new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));

                // _CAT.opcua.xml
                Ensure(noneGroup, new XElement(ns + "None",
                    new XAttribute("Include", $@"{catName}\{catName}_CAT.opcua.xml"),
                    new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                    new XElement(ns + "Plugin", "OPCUAConfigurator"),
                    new XElement(ns + "IEC61499Type", "CAT_OPCUA")));

                // _HMI.meta.xml
                Ensure(noneGroup, new XElement(ns + "None",
                    new XAttribute("Include", $@"{catName}\{hmiName}.meta.xml"),
                    new XElement(ns + "DependentUpon", $"{hmiName}.fbt")));

                // _HMI.offline.xml
                Ensure(noneGroup, new XElement(ns + "None",
                    new XAttribute("Include", $@"{catName}\{hmiName}.offline.xml"),
                    new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                    new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                    new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));

                // _HMI.opcua.xml
                Ensure(noneGroup, new XElement(ns + "None",
                    new XAttribute("Include", $@"{catName}\{hmiName}.opcua.xml"),
                    new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                    new XElement(ns + "Plugin", "OPCUAConfigurator"),
                    new XElement(ns + "IEC61499Type", "CAT_OPCUA")));
            }

            xml.Save(dfbprojPath);
            MapperLogger.Info($"[TemplatePackager] dfbproj: {adds} entries added.");
            return adds;
        }

        // ── File helpers ──────────────────────────────────────────────────────

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                var destFile = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: false);
            }
        }
    }
}