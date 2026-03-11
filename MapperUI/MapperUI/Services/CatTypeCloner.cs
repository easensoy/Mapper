// MapperUI/MapperUI/Services/CatTypeCloner.cs
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace MapperUI.Services
{
    public static class CatTypeCloner
    {
        public static string Clone(
            string templateFbtPath,
            string newCatName,
            string dfbprojPath,
            string componentName)
        {
            if (!File.Exists(templateFbtPath))
                throw new FileNotFoundException($"Template .fbt not found:\n{templateFbtPath}");
            if (!File.Exists(dfbprojPath))
                throw new FileNotFoundException($"IEC61499.dfbproj not found:\n{dfbprojPath}");

            var templateDir = Path.GetDirectoryName(templateFbtPath)!;
            var templateBaseName = Path.GetFileNameWithoutExtension(templateFbtPath);
            var iec61499Dir = Path.GetDirectoryName(dfbprojPath)!;
            var targetDir = Path.Combine(iec61499Dir, newCatName);
            var hmiName = newCatName + "_HMI";

            Directory.CreateDirectory(targetDir);

            MapperLogger.Info($"[CatClone] Template  : {templateBaseName}");
            MapperLogger.Info($"[CatClone] New type  : {newCatName}");
            MapperLogger.Info($"[CatClone] Target    : {targetDir}");

            int copied = 0, generated = 0, skipped = 0;

            GenerateFbt(templateDir, templateBaseName, targetDir, newCatName, componentName, ref copied, ref skipped, ref generated);
            GenerateHmiFbt(templateDir, templateBaseName, targetDir, newCatName, componentName, ref copied, ref skipped, ref generated);
            GenerateCfg(templateDir, templateBaseName, targetDir, newCatName, ref copied, ref skipped, ref generated);
            CopyOrFallback(templateDir, templateBaseName, targetDir, newCatName, ".composite.offline.xml",
                () => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<CompositeFBTypeCompileInfo>\r\n  <Signature />\r\n</CompositeFBTypeCompileInfo>",
                ref copied, ref skipped, ref generated);
            CopyOrFallback(templateDir, templateBaseName, targetDir, newCatName, ".doc.xml",
                () => GenerateDocXml(newCatName, componentName), ref copied, ref skipped, ref generated);
            CopyOrFallback(templateDir, templateBaseName, targetDir, newCatName, ".meta.xml",
                () => string.Empty, ref copied, ref skipped, ref generated);
            CopyOrFallback(templateDir, templateBaseName, targetDir, newCatName, "_CAT.offline.xml",
                GenerateOfflineXml, ref copied, ref skipped, ref generated);
            CopyOrFallback(templateDir, templateBaseName, targetDir, newCatName, "_CAT.opcua.xml",
                GenerateEmptyOpcuaComplex, ref copied, ref skipped, ref generated);
            CopyOrFallback(templateDir, templateBaseName, targetDir, newCatName, "_HMI.meta.xml",
                () => string.Empty, ref copied, ref skipped, ref generated);
            CopyOrFallback(templateDir, templateBaseName, targetDir, newCatName, "_HMI.offline.xml",
                GenerateOfflineXml, ref copied, ref skipped, ref generated);
            CopyWithRename(templateDir, templateBaseName, targetDir, newCatName, "_HMI.opcua.xml", ref copied, ref skipped);

            int registered = RegisterInDfbproj(dfbprojPath, newCatName, hmiName);

            File.SetLastWriteTime(dfbprojPath, DateTime.Now);
            MapperLogger.Touch($"Touched {Path.GetFileName(dfbprojPath)}");

            var sb = new StringBuilder();
            sb.AppendLine($"{newCatName} created successfully.");
            sb.AppendLine($"  Target folder   : {targetDir}");
            sb.AppendLine($"  Files copied    : {copied}");
            sb.AppendLine($"  Files generated : {generated}");
            sb.AppendLine($"  Files skipped   : {skipped} (already present)");
            sb.AppendLine($"  dfbproj entries : {registered} added");
            MapperLogger.Info(sb.ToString());
            return sb.ToString();
        }

        private static void GenerateFbt(string templateDir, string templateBaseName,
            string targetDir, string newCatName, string componentName,
            ref int copied, ref int skipped, ref int generated)
        {
            var targetPath = Path.Combine(targetDir, $"{newCatName}.fbt");
            if (File.Exists(targetPath)) { skipped++; return; }
            var sourcePath = Path.Combine(templateDir, $"{templateBaseName}.fbt");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Template .fbt not found: {sourcePath}");

            var doc = XDocument.Load(sourcePath);
            var root = doc.Root!;
            root.SetAttributeValue("Name", newCatName);
            root.SetAttributeValue("GUID", DeterministicGuid(newCatName));
            root.SetAttributeValue("Comment", $"Function Block for {componentName}");

            var vi = root.Elements().FirstOrDefault(e => e.Name.LocalName == "VersionInfo");
            if (vi != null)
            {
                vi.SetAttributeValue("Author", "alper_sensoy");
                vi.SetAttributeValue("Date", DateTime.Now.ToString("M/d/yyyy"));
                vi.SetAttributeValue("Remarks", $"Generated from VueOne component: {componentName}");
            }

            var oldHmiType = templateBaseName + "_HMI";
            var newHmiType = newCatName + "_HMI";
            foreach (var fb in root.Descendants().Where(e => e.Name.LocalName == "FB"))
            {
                if ((string?)fb.Attribute("Name") == "IThis" &&
                    (string?)fb.Attribute("Type") == oldHmiType)
                    fb.SetAttributeValue("Type", newHmiType);
            }

            SaveXml(doc, targetPath);
            generated++;
            MapperLogger.Info($"[CatClone]  Generated {newCatName}.fbt");
        }

        private static void GenerateHmiFbt(string templateDir, string templateBaseName,
            string targetDir, string newCatName, string componentName,
            ref int copied, ref int skipped, ref int generated)
        {
            var hmiName = newCatName + "_HMI";
            var targetPath = Path.Combine(targetDir, $"{hmiName}.fbt");
            if (File.Exists(targetPath)) { skipped++; return; }
            var sourcePath = Path.Combine(templateDir, $"{templateBaseName}_HMI.fbt");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Template _HMI.fbt not found: {sourcePath}");

            var doc = XDocument.Load(sourcePath);
            var root = doc.Root!;
            root.SetAttributeValue("Name", hmiName);
            root.SetAttributeValue("GUID", DeterministicGuid(hmiName));

            var vi = root.Elements().FirstOrDefault(e => e.Name.LocalName == "VersionInfo");
            if (vi != null)
            {
                vi.SetAttributeValue("Author", "alper_sensoy");
                vi.SetAttributeValue("Date", DateTime.Now.ToString("M/d/yyyy"));
                vi.SetAttributeValue("Remarks", $"Generated from VueOne component: {componentName}");
            }

            SaveXml(doc, targetPath);
            generated++;
            MapperLogger.Info($"[CatClone]  Generated {hmiName}.fbt");
        }

        private static void GenerateCfg(string templateDir, string templateBaseName,
            string targetDir, string newCatName,
            ref int copied, ref int skipped, ref int generated)
        {
            var targetPath = Path.Combine(targetDir, $"{newCatName}.cfg");
            if (File.Exists(targetPath)) { skipped++; return; }
            var sourcePath = Path.Combine(templateDir, $"{templateBaseName}.cfg");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Template .cfg not found: {sourcePath}");

            var doc = XDocument.Load(sourcePath);
            var root = doc.Root!;
            XNamespace ns = root.GetDefaultNamespace();

            root.SetAttributeValue("Name", newCatName);
            root.SetAttributeValue("CATFile", newCatName + "\\" + newCatName + ".fbt");

            foreach (var hmi in root.Elements(ns + "HMIInterface").Concat(root.Elements("HMIInterface")))
            {
                var fn = hmi.Attribute("FileName");
                if (fn == null) continue;
                var origFile = Path.GetFileName(fn.Value);
                fn.Value = newCatName + "\\" + origFile.Replace(templateBaseName, newCatName);
            }

            foreach (var plugin in root.Elements(ns + "Plugin").Concat(root.Elements("Plugin")))
            {
                var val = plugin.Attribute("Value");
                if (val == null) continue;
                var origFile = Path.GetFileName(val.Value);
                val.Value = newCatName + "\\" + origFile.Replace(templateBaseName, newCatName);
            }

            SaveXml(doc, targetPath);
            generated++;
            MapperLogger.Info($"[CatClone]  Generated {newCatName}.cfg (FIXED cross-references)");
        }

        private static int RegisterInDfbproj(string dfbprojPath, string catName, string hmiName)
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();

            var compileGroup = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "Compile").Any());
            if (compileGroup == null) { compileGroup = new XElement(ns + "ItemGroup"); xml.Root.Add(compileGroup); }

            var noneGroup = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "None").Any());
            if (noneGroup == null) { noneGroup = new XElement(ns + "ItemGroup"); xml.Root.Add(noneGroup); }

            int adds = 0;
            void Ensure(XElement group, XElement entry)
            {
                var inc = (string?)entry.Attribute("Include") ?? "";
                if (!group.Elements().Any(e => string.Equals((string?)e.Attribute("Include"), inc, StringComparison.OrdinalIgnoreCase)))
                { group.Add(entry); adds++; }
            }

            Ensure(compileGroup, new XElement(ns + "Compile",
                new XAttribute("Include", $@"{catName}\{catName}.fbt"),
                new XElement(ns + "IEC61499Type", "CAT")));

            Ensure(compileGroup, new XElement(ns + "Compile",
                new XAttribute("Include", $@"{catName}\{hmiName}.fbt"),
                new XElement(ns + "IEC61499Type", "CAT"),
                new XElement(ns + "DependentUpon", $@"{catName}\{catName}.fbt"),
                new XElement(ns + "Usage", "Private")));

            Ensure(noneGroup, new XElement(ns + "None",
                new XAttribute("Include", $@"{catName}\{catName}.cfg"),
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "IEC61499Type", "CAT")));

            Ensure(noneGroup, new XElement(ns + "None",
                new XAttribute("Include", $@"{catName}\{catName}.composite.offline.xml"),
                new XElement(ns + "DependentUpon", $"{catName}.fbt")));

            Ensure(noneGroup, new XElement(ns + "None",
                new XAttribute("Include", $@"{catName}\{catName}.doc.xml"),
                new XElement(ns + "DependentUpon", $"{catName}.fbt")));

            Ensure(noneGroup, new XElement(ns + "None",
                new XAttribute("Include", $@"{catName}\{catName}.meta.xml"),
                new XElement(ns + "DependentUpon", $"{catName}.fbt")));

            Ensure(noneGroup, new XElement(ns + "None",
                new XAttribute("Include", $@"{catName}\{catName}_CAT.offline.xml"),
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));

            Ensure(noneGroup, new XElement(ns + "None",
                new XAttribute("Include", $@"{catName}\{catName}_CAT.opcua.xml"),
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "Plugin", "OPCUAConfigurator"),
                new XElement(ns + "IEC61499Type", "CAT_OPCUA")));

            Ensure(noneGroup, new XElement(ns + "None",
                new XAttribute("Include", $@"{catName}\{hmiName}.meta.xml"),
                new XElement(ns + "DependentUpon", $"{hmiName}.fbt")));

            Ensure(noneGroup, new XElement(ns + "None",
                new XAttribute("Include", $@"{catName}\{hmiName}.offline.xml"),
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));

            Ensure(noneGroup, new XElement(ns + "None",
                new XAttribute("Include", $@"{catName}\{hmiName}.opcua.xml"),
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "Plugin", "OPCUAConfigurator"),
                new XElement(ns + "IEC61499Type", "CAT_OPCUA")));

            xml.Save(dfbprojPath);
            MapperLogger.Info($"[CatClone] dfbproj saved — {adds} new entries added.");
            return adds;
        }

        private static void CopyOrFallback(string templateDir, string templateBaseName,
            string targetDir, string newCatName, string suffix, Func<string> fallback,
            ref int copied, ref int skipped, ref int generated)
        {
            var targetPath = Path.Combine(targetDir, $"{newCatName}{suffix}");
            if (File.Exists(targetPath)) { skipped++; return; }
            var sourcePath = Path.Combine(templateDir, $"{templateBaseName}{suffix}");
            if (File.Exists(sourcePath)) { File.Copy(sourcePath, targetPath); copied++; }
            else { File.WriteAllText(targetPath, fallback(), Encoding.UTF8); generated++; }
            MapperLogger.Info($"[CatClone]  {(File.Exists(sourcePath) ? "Copied" : "Generated")} {newCatName}{suffix}");
        }

        private static void CopyWithRename(string templateDir, string templateBaseName,
            string targetDir, string newCatName, string suffix, ref int copied, ref int skipped)
        {
            var targetPath = Path.Combine(targetDir, $"{newCatName}{suffix}");
            if (File.Exists(targetPath)) { skipped++; return; }
            var sourcePath = Path.Combine(templateDir, $"{templateBaseName}{suffix}");
            if (File.Exists(sourcePath)) { File.Copy(sourcePath, targetPath); copied++; }
            else MapperLogger.Warn($"[CatClone]  WARN: {templateBaseName}{suffix} not found.");
        }

        private static string GenerateDocXml(string fbName, string componentName) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<section xmlns=\"http://docbook.org/ns/docbook\"\r\n" +
            "  xmlns:xi=\"http://www.w3.org/2001/XInclude\" xmlns:xlink=\"http://www.w3.org/1999/xlink\">\r\n" +
            "  <info><author><personname><firstname>Alper</firstname><surname>Sensoy</surname></personname></author>\r\n" +
            $"  <abstract><para>{fbName} — generated by VueOne Mapper for {componentName}</para></abstract></info>\r\n" +
            "  <para></para>\r\n</section>";

        private static string GenerateOfflineXml() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<OfflineParameterModel xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" IsDefaultEventSelectionDialogsHidden=\"0\" />";

        private static string GenerateEmptyOpcuaComplex() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<OPCUAComplexObject xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" />";

        private static void SaveXml(XDocument doc, string path)
        {
            using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
            doc.Save(sw);
        }

        private static string DeterministicGuid(string name)
        {
            using var sha = SHA256.Create();
            var b = sha.ComputeHash(Encoding.UTF8.GetBytes(name));
            return $"{b[0]:x2}{b[1]:x2}{b[2]:x2}{b[3]:x2}-{b[4]:x2}{b[5]:x2}-{b[6]:x2}{b[7]:x2}-" +
                   $"{b[8]:x2}{b[9]:x2}-{b[10]:x2}{b[11]:x2}{b[12]:x2}{b[13]:x2}{b[14]:x2}{b[15]:x2}";
        }
    }
}