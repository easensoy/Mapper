using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
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

            // Save only on a real change (an unconditional save bumps the mtime -> spurious EAE
            // "Reload Solution" prompt).
            if (a > 0) xml.Save(dfbprojPath);
            return a;
        }

        // Registers a hardware-device CAT type folder (e.g. the EtherNet/IP coupler the BX1 .hcf scanner
        // instantiates). Unlike RegisterCat it does NOT register the actuator-CAT siblings (offline/
        // opcua/HMI xml) — a hardware type has none, and registering missing files = Missing Project Files.
        public static int RegisterHardwareDeviceCat(string dfbprojPath, string typeName)
        {
            var hmi = typeName + "_HMI";
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, ng) = Groups(xml, ns);
            int a = 0;

            Add(cg, ns, "Compile", $@"{typeName}\{typeName}.fbt", ref a,
                new XElement(ns + "IEC61499Type", "CAT"),
                new XElement(ns + "SubType", "Hardware"));

            Add(cg, ns, "Compile", $@"{typeName}\{hmi}.fbt", ref a,
                new XElement(ns + "DependentUpon", $"{typeName}.fbt"),
                new XElement(ns + "IEC61499Type", "CAT"),
                new XElement(ns + "HMI", $@"..\HMI\{typeName}\{typeName}_sDefault.cnv.cs"));

            Add(ng, ns, "None", $@"{typeName}\{typeName}.cfg", ref a,
                new XElement(ns + "DependentUpon", $"{typeName}.fbt"),
                new XElement(ns + "IEC61499Type", "CAT"));

            var fg = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "Folder").Any())
                     ?? AddGroup(xml, ns);
            Add(fg, ns, "Folder", typeName, ref a);

            if (a > 0) xml.Save(dfbprojPath);
            return a;
        }

        // Removes the entries RegisterHardwareDeviceCat added for the named type. Idempotent.
        public static int UnregisterHardwareDeviceCat(string dfbprojPath, string typeName)
        {
            if (!File.Exists(dfbprojPath)) return 0;
            var xml = XDocument.Load(dfbprojPath, LoadOptions.PreserveWhitespace);
            var ns = xml.Root!.GetDefaultNamespace();
            int removed = 0;
            var prefix = typeName + @"\";
            foreach (var name in new[] { "Compile", "None", "Folder" })
            {
                foreach (var el in xml.Descendants(ns + name).ToList())
                {
                    var inc = (string?)el.Attribute("Include");
                    if (string.IsNullOrEmpty(inc)) continue;
                    bool match = name == "Folder"
                        ? string.Equals(inc, typeName, StringComparison.OrdinalIgnoreCase)
                        : inc.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                    var nextWs = el.NextNode as XText;
                    el.Remove();
                    if (nextWs != null) nextWs.Remove();
                    removed++;
                }
            }
            if (removed > 0) xml.Save(dfbprojPath);
            return removed;
        }

        public static int RegisterBasicFb(string dfbprojPath, string fileName, string type = "Basic")
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, _) = Groups(xml, ns);
            int a = 0;
            Add(cg, ns, "Compile", fileName, ref a, new XElement(ns + "IEC61499Type", type));
            if (a > 0) xml.Save(dfbprojPath);   // only write on a real change
            return a;
        }

        // Register a DataType (.dt) as <Compile DataType>, else FBs referencing it fail ERR_NO_SUCH_TYPE.
        public static int RegisterDataType(string dfbprojPath, string dtRelativePath)
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, _) = Groups(xml, ns);
            int a = 0;
            Add(cg, ns, "Compile", dtRelativePath, ref a, new XElement(ns + "IEC61499Type", "DataType"));
            if (a > 0) xml.Save(dfbprojPath);   // only write on a real change
            return a;
        }

        // Register an SE library <Reference>, else FBs of those types fail ERR_NO_SUCH_TYPE. Idempotent
        // (an existing reference is left alone, preserving a hand-pinned Version).
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

        // Registers a .sysdev (<Compile SystemDevice>) plus its sibling .hcf/Properties.xml (<None
        // SystemDevice>, DependentUpon the .sysdev). Idempotent; de-duplicates repeated children.
        public static int RegisterSystemDevice(string dfbprojPath, string eaeProjectDir, string sysdevPath)
        {
            if (!File.Exists(dfbprojPath)) return 0;
            if (!File.Exists(sysdevPath)) return 0;

            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            var sysdevRel = Path.GetRelativePath(iec, sysdevPath).Replace('/', '\\');
            var sysdevFileName = Path.GetFileName(sysdevPath);
            var sysdevFolder = Path.Combine(
                Path.GetDirectoryName(sysdevPath)!,
                Path.GetFileNameWithoutExtension(sysdevPath));

            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, ng) = Groups(xml, ns);
            int added = 0;

            // .sysdev <Compile SystemDevice> DependentUpon the parent .system — TopologyManager binds
            // Logical Device -> System through it, else the sysdev stays orphaned and Deploy &
            // Diagnostic filters it out.
            const string SystemFileName = "00000000-0000-0000-0000-000000000000.system";
            Add(cg, ns, "Compile", sysdevRel, ref added,
                new XElement(ns + "DependentUpon", SystemFileName),
                new XElement(ns + "IEC61499Type", "SystemDevice"));

            // Siblings go under <None SystemDevice>, EXCEPT the BX1 SoftPAC (sysdev id ...0004, the only
            // EtherNet/IP-scanner PLC): its .sysres must be <Compile SystemResource> and its .hcf ALSO
            // <Content SystemDevice>, or EAE compiles no HWConfig and the Deploy export emits an EMPTY
            // scanner. M262/M580 keep the legacy <None>.
            bool isBx1Resource = sysdevFileName.StartsWith(
                "00000000-0000-0000-0000-000000000004", StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(sysdevFolder))
            {
                foreach (var sibling in Directory.EnumerateFiles(sysdevFolder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var rel = Path.GetRelativePath(iec, sibling).Replace('/', '\\');
                    var ext = Path.GetExtension(sibling).ToLowerInvariant();

                    if (ext == ".sysres" && isBx1Resource)
                    {
                        // Migrate a stale <None> registration of this .sysres to <Compile SystemResource>.
                        foreach (var stale in xml.Root!.Descendants(ns + "None")
                                     .Where(e => string.Equals((string?)e.Attribute("Include"), rel,
                                         StringComparison.OrdinalIgnoreCase)).ToList())
                        { stale.Remove(); added++; }
                        Add(cg, ns, "Compile", rel, ref added,
                            new XElement(ns + "IEC61499Type", "SystemResource"),
                            new XElement(ns + "DependentUpon", sysdevFileName));
                        continue;
                    }

                    Add(ng, ns, "None", rel, ref added,
                        new XElement(ns + "IEC61499Type", "SystemDevice"),
                        new XElement(ns + "DependentUpon", sysdevFileName));

                    if (ext == ".hcf" && isBx1Resource)
                        Add(cg, ns, "Content", rel, ref added,
                            new XElement(ns + "IEC61499Type", "SystemDevice"),
                            new XElement(ns + "DependentUpon", sysdevFileName));
                }
            }

            int removed = DeduplicateChildren(ng, ns, "None", sysdevFileName)
                        + DeduplicateChildren(cg, ns, "Compile", sysdevFileName);

            // Backfill a missing DependentUpon on an existing Compile entry (else the device disappears
            // from EAE's Deploy & Diagnostic tab).
            int backfilled = 0;
            foreach (var compile in cg.Elements(ns + "Compile"))
            {
                if (!string.Equals((string?)compile.Attribute("Include"), sysdevRel,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (compile.Elements(ns + "DependentUpon").Any()) continue;
                compile.AddFirst(new XElement(ns + "DependentUpon", SystemFileName));
                backfilled++;
            }

            // Save only on a real change (else a spurious EAE "Reload Solution" prompt).
            if (added > 0 || removed > 0 || backfilled > 0) xml.Save(dfbprojPath);
            return added;
        }

        // Inverse of RegisterSystemDevice: remove every dfbproj entry whose Include references the given
        // sysdev id (the .sysdev + everything under its <id>\ folder). Used when a device is deleted (e.g.
        // M262 when the RevPi replaces it) so EAE Solution Integrity sees no missing project files.
        // Idempotent; returns the count removed.
        public static int UnregisterSystemDevice(string dfbprojPath, string sysdevId)
        {
            if (!File.Exists(dfbprojPath) || string.IsNullOrEmpty(sysdevId)) return 0;
            var xml = XDocument.Load(dfbprojPath);
            var tags = new[] { "Compile", "None", "Content", "EmbeddedResource" };
            var stale = xml.Root!.Descendants()
                .Where(e => tags.Contains(e.Name.LocalName))
                .Where(e => ((string?)e.Attribute("Include") ?? string.Empty)
                    .Contains(sysdevId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var e in stale) e.Remove();
            if (stale.Count > 0) xml.Save(dfbprojPath);
            return stale.Count;
        }

        // Idempotently ensures the four APPLICATION dfbproj entries exist: .sysapp (SystemApplication)
        // + .syslay (SystemLayer) under <Compile>, aspmap/opcua companions under <Content>.
        public static int RegisterApplicationShell(string dfbprojPath)
        {
            if (!File.Exists(dfbprojPath)) return 0;
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, _) = Groups(xml, ns);
            int added = 0;

            const string SystemId   = "00000000-0000-0000-0000-000000000000";
            const string AppId      = "00000000-0000-0000-0000-000000000001";
            const string SystemFile = SystemId + ".system";
            const string SyslayFile = SystemId + ".syslay";
            string sysappRel = $@"System\{SystemId}\{AppId}.sysapp";
            string syslayRel = $@"System\{SystemId}\{AppId}\{SystemId}.syslay";
            string aspmapRel = $@"System\{SystemId}\{AppId}\{SystemId}\aspmap.xml";
            string opcuaRel  = $@"System\{SystemId}\{AppId}\{SystemId}\opcua.xml";

            Add(cg, ns, "Compile", sysappRel, ref added,
                new XElement(ns + "DependentUpon", SystemFile),
                new XElement(ns + "IEC61499Type", "SystemApplication"));

            Add(cg, ns, "Compile", syslayRel, ref added,
                new XElement(ns + "DependentUpon", AppId + ".sysapp"),
                new XElement(ns + "IEC61499Type", "SystemLayer"));

            // aspmap/opcua go under <Content>.
            var content = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "Content").Any()) ?? AddGroup(xml, ns);

            Add(content, ns, "Content", aspmapRel, ref added,
                new XElement(ns + "DependentUpon", SyslayFile),
                new XElement(ns + "Plugin", "AvevaServerPlugin"),
                new XElement(ns + "IEC61499Type", "CAT_ASPMAP"));

            Add(content, ns, "Content", opcuaRel, ref added,
                new XElement(ns + "DependentUpon", SyslayFile),
                new XElement(ns + "Plugin", "OPCUAConfigurator"),
                new XElement(ns + "IEC61499Type", "CAT_OPCUA"));

            if (added > 0) xml.Save(dfbprojPath);
            return added;
        }

        // Strips every Content/None/Compile entry whose Include references a sysres-stem directory (or
        // .sysres file) absent on disk, so EAE's Solution Integrity doesn't flag them as missing.
        public static int StripStaleSysresStemEntries(string dfbprojPath, string eaeProjectDir)
        {
            if (!File.Exists(dfbprojPath) || !Directory.Exists(eaeProjectDir)) return 0;
            var systemDir = Path.Combine(eaeProjectDir, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return 0;

            var liveStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sysres in Directory.EnumerateFiles(systemDir, "*.sysres",
                         SearchOption.AllDirectories))
            {
                liveStems.Add(Path.GetFileNameWithoutExtension(sysres));
            }

            var xml = XDocument.Load(dfbprojPath, LoadOptions.PreserveWhitespace);
            var ns = xml.Root!.GetDefaultNamespace();
            int removed = 0;
            var stemRx = new System.Text.RegularExpressions.Regex(
                @"\\([0-9A-Fa-f]{14,17})\\",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            var candidates = new System.Collections.Generic.List<XElement>();
            foreach (var name in new[] { "Content", "None", "Compile" })
            {
                candidates.AddRange(xml.Descendants(ns + name));
            }
            foreach (var el in candidates)
            {
                var include = (string?)el.Attribute("Include");
                if (string.IsNullOrEmpty(include)) continue;
                if (!include.Contains("System\\", StringComparison.OrdinalIgnoreCase)) continue;

                string? stem = null;
                if (include.EndsWith(".sysres", StringComparison.OrdinalIgnoreCase))
                {
                    // A .sysres FILE entry (the directory-stem regex misses the filename stem).
                    stem = Path.GetFileNameWithoutExtension(include);
                }
                else
                {
                    var m = stemRx.Match(include);  // a sister-folder ref (…\<stem>\opcua.xml etc.)
                    if (m.Success) stem = m.Groups[1].Value;
                }
                if (string.IsNullOrEmpty(stem)) continue;
                if (liveStems.Contains(stem)) continue;
                var nextWs = el.NextNode as XText;
                el.Remove();
                if (nextWs != null) nextWs.Remove();
                removed++;
            }
            if (removed > 0) xml.Save(dfbprojPath);
            return removed;
        }

        // Removes dfbproj entries pointing at an EAE-owned per-resource compile artifact (opcua/offline/
        // opcuaclient/symlink.xml) whose file is absent — EAE regenerates them on Build, so a dangling
        // ref = Missing Project File. Never touches .sysdev/.sysres/.hcf/.Properties.xml.
        public static int StripDanglingResourceArtifactEntries(string eaeProjectDir)
        {
            if (string.IsNullOrEmpty(eaeProjectDir)) return 0;
            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec)) return 0;
            var dfbprojPath = Directory.EnumerateFiles(iec, "*.dfbproj").FirstOrDefault();
            if (dfbprojPath == null) return 0;

            var xml = XDocument.Load(dfbprojPath, LoadOptions.PreserveWhitespace);
            var ns = xml.Root!.GetDefaultNamespace();
            var artifactRx = new System.Text.RegularExpressions.Regex(
                @"[\\/](opcua|offline|opcuaclient|symlink)\.xml$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);
            int removed = 0;
            var candidates = new System.Collections.Generic.List<XElement>();
            foreach (var name in new[] { "Content", "None", "Compile" })
                candidates.AddRange(xml.Descendants(ns + name));
            foreach (var el in candidates)
            {
                var include = (string?)el.Attribute("Include");
                if (string.IsNullOrEmpty(include)) continue;
                if (!include.Contains("System\\", StringComparison.OrdinalIgnoreCase) &&
                    !include.Contains("System/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!artifactRx.IsMatch(include)) continue;
                var abs = Path.Combine(iec,
                    include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(abs)) continue;  // file present — EAE compiled it; keep
                var nextWs = el.NextNode as XText;
                el.Remove();
                if (nextWs != null) nextWs.Remove();
                removed++;
            }
            if (removed > 0) xml.Save(dfbprojPath);
            return removed;
        }

        static int DeduplicateChildren(XElement group, XNamespace ns, string tag, string sysdevFileName)
        {
            int removed = 0;
            foreach (var entry in group.Elements(ns + tag).ToList())
            {
                var include = (string?)entry.Attribute("Include") ?? string.Empty;
                // Only entries tied to this sysdev (same file or its folder).
                if (!include.EndsWith(sysdevFileName, StringComparison.OrdinalIgnoreCase) &&
                    !include.Contains(Path.GetFileNameWithoutExtension(sysdevFileName),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                removed += CollapseDuplicateChildElements(entry, ns + "IEC61499Type");
                removed += CollapseDuplicateChildElements(entry, ns + "DependentUpon");
            }
            return removed;
        }

        static int CollapseDuplicateChildElements(XElement parent, XName childName)
        {
            var children = parent.Elements(childName).ToList();
            if (children.Count <= 1) return 0;
            // EAE honours only the first — keep it, drop the rest.
            for (int i = 1; i < children.Count; i++)
                children[i].Remove();
            return children.Count - 1;
        }

        // Safety-net pass: registers any .dt/.adp/.fbt in the IEC61499 folder not yet in the project, so
        // an external file drop is still picked up by the compiler.
        public static int SweepIec61499Folder(string dfbprojPath, string iec61499Dir)
        {
            if (!File.Exists(dfbprojPath) || !Directory.Exists(iec61499Dir)) return 0;
            int added = 0;

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
            // .fbt at root only — CAT folders are handled by RegisterCat.
            foreach (var fbt in Directory.EnumerateFiles(iec61499Dir, "*.fbt", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(fbt);
                // Composite if a same-stem .composite.offline.xml exists, else Basic.
                var stem = Path.GetFileNameWithoutExtension(name);
                bool isComposite = File.Exists(Path.Combine(iec61499Dir, stem + ".composite.offline.xml"));
                added += RegisterBasicFb(dfbprojPath, name, isComposite ? "Composite" : "Basic");
            }

            // Register flat sibling files at IEC61499 root as <None> — a Composite FB fails to resolve
            // its child FB type without the .composite.offline.xml registered.
            string[] siblingPatterns = {
                "*.composite.offline.xml",
                "*.doc.xml",
                "*.opcua.xml",
                "*.meta.xml",
            };
            var sxml = XDocument.Load(dfbprojPath);
            var sns = sxml.Root!.GetDefaultNamespace();
            var (_, sng) = Groups(sxml, sns);
            int siblingsAdded = 0;
            foreach (var pat in siblingPatterns)
                foreach (var f in Directory.EnumerateFiles(iec61499Dir, pat, SearchOption.TopDirectoryOnly))
                    Add(sng, sns, "None", Path.GetFileName(f), ref siblingsAdded);
            if (siblingsAdded > 0) sxml.Save(dfbprojPath);
            added += siblingsAdded;

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