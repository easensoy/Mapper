using System;
using System.Linq;
using System.Xml.Linq;

namespace MapperUI.Services
{
    public static class DfbprojRegistrar
    {
        public static int RegisterCat(string dfbprojPath, string catName)
        {
            var hmi = catName + "_HMI";
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, ng) = Groups(xml, ns);
            int a = 0;

            Add(cg, ns, "Compile", $@"{catName}\{catName}.fbt", ref a,
                new XElement(ns + "IEC61499Type", "CAT"));

            Add(cg, ns, "Compile", $@"{catName}\{hmi}.fbt", ref a,
                new XElement(ns + "IEC61499Type", "CAT"),
                new XElement(ns + "DependentUpon", $@"{catName}\{catName}.fbt"),
                new XElement(ns + "Usage", "Private"));

            Add(ng, ns, "None", $@"{catName}\{catName}.cfg", ref a,
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "IEC61499Type", "CAT"));

            Add(ng, ns, "None", $@"{catName}\{catName}_CAT.offline.xml", ref a,
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                new XElement(ns + "IEC61499Type", "CAT_OFFLINE"));

            Add(ng, ns, "None", $@"{catName}\{catName}_CAT.opcua.xml", ref a,
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "Plugin", "OPCUAConfigurator"),
                new XElement(ns + "IEC61499Type", "CAT_OPCUA"));

            Add(ng, ns, "None", $@"{catName}\{hmi}.meta.xml", ref a,
                new XElement(ns + "DependentUpon", $"{hmi}.fbt"));

            Add(ng, ns, "None", $@"{catName}\{hmi}.offline.xml", ref a,
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                new XElement(ns + "IEC61499Type", "CAT_OFFLINE"));

            Add(ng, ns, "None", $@"{catName}\{hmi}.opcua.xml", ref a,
                new XElement(ns + "DependentUpon", $"{catName}.fbt"),
                new XElement(ns + "Plugin", "OPCUAConfigurator"),
                new XElement(ns + "IEC61499Type", "CAT_OPCUA"));

            xml.Save(dfbprojPath);
            return a;
        }

        public static int RegisterBasicFb(string dfbprojPath, string fileName, string type = "Basic")
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, _) = Groups(xml, ns);
            int a = 0;
            Add(cg, ns, "Compile", fileName, ref a, new XElement(ns + "IEC61499Type", type));
            xml.Save(dfbprojPath);
            return a;
        }

        static (XElement cg, XElement ng) Groups(XDocument xml, XNamespace ns)
        {
            var cg = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "Compile").Any())
                     ?? AddGroup(xml, ns);
            var ng = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "None").Any())
                     ?? AddGroup(xml, ns);
            return (cg, ng);
        }

        static XElement AddGroup(XDocument xml, XNamespace ns)
        {
            var g = new XElement(ns + "ItemGroup");
            xml.Root!.Add(g);
            return g;
        }

        static void Add(XElement group, XNamespace ns, string tag, string include, ref int count, params XElement[] children)
        {
            if (group.Elements(ns + tag).Any(e =>
                string.Equals((string?)e.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase)))
                return;
            var el = new XElement(ns + tag, new XAttribute("Include", include));
            foreach (var c in children) el.Add(c);
            group.Add(el);
            count++;
        }
    }
}