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
            string templateFbtPath, string newCatName,
            string dfbprojPath, string componentName)
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
            MapperLogger.Info($"[CatClone] Template: {templateBaseName}  New: {newCatName}  Target: {targetDir}");

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
                GenerateEmptyOpcua, ref copied, ref skipped, ref generated);
            CopyOrFallback(templateDir, templateBaseName, targetDir, newCatName, "_HMI.meta.xml",
                () => string.Empty, ref copied, ref skipped, ref generated);
            CopyOrFallback(templateDir, templateBaseName, targetDir, newCatName, "_HMI.offline.xml",
                GenerateOfflineXml, ref copied, ref skipped, ref generated);
            CopyWithRename(templateDir, templateBaseName, targetDir, newCatName, "_HMI.opcua.xml", ref copied, ref skipped);

            int registered = RegisterInDfbproj(dfbprojPath, newCatName, hmiName);
            File.SetLastWriteTime(dfbprojPath, DateTime.Now);

            var sb = new StringBuilder();
            sb.AppendLine($"{newCatName} created. copied={copied} generated={generated} skipped={skipped} dfbproj+={registered}");
            MapperLogger.Info(sb.ToString());
            return sb.ToString();
        }

        private static void GenerateFbt(string tDir, string tBase, string outDir, string name, string comp,
            ref int copied, ref int skipped, ref int generated)
        {
            var target = Path.Combine(outDir, $"{name}.fbt");
            if (File.Exists(target)) { skipped++; return; }
            var source = Path.Combine(tDir, $"{tBase}.fbt");
            if (!File.Exists(source)) throw new FileNotFoundException($"Template .fbt not found: {source}");

            var doc = XDocument.Load(source);
            var root = doc.Root!;
            root.SetAttributeValue("Name", name);
            root.SetAttributeValue("GUID", Guid(name));
            root.SetAttributeValue("Comment", $"Function Block for {comp}");

            var vi = root.Elements().FirstOrDefault(e => e.Name.LocalName == "VersionInfo");
            if (vi != null) { vi.SetAttributeValue("Author", "alper_sensoy"); vi.SetAttributeValue("Date", DateTime.Now.ToString("M/d/yyyy")); vi.SetAttributeValue("Remarks", $"Generated from VueOne component: {comp}"); }

            foreach (var fb in root.Descendants().Where(e => e.Name.LocalName == "FB"))
                if ((string?)fb.Attribute("Name") == "IThis" && (string?)fb.Attribute("Type") == tBase + "_HMI")
                    fb.SetAttributeValue("Type", name + "_HMI");

            Save(doc, target); generated++;
            MapperLogger.Info($"[CatClone]  Generated {name}.fbt");
        }

        private static void GenerateHmiFbt(string tDir, string tBase, string outDir, string name, string comp,
            ref int copied, ref int skipped, ref int generated)
        {
            var hmi = name + "_HMI";
            var target = Path.Combine(outDir, $"{hmi}.fbt");
            if (File.Exists(target)) { skipped++; return; }
            var source = Path.Combine(tDir, $"{tBase}_HMI.fbt");
            if (!File.Exists(source)) throw new FileNotFoundException($"Template _HMI.fbt not found: {source}");

            var doc = XDocument.Load(source);
            var root = doc.Root!;
            root.SetAttributeValue("Name", hmi);
            root.SetAttributeValue("GUID", Guid(hmi));
            var vi = root.Elements().FirstOrDefault(e => e.Name.LocalName == "VersionInfo");
            if (vi != null) { vi.SetAttributeValue("Author", "alper_sensoy"); vi.SetAttributeValue("Date", DateTime.Now.ToString("M/d/yyyy")); vi.SetAttributeValue("Remarks", $"Generated from VueOne component: {comp}"); }

            Save(doc, target); generated++;
            MapperLogger.Info($"[CatClone]  Generated {hmi}.fbt");
        }

        private static void GenerateCfg(string tDir, string tBase, string outDir, string name,
            ref int copied, ref int skipped, ref int generated)
        {
            var target = Path.Combine(outDir, $"{name}.cfg");
            if (File.Exists(target)) { skipped++; return; }
            var source = Path.Combine(tDir, $"{tBase}.cfg");
            if (!File.Exists(source)) throw new FileNotFoundException($"Template .cfg not found: {source}");

            var doc = XDocument.Load(source);
            var root = doc.Root!;
            XNamespace ns = root.GetDefaultNamespace();

            root.SetAttributeValue("Name", name);
            root.SetAttributeValue("CATFile", name + "\\" + name + ".fbt");

            foreach (var hmi in root.Elements(ns + "HMIInterface").Concat(root.Elements("HMIInterface")))
            {
                var fn = hmi.Attribute("FileName");
                if (fn != null) fn.Value = name + "\\" + Path.GetFileName(fn.Value).Replace(tBase, name);
            }

            foreach (var p in root.Elements(ns + "Plugin").Concat(root.Elements("Plugin")))
            {
                var v = p.Attribute("Value");
                if (v != null) v.Value = name + "\\" + Path.GetFileName(v.Value).Replace(tBase, name);
            }

            Save(doc, target); generated++;
            MapperLogger.Info($"[CatClone]  Generated {name}.cfg");
        }

        private static int RegisterInDfbproj(string path, string cat, string hmi)
        {
            var xml = XDocument.Load(path);
            var ns = xml.Root!.GetDefaultNamespace();
            var cg = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "Compile").Any())
                ?? AddGroup(xml, ns);
            var ng = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "None").Any())
                ?? AddGroup(xml, ns);

            int a = 0;
            void E(XElement g, XElement e) { var i = (string?)e.Attribute("Include") ?? ""; if (!g.Elements().Any(x => string.Equals((string?)x.Attribute("Include"), i, StringComparison.OrdinalIgnoreCase))) { g.Add(e); a++; } }

            E(cg, new XElement(ns + "Compile", new XAttribute("Include", $@"{cat}\{cat}.fbt"), new XElement(ns + "IEC61499Type", "CAT")));
            E(cg, new XElement(ns + "Compile", new XAttribute("Include", $@"{cat}\{hmi}.fbt"), new XElement(ns + "IEC61499Type", "CAT"), new XElement(ns + "DependentUpon", $@"{cat}\{cat}.fbt"), new XElement(ns + "Usage", "Private")));
            E(ng, new XElement(ns + "None", new XAttribute("Include", $@"{cat}\{cat}.cfg"), new XElement(ns + "DependentUpon", $"{cat}.fbt"), new XElement(ns + "IEC61499Type", "CAT")));
            E(ng, new XElement(ns + "None", new XAttribute("Include", $@"{cat}\{cat}.composite.offline.xml"), new XElement(ns + "DependentUpon", $"{cat}.fbt")));
            E(ng, new XElement(ns + "None", new XAttribute("Include", $@"{cat}\{cat}.doc.xml"), new XElement(ns + "DependentUpon", $"{cat}.fbt")));
            E(ng, new XElement(ns + "None", new XAttribute("Include", $@"{cat}\{cat}.meta.xml"), new XElement(ns + "DependentUpon", $"{cat}.fbt")));
            E(ng, new XElement(ns + "None", new XAttribute("Include", $@"{cat}\{cat}_CAT.offline.xml"), new XElement(ns + "DependentUpon", $"{cat}.fbt"), new XElement(ns + "Plugin", "OfflineParametrizationEditor"), new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));
            E(ng, new XElement(ns + "None", new XAttribute("Include", $@"{cat}\{cat}_CAT.opcua.xml"), new XElement(ns + "DependentUpon", $"{cat}.fbt"), new XElement(ns + "Plugin", "OPCUAConfigurator"), new XElement(ns + "IEC61499Type", "CAT_OPCUA")));
            E(ng, new XElement(ns + "None", new XAttribute("Include", $@"{cat}\{hmi}.meta.xml"), new XElement(ns + "DependentUpon", $"{hmi}.fbt")));
            E(ng, new XElement(ns + "None", new XAttribute("Include", $@"{cat}\{hmi}.offline.xml"), new XElement(ns + "DependentUpon", $"{cat}.fbt"), new XElement(ns + "Plugin", "OfflineParametrizationEditor"), new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));
            E(ng, new XElement(ns + "None", new XAttribute("Include", $@"{cat}\{hmi}.opcua.xml"), new XElement(ns + "DependentUpon", $"{cat}.fbt"), new XElement(ns + "Plugin", "OPCUAConfigurator"), new XElement(ns + "IEC61499Type", "CAT_OPCUA")));

            xml.Save(path);
            MapperLogger.Info($"[CatClone] dfbproj: {a} entries added.");
            return a;
        }

        private static XElement AddGroup(XDocument xml, XNamespace ns) { var g = new XElement(ns + "ItemGroup"); xml.Root!.Add(g); return g; }

        private static void CopyOrFallback(string tDir, string tBase, string outDir, string name, string suffix, Func<string> fb,
            ref int copied, ref int skipped, ref int generated)
        {
            var target = Path.Combine(outDir, $"{name}{suffix}");
            if (File.Exists(target)) { skipped++; return; }
            var source = Path.Combine(tDir, $"{tBase}{suffix}");
            if (File.Exists(source)) { File.Copy(source, target); copied++; }
            else { File.WriteAllText(target, fb(), Encoding.UTF8); generated++; }
        }

        private static void CopyWithRename(string tDir, string tBase, string outDir, string name, string suffix, ref int copied, ref int skipped)
        {
            var target = Path.Combine(outDir, $"{name}{suffix}");
            if (File.Exists(target)) { skipped++; return; }
            var source = Path.Combine(tDir, $"{tBase}{suffix}");
            if (File.Exists(source)) { File.Copy(source, target); copied++; }
        }

        private static string GenerateDocXml(string n, string c) =>
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<section xmlns=\"http://docbook.org/ns/docbook\" xmlns:xi=\"http://www.w3.org/2001/XInclude\" xmlns:xlink=\"http://www.w3.org/1999/xlink\">\r\n  <info><author><personname><firstname>Alper</firstname><surname>Sensoy</surname></personname></author>\r\n  <abstract><para>{n} — generated by VueOne Mapper for {c}</para></abstract></info>\r\n  <para></para>\r\n</section>";

        private static string GenerateOfflineXml() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<OfflineParameterModel xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" IsDefaultEventSelectionDialogsHidden=\"0\" />";

        private static string GenerateEmptyOpcua() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<OPCUAComplexObject xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" />";

        private static void Save(XDocument doc, string path) { using var sw = new StreamWriter(path, false, new UTF8Encoding(true)); doc.Save(sw); }

        private static string Guid(string name)
        {
            using var sha = SHA256.Create();
            var b = sha.ComputeHash(Encoding.UTF8.GetBytes(name));
            return $"{b[0]:x2}{b[1]:x2}{b[2]:x2}{b[3]:x2}-{b[4]:x2}{b[5]:x2}-{b[6]:x2}{b[7]:x2}-{b[8]:x2}{b[9]:x2}-{b[10]:x2}{b[11]:x2}{b[12]:x2}{b[13]:x2}{b[14]:x2}{b[15]:x2}";
        }
    }
}