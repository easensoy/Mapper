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

            // Save ONLY if something was added. An unconditional save rewrites the
            // .dfbproj on every (idempotent) re-run, which bumps its mtime and makes
            // EAE pop a "Reload Solution" prompt even though nothing changed. With
            // ~8 CATs + basics + adapters + DTs each saving unconditionally, one
            // Test Runtime produced a flurry of reload prompts. (a > 0) collapses
            // that to a real change only.
            if (a > 0) xml.Save(dfbprojPath);
            return a;
        }

        /// <summary>
        /// Registers a generated <i>hardware device</i> CAT type folder — e.g. the
        /// EtherNet/IP coupler type <c>TM3BC_Ethe_yYhtt9jWKUOJs</c> that the BX1
        /// <c>.hcf</c> scanner (EIPSCANNER2) instantiates — using the EXACT shape the
        /// reference project uses:
        /// <code>
        ///   &lt;Compile Include="&lt;t&gt;\&lt;t&gt;.fbt"&gt;&lt;IEC61499Type&gt;CAT&lt;/IEC61499Type&gt;&lt;SubType&gt;Hardware&lt;/SubType&gt;&lt;/Compile&gt;
        ///   &lt;Compile Include="&lt;t&gt;\&lt;t&gt;_HMI.fbt"&gt;&lt;DependentUpon&gt;&lt;t&gt;.fbt&lt;/DependentUpon&gt;&lt;IEC61499Type&gt;CAT&lt;/IEC61499Type&gt;&lt;HMI&gt;..\HMI\&lt;t&gt;\&lt;t&gt;_sDefault.cnv.cs&lt;/HMI&gt;&lt;/Compile&gt;
        ///   &lt;None Include="&lt;t&gt;\&lt;t&gt;.cfg"&gt;&lt;DependentUpon&gt;&lt;t&gt;.fbt&lt;/DependentUpon&gt;&lt;IEC61499Type&gt;CAT&lt;/IEC61499Type&gt;&lt;/None&gt;
        ///   &lt;Folder Include="&lt;t&gt;" /&gt;
        /// </code>
        /// Unlike <see cref="RegisterCat"/> this does NOT register the actuator-CAT
        /// siblings (<c>_CAT.offline.xml</c> / <c>_CAT.opcua.xml</c> / <c>_HMI.*.xml</c>)
        /// — a generated hardware device type has none of those, and registering missing
        /// files makes EAE's Solution Integrity flag them as Missing Project Files. The
        /// compiler-generated gate types it pulls in (<c>AND_*</c>, <c>NOT_*</c>,
        /// <c>DS_SELECTX_*</c> in namespace Main) are produced by EAE at compile into
        /// SnapshotCompiles, not shipped. Idempotent.
        /// </summary>
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

        /// <summary>
        /// Removes every <c>&lt;Compile&gt;</c>/<c>&lt;None&gt;</c>/<c>&lt;Folder&gt;</c>
        /// entry registered by <see cref="RegisterHardwareDeviceCat"/> for the named type
        /// — used when the EtherNet/IP device is held out (cfg.EmitBx1EtherNetIpDevice
        /// false) so the type folder + its registrations are swept together and EAE does
        /// not list orphaned project files. Idempotent. Returns the number removed.
        /// </summary>
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
            if (a > 0) xml.Save(dfbprojPath);   // only write on a real change (see RegisterCat)
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
            if (a > 0) xml.Save(dfbprojPath);   // only write on a real change (see RegisterCat)
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
        /// Registers the M262 .sysdev plus its sibling .hcf and Properties.xml files in
        /// the .dfbproj as <c>&lt;None Include&gt;</c> entries with
        /// <c>&lt;IEC61499Type&gt;SystemDevice&lt;/IEC61499Type&gt;</c> and
        /// <c>&lt;DependentUpon&gt;</c> pointing at the .sysdev. The .sysdev itself
        /// gets a <c>&lt;Compile Include&gt;</c> entry. Idempotent — also de-duplicates
        /// repeated <c>IEC61499Type</c> / <c>DependentUpon</c> child elements that
        /// previous broken deploy runs left behind on existing entries.
        /// Returns the number of new entries added (existing-but-deduped doesn't count).
        /// </summary>
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

            // .sysdev itself goes under <Compile> with IEC61499Type=SystemDevice
            // AND a DependentUpon pointer at the parent .system file. EAE's
            // TopologyManager binds Logical Device -> System through that
            // pointer; without it the sysdev compiles but stays orphaned and
            // EAE's Deploy & Diagnostic tab silently filters it out (observed
            // 2026-05-27 — M262 sysdev was registered WITH DependentUpon and
            // appeared in D&D; M580 + BX1 sysdevs were registered WITHOUT and
            // disappeared even though their Equipment JSON, Properties.xml,
            // Simulation.Binding.xml and sysres were all valid). The .system
            // file's name is the zero UUID by Mapper convention.
            const string SystemFileName = "00000000-0000-0000-0000-000000000000.system";
            Add(cg, ns, "Compile", sysdevRel, ref added,
                new XElement(ns + "DependentUpon", SystemFileName),
                new XElement(ns + "IEC61499Type", "SystemDevice"));

            // Sibling files (under sysdev's per-device folder). MOST go under <None>.
            // The BX1 SoftPAC (the only PLC with an EtherNet/IP scanner — sysdev id
            // ...0004 by Mapper convention) needs its .sysres + .hcf registered the way
            // the working reference (SMC_Rig_Expo_withClamp) does, or EAE carries the
            // files but never COMPILES the resource's HWConfig → the Deploy export emits
            // an EMPTY EtherNet/IP scanner (the split-brain that kept the cover I/O dead):
            //   - .sysres : <Compile IEC61499Type=SystemResource DependentUpon=sysdev>
            //   - .hcf    : BOTH <None SystemDevice> AND <Content SystemDevice>
            // M262/M580 keep the legacy <None SystemDevice> for the .sysres + no .hcf
            // <Content> — their working mechanism is left UNTOUCHED (user directive
            // 2026-06-09: "don't touch M262/M580"); they have no EtherNet/IP scanner so
            // the SystemResource registration is irrelevant to them anyway. NOTE: this
            // registration is NECESSARY but not sufficient for a populated scanner — the
            // HwConfiguration TM3BC device model (Station2DeviceEmitter
            // .DeployBx1HwConfigScannerModel) is what EAE actually compiles the scanner from.
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
                        // Migrate any stale <None ...> registration of this .sysres (older
                        // Mapper versions emitted it as <None SystemDevice>) to the correct
                        // <Compile SystemResource> — otherwise both would coexist as a dup.
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

            // De-dup: any existing <None>/<Compile> referencing this sysdev that has
            // duplicate IEC61499Type or DependentUpon child elements gets cleaned up.
            int removed = DeduplicateChildren(ng, ns, "None", sysdevFileName)
                        + DeduplicateChildren(cg, ns, "Compile", sysdevFileName);

            // Backfill: an existing Compile entry for this sysdev that was
            // written by a prior Mapper version (before DependentUpon was
            // emitted) needs the child added in place. Without it the device
            // disappears from EAE's Deploy & Diagnostic tab. Detects the gap
            // and inserts the child element exactly once.
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

            // Save ONLY if we actually added an entry, removed a duplicate, or
            // backfilled a missing DependentUpon. Otherwise an idempotent re-run
            // rewrites the .dfbproj and triggers a spurious EAE "Reload Solution"
            // prompt (this is called once per sysdev — M262, M580, BX1 — so up
            // to three needless saves per Test Runtime).
            if (added > 0 || removed > 0 || backfilled > 0) xml.Save(dfbprojPath);
            return added;
        }

        /// <summary>
        /// Strips every &lt;Content&gt;/&lt;None&gt;/&lt;Compile&gt; entry from the
        /// .dfbproj whose Include path references a 14-17 hex-char sysres-stem
        /// directory that no longer has a matching <c>.sysres</c> file on disk.
        /// Companion to EmitOnePlc's sister-folder sweep — without this,
        /// EAE's Solution Integrity dialog lists every opcua.xml / offline.xml
        /// / opcuaclient.xml that used to live in those folders as a "missing
        /// project file" even though the folders were deliberately removed.
        /// Returns the number of entries removed.
        /// </summary>
        public static int StripStaleSysresStemEntries(string dfbprojPath, string eaeProjectDir)
        {
            if (!File.Exists(dfbprojPath) || !Directory.Exists(eaeProjectDir)) return 0;
            var systemDir = Path.Combine(eaeProjectDir, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return 0;

            // Build the set of live sysres stems (the BaseName of every .sysres
            // currently on disk anywhere under IEC61499/System/).
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

            // Walk every Item element that can carry an Include path.
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
                    // A .sysres FILE entry whose stem has no matching file on disk
                    // — e.g. a prior-deploy BX1 resource id (C9F2A4B7E1D3F5A8) left
                    // behind after the id was realigned to the .hcf's ResourceId
                    // (78E9CD3D27851B64). The directory-stem regex below MISSES this
                    // because here the stem is a filename, not a folder between
                    // backslashes — EAE then lists it as a Missing Project File and
                    // refuses to import the topology.
                    stem = Path.GetFileNameWithoutExtension(include);
                }
                else
                {
                    // A sister-folder reference (…\<stem>\opcua.xml etc.).
                    var m = stemRx.Match(include);
                    if (m.Success) stem = m.Groups[1].Value;
                }
                if (string.IsNullOrEmpty(stem)) continue;
                if (liveStems.Contains(stem)) continue;
                // Stale — remove the element (and its trailing whitespace so
                // we do not leave blank lines).
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
                // Only touch entries clearly tied to this sysdev (same file or its folder).
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
            // Keep the first, drop the rest. EAE only honours the first anyway.
            for (int i = 1; i < children.Count; i++)
                children[i].Remove();
            return children.Count - 1;
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

            // Register flat sibling files at IEC61499 root as <None> entries
            // (composite layout files, doc.xml, opcua.xml, meta.xml). EAE
            // bundles these into the project artefact and reads them when
            // opening the FB editor — the .fbt registration alone won't
            // import them, and Composite FBs in particular fail to resolve
            // their child FB type without the .composite.offline.xml present
            // in the dfbproj.
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