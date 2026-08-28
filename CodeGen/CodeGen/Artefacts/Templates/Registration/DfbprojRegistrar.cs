using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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

            // Save only on a real change: an unconditional save bumps the mtime and prompts EAE to reload.
            if (a > 0) Save(xml, dfbprojPath);
            return a;
        }

        // Registers a hardware-device CAT type folder. Unlike RegisterCat it registers no actuator-CAT siblings: a hardware type has none.
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

            if (a > 0) Save(xml, dfbprojPath);
            return a;
        }

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
            if (removed > 0) Save(xml, dfbprojPath);
            return removed;
        }

        public static int RegisterBasicFb(string dfbprojPath, string fileName, string type = "Basic")
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, _) = Groups(xml, ns);
            int a = 0;
            Add(cg, ns, "Compile", fileName, ref a, new XElement(ns + "IEC61499Type", type));
            if (a > 0) Save(xml, dfbprojPath);   // only write on a real change
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
            if (a > 0) Save(xml, dfbprojPath);   // only write on a real change
            return a;
        }

        // Register an SE library <Reference>, else FBs of those types fail ERR_NO_SUCH_TYPE. An existing reference keeps its pinned Version.
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
            Save(xml, dfbprojPath);
            return 1;
        }

        // EAE's own device-type token for a PC-hosted runtime. Vendor vocabulary, so it is typed here
        // rather than declared: device.yml says WHICH type a target is, the schema says what the token is.
        const string SoftDpacDeviceType = "Soft_dPAC";

        // Registers a .sysdev (<Compile SystemDevice>) plus its sibling .hcf/Properties.xml, DependentUpon it.
        //
        // `target` is the descriptor of the device being registered, when there is one. It decides the two
        // registration shapes below from what the device IS rather than from which sysdev GUID it carries:
        // a null descriptor is a device with no target row (the HMI panel), which takes neither shape.
        public static int RegisterSystemDevice(string dfbprojPath, string eaeProjectDir, string sysdevPath,
            Mapping.TargetDescriptor? target = null)
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

            // The .sysdev must be DependentUpon the parent .system: TopologyManager binds Logical Device ->
            // System through it, else Deploy & Diagnostic filters the sysdev out as orphaned.
            Add(cg, ns, "Compile", sysdevRel, ref added,
                new XElement(ns + "DependentUpon", Artefacts.EaeAbi.SystemFileName),
                new XElement(ns + "IEC61499Type", "SystemDevice"));

            // Siblings go under <None SystemDevice>, EXCEPT a SOFT dPAC, whose .sysres must be
            // <Compile SystemResource> or EAE compiles no HWConfig for it — the scanner exports empty and
            // the device deploys with no I/O. A real dPAC keeps the rig-proven legacy <None>.
            //
            // Both shapes are decided from the device's own DECLARATION rather than from its sysdev GUID:
            // keyed on two GUIDs, a third Soft dPAC would silently get the wrong registration and deploy
            // with no hardware config at all.
            bool isSoftDpacResource = string.Equals(target?.DeviceType, SoftDpacDeviceType,
                StringComparison.OrdinalIgnoreCase);
            // A device whose hardware config drives an EtherNet/IP scanner additionally needs its .hcf as
            // <Content>, or EAE exports the scanner with no coupler in it.
            bool exportsEtherNetIpScanner = !string.IsNullOrWhiteSpace(target?.EtherNetIpDeviceType);
            if (Directory.Exists(sysdevFolder))
            {
                foreach (var sibling in Directory.EnumerateFiles(sysdevFolder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var rel = Path.GetRelativePath(iec, sibling).Replace('/', '\\');
                    var ext = Path.GetExtension(sibling).ToLowerInvariant();

                    if (ext == ".sysres" && isSoftDpacResource)
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

                    if (ext == ".hcf" && exportsEtherNetIpScanner)
                        Add(cg, ns, "Content", rel, ref added,
                            new XElement(ns + "IEC61499Type", "SystemDevice"),
                            new XElement(ns + "DependentUpon", sysdevFileName));
                }
            }

            int removed = DeduplicateChildren(ng, ns, "None", sysdevFileName)
                        + DeduplicateChildren(cg, ns, "Compile", sysdevFileName);

            // Backfill a missing DependentUpon on an existing Compile entry, else the device disappears from Deploy & Diagnostic.
            int backfilled = 0;
            foreach (var compile in cg.Elements(ns + "Compile"))
            {
                if (!string.Equals((string?)compile.Attribute("Include"), sysdevRel,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (compile.Elements(ns + "DependentUpon").Any()) continue;
                compile.AddFirst(new XElement(ns + "DependentUpon", Artefacts.EaeAbi.SystemFileName));
                backfilled++;
            }

            if (added > 0 || removed > 0 || backfilled > 0) Save(xml, dfbprojPath);
            return added;
        }

        // ONE file that belongs to a device folder, registered by whatever wrote it.
        //
        // RegisterSystemDevice picks up siblings by scanning the folder, which only registers what was
        // already on disk when the device was emitted. A stage that writes into that folder AFTERWARDS
        // has to say so: relying on some later pass re-scanning is a side effect, not an owner, and it
        // silently stops working the moment that pass is removed or the device is preserved.
        public static int RegisterDeviceArtefact(string? eaeProjectDir, string artefactPath)
        {
            if (string.IsNullOrEmpty(eaeProjectDir) || !File.Exists(artefactPath)) return 0;
            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            var dfbprojPath = Path.Combine(iec, "IEC61499.dfbproj");
            if (!File.Exists(dfbprojPath)) return 0;

            // The device folder is named after its .sysdev, which is what the entry must depend on.
            var folder = Path.GetDirectoryName(artefactPath);
            if (folder == null) return 0;
            var sysdevFileName = Path.GetFileName(folder) + ".sysdev";
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, sysdevFileName))) return 0;

            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (_, ng) = Groups(xml, ns);
            int added = 0;
            Add(ng, ns, "None", Path.GetRelativePath(iec, artefactPath).Replace('/', '\\'), ref added,
                new XElement(ns + "IEC61499Type", "SystemDevice"),
                new XElement(ns + "DependentUpon", sysdevFileName));
            if (added > 0) Save(xml, dfbprojPath);
            return added;
        }

        // Ensures the four APPLICATION entries exist: .sysapp + .syslay under <Compile>, aspmap/opcua under <Content>.
        public static int RegisterApplicationShell(string dfbprojPath)
        {
            if (!File.Exists(dfbprojPath)) return 0;
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();
            var (cg, _) = Groups(xml, ns);
            int added = 0;

            const string SystemId   = Artefacts.EaeAbi.SystemId;
            const string AppId      = ApplicationShellEmitter.AppId;
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

            if (added > 0) Save(xml, dfbprojPath);
            return added;
        }

        // Strips entries referencing a sysres-stem directory or .sysres file absent on disk, so Solution Integrity stays clean.
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
            if (removed > 0) Save(xml, dfbprojPath);
            return removed;
        }

        // Removes entries pointing at an absent EAE-owned compile artifact (EAE regenerates them on Build). Never touches .sysdev/.sysres/.hcf.
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
            if (removed > 0) Save(xml, dfbprojPath);
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

        // Safety net: registers any .dt/.adp/.fbt in IEC61499 not yet in the project, so an external file drop still compiles.
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

            // A Composite FB fails to resolve its child FB type unless its .composite.offline.xml is registered.
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
            if (siblingsAdded > 0) Save(sxml, dfbprojPath);
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

        // Drop any ItemGroup left empty: MSBuild ignores them but they accumulate every generation.
        static void Save(XDocument xml, string dfbprojPath)
        {
            XNamespace ns = xml.Root!.Name.Namespace;
            xml.Descendants(ns + "ItemGroup").Where(g => !g.HasElements).ToList().ForEach(g => g.Remove());
            xml.Save(dfbprojPath);
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