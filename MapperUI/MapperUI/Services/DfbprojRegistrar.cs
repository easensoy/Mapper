using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>
        /// Registers a DataType (.dt) file. EAE expects:
        ///   <Compile Include="DataType\Component_State.dt"><IEC61499Type>DataType</IEC61499Type></Compile>
        /// Without this entry the compiler reports ERR_NO_SUCH_TYPE on every FB that
        /// references the type, even though the .dt file is present on disk.
        /// </summary>
        public static int RegisterDataType(string dfbprojPath, string dtRelativePath)
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, _) = Groups(xml, ns);
            int a = 0;
            Add(cg, ns, "Compile", dtRelativePath, ref a, new XElement(ns + "IEC61499Type", "DataType"));
            xml.Save(dfbprojPath);
            return a;
        }

        /// <summary>
        /// Registers an SE library reference like:
        ///   &lt;Reference Include="SE.DPAC"&gt;&lt;Version&gt;24.1.0.33&lt;/Version&gt;&lt;/Reference&gt;
        /// SE library types (DPAC_FULLINIT in SE.DPAC, plcStart in SE.AppBase) are not local
        /// .fbt files — the EAE compiler resolves them via these &lt;Reference&gt; entries.
        /// Without the reference, FBs of those types fail with ERR_NO_SUCH_TYPE even though
        /// they appear in the syslay/sysres XML correctly. Idempotent: existing reference
        /// to the same library is left alone (Version is not overwritten so a hand-set
        /// pinned version is preserved).
        /// </summary>
        public static int RegisterReference(string dfbprojPath, string libraryName, string version)
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();

            var refGroup = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "Reference").Any())
                ?? AddGroup(xml, ns);

            var existing = refGroup.Elements(ns + "Reference").FirstOrDefault(e =>
                string.Equals((string?)e.Attribute("Include"), libraryName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return 0;

            refGroup.Add(new XElement(ns + "Reference",
                new XAttribute("Include", libraryName),
                new XElement(ns + "Version", version)));
            xml.Save(dfbprojPath);
            return 1;
        }

        /// <summary>
        /// Sweeps the IEC61499 folder for any .dt, .adp, or .fbt file that is not yet
        /// registered in the project, and adds the appropriate &lt;Compile&gt; entry. This is the
        /// safety-net pass run after CAT/Basic/Adapter/DataType deployment so an external
        /// drop of a file is still picked up by the compiler.
        /// </summary>
        public static int SweepIec61499Folder(string dfbprojPath, string iec61499Dir)
        {
            if (!File.Exists(dfbprojPath) || !Directory.Exists(iec61499Dir)) return 0;
            int added = 0;

            // Top-level .dt files belong under the conventional DataType subfolder, but EAE
            // also accepts them at the IEC61499 root. Pick up both.
            foreach (var dt in Directory.EnumerateFiles(iec61499Dir, "*.dt", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(iec61499Dir, dt).Replace('/', '\\');
                added += RegisterDataType(dfbprojPath, rel);
            }
            foreach (var adp in Directory.EnumerateFiles(iec61499Dir, "*.adp", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(adp);
                added += RegisterBasicFb(dfbprojPath, name, "Adapter");
            }
            // .fbt at root only — CAT folders are already handled by RegisterCat.
            foreach (var fbt in Directory.EnumerateFiles(iec61499Dir, "*.fbt", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(fbt);
                // Heuristic: composite if a same-stem .composite.offline.xml exists, else Basic.
                var stem = Path.GetFileNameWithoutExtension(name);
                bool isComposite = File.Exists(Path.Combine(iec61499Dir, stem + ".composite.offline.xml"));
                added += RegisterBasicFb(dfbprojPath, name, isComposite ? "Composite" : "Basic");
            }
            return added;
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