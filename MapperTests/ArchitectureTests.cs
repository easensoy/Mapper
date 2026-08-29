using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeGen.Mapping;
using Xunit;

namespace MapperTests
{
    /// The refactor's structural invariants, asserted rather than described. Each was true by
    /// inspection once; a test is what stops it quietly becoming false again. These read the working
    /// tree and fail naming the file and the symbol that broke the rule.
    public sealed class ArchitectureTests
    {
        static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "CodeGen", "CodeGen", "Planning")))
                d = d.Parent;
            Assert.True(d != null, "could not locate the repo from " + AppContext.BaseDirectory);
            return d!.FullName;
        }

        // Production C# this refactor owns: core + UI, minus the separately-owned HMI module and
        // everything MSBuild generates.
        static IEnumerable<string> Production()
        {
            foreach (var area in new[] { Path.Combine("CodeGen", "CodeGen"), "MapperUI" })
                foreach (var f in Directory.EnumerateFiles(Path.Combine(Root(), area), "*.cs", SearchOption.AllDirectories))
                {
                    var n = f.Replace(Path.DirectorySeparatorChar, '/');
                    if (n.Contains("/obj/") || n.Contains("/bin/") || n.Contains("/Hmi/")) continue;
                    yield return f;
                }
        }

        static string Rel(string f) => f.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Root().Replace(Path.DirectorySeparatorChar, '/') + "/", string.Empty);

        // A comment may describe a rule; only code can break one.
        static string CodeOf(string f)
        {
            var t = Regex.Replace(File.ReadAllText(f), @"/[*].*?[*]/", string.Empty, RegexOptions.Singleline);
            return string.Join("|", t.Split('\n')
                .Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));
        }

        static void NoBreaches(List<string> breaches, string rule) =>
            Assert.True(breaches.Count == 0,
                rule + ":" + Environment.NewLine + "  - " +
                string.Join(Environment.NewLine + "  - ", breaches.Distinct().OrderBy(x => x)));

        // ------------------------------------------------------------------------------------
        // Configuration is loaded once, at the composition root, and travels explicitly.
        // ------------------------------------------------------------------------------------

        // The only files that may reach a configuration global. Each is a LOADER (it owns the
        // declaration), the COMPOSITION ROOT (it builds the snapshot), or a COMPATIBILITY FACADE that
        // an external binary links by exact signature - which forwards and decides nothing.
        //
        // There used to be a fourth kind: a REGISTRY that froze a declaration on first use. There is
        // no such thing any more - the target and template indexes are built from a snapshot and hold
        // no declaration of their own - so the category is gone, and with it the place a newly frozen
        // static could have hidden.
        static readonly string[] ConfigurationLayer =
        {
            "CodeGen/CodeGen/Configuration/CompilerConfiguration.cs",
            "CodeGen/CodeGen/Application/GenerateProject.cs",
            "CodeGen/CodeGen/IO/GenerationConfig.cs",
            "CodeGen/CodeGen/IO/TemplateCatalog.cs",
            "CodeGen/CodeGen/IO/RigCatalog.cs",
            "CodeGen/CodeGen/Deployment/DeviceConfig.cs",
            "CodeGen/CodeGen/Deployment/SecurityProfile.cs",
            "CodeGen/CodeGen/Deployment/TelemetrySettings.cs",
            "CodeGen/CodeGen/Mapping/LayoutCatalog.cs",
            "CodeGen/CodeGen/Planning/Interlocks/InterlockConfig.cs",
            "CodeGen/CodeGen/Input/Settings/MapperConfig.cs",
            "CodeGen/CodeGen/Mapping/ControllerMap.cs",
        };

        // AN ALLOWLIST IS ONLY AS GOOD AS ITS ENTRIES. A list of file names that nothing checks
        // widens silently: an entry for a deleted file is dead weight, and an entry added to make a
        // failing test pass is the rule being edited rather than obeyed. So every entry must still
        // exist AND still be one of the four kinds the rule actually permits.
        [Fact]
        public void Every_entry_in_the_configuration_allowlist_justifies_itself()
        {
            var breaches = new List<string>();
            foreach (var rel in ConfigurationLayer)
            {
                var full = Path.Combine(Root(), rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) { breaches.Add(rel + " -> no such file (stale entry)"); continue; }

                var code = CodeOf(full);
                // A loader either declares the file itself or delegates to a loader type beside it.
                bool loader = code.Contains("YamlConfigFile<", StringComparison.Ordinal) ||
                              Regex.IsMatch(code, "Current +=> *[A-Za-z]+Loader[.]");
                // The snapshot is what every other reader is handed instead of reaching for a global.
                bool snapshot = rel.EndsWith("Configuration/CompilerConfiguration.cs", StringComparison.Ordinal);
                bool root = rel.EndsWith("Application/GenerateProject.cs", StringComparison.Ordinal);
                bool facade = rel.EndsWith("Input/Settings/MapperConfig.cs", StringComparison.Ordinal);
                // The HMI module and the prebuilt VueOne runner call this by exact signature and cannot
                // be handed a run's snapshot, so it reads the named process-wide one and decides nothing.
                bool compat = rel.EndsWith("Mapping/ControllerMap.cs", StringComparison.Ordinal);

                if (!loader && !snapshot && !root && !facade && !compat)
                    breaches.Add(rel + " -> not a loader, the snapshot, the composition root or a compatibility facade");
            }
            NoBreaches(breaches,
                "the configuration allowlist carries an entry that no longer justifies being on it");
        }

        [Fact]
        public void Configuration_is_read_only_by_the_configuration_layer()
        {
            var breaches = new List<string>();
            foreach (var f in Production())
            {
                if (ConfigurationLayer.Contains(Rel(f), StringComparer.OrdinalIgnoreCase)) continue;
                foreach (Match m in Regex.Matches(CodeOf(f),
                             @"(DeviceConfig|GenerationConfig|TelemetrySettings|RigCatalog|TemplateCatalog|SecurityProfile)[.]Current|CompilerConfiguration[.]Default"))
                    breaches.Add(Rel(f) + " -> " + m.Value);
            }
            NoBreaches(breaches,
                "a planner, validator, renderer or UI reloads configuration instead of being handed the " +
                "immutable snapshot, so two parts of one run could see different declarations");
        }

        // The rule above bans reaching for a loader's CACHED SINGLETON. This one bans the other half:
        // CALLING a loader at all. `MapperConfig.Load()` and `LayoutCatalog.Load()` are ordinary static
        // methods, so they slipped past the `.Current` pattern entirely - which is how a device emitter
        // came to resolve its own coupler from whatever bundle happened to be beside the DLL rather than
        // from the bundle its run was given.
        //
        // The exemptions are the two kinds that genuinely cannot be handed a snapshot: the places that
        // BUILD one, and the entry-point signatures an external binary links by exact shape. Each of the
        // latter also carries a cfg-taking overload, which is what every in-process caller uses.
        static readonly string[] MayCallALoader =
        {
            "CodeGen/CodeGen/Configuration/CompilerConfiguration.cs",   // builds the snapshot
            "CodeGen/CodeGen/Application/GenerateProject.cs",           // the composition root
            "CodeGen/CodeGen/Input/Settings/MapperConfig.cs",           // the loader itself
            "CodeGen/CodeGen/Mapping/LayoutCatalog.cs",                 // the loader itself
            "CodeGen/CodeGen/Devices/Common/FoldersXmlEmitter.cs",      // Register(MapperConfig): HMI module
            "MapperUI/Forms/MainForm.cs",                               // the UI composition root
        };

        [Fact]
        public void No_production_code_calls_a_configuration_loader_below_the_composition_root()
        {
            var breaches = new List<string>();
            foreach (var f in Production())
            {
                var rel = Rel(f);
                if (MayCallALoader.Any(a => rel.EndsWith(a, StringComparison.OrdinalIgnoreCase))) continue;
                foreach (Match m in Regex.Matches(CodeOf(f),
                             @"MapperConfig[.]Load\(|LayoutCatalog[.]Load\(|CompilerConfiguration[.]Load\("))
                    breaches.Add(rel + " -> " + m.Value + ")");
            }
            NoBreaches(breaches,
                "a file below the composition root loads a declaration file for itself, so what it " +
                "resolves comes from the bundle beside the DLL rather than from the bundle its run was given");
        }

        // AN EXEMPTION IS ONLY HONEST IF IT IS STILL NEEDED. An entry point exempted because an external
        // binary links its signature must still HAVE that signature and must still offer the cfg-taking
        // overload every in-process caller uses - otherwise the exemption is just a hole.
        [Fact]
        public void Every_loader_exemption_still_names_a_file_that_needs_one()
        {
            var breaches = new List<string>();
            foreach (var rel in MayCallALoader)
            {
                var full = Production().FirstOrDefault(f =>
                    Rel(f).EndsWith(rel, StringComparison.OrdinalIgnoreCase));
                if (full == null) { breaches.Add(rel + " -> no such production file (stale exemption)"); continue; }
                var code = CodeOf(full);
                // Either it CALLS a loader (a composition root or a compat entry point), or it DEFINES
                // one - a file that does neither has no reason to be exempt from the rule.
                bool calls = Regex.IsMatch(code, @"(MapperConfig|LayoutCatalog|CompilerConfiguration)[.]Load\(");
                bool defines = Regex.IsMatch(code, @"(static|public|internal)[^;{]*\bLoad(From)?\s*\(");
                if (!calls && !defines)
                    breaches.Add(rel + " -> neither calls nor defines a loader, so the exemption is dead weight");
            }
            NoBreaches(breaches, "the loader exemption list carries an entry that no longer justifies itself");
        }

        // ------------------------------------------------------------------------------------
        // The SEMANTIC layers compile against the run they were handed, never a process-wide one.
        // ------------------------------------------------------------------------------------

        // Domain and Planning turn a twin into a plan. Whatever they resolve a target, a CAT, a port
        // or a boot sequence against must arrive as an argument, because two generations can be in
        // flight at once - a different twin, a different target selection, a different profile - and
        // a shared registry is how one of them ends up compiled half against the other's declarations.
        //
        // This is stricter than the configuration rule above: that one bans re-READING a file, this
        // one bans reaching for any process-wide RESOLUTION of one. The named types below no longer
        // exist; the rule names them anyway so that re-introducing one fails here rather than quietly
        // reintroducing the defect it was built to remove.
        [Fact]
        public void Planning_resolves_targets_and_templates_from_the_run_it_was_given()
        {
            var breaches = new List<string>();
            foreach (var f in Production().Where(p =>
                         Rel(p).StartsWith("CodeGen/CodeGen/Domain/", StringComparison.Ordinal) ||
                         Rel(p).StartsWith("CodeGen/CodeGen/Planning/", StringComparison.Ordinal)))
            {
                foreach (Match m in Regex.Matches(CodeOf(f),
                             @"(?<![.\w])(TargetRegistry|TemplateManifest|TargetBootstrap|RingHost|ProcessPhaseTransport)(?![\w])|" +
                             @"CompilerConfiguration[.]Default|" +
                             @"(DeviceConfig|GenerationConfig|TelemetrySettings|RigCatalog|TemplateCatalog|SecurityProfile)[.]Current"))
                    breaches.Add(Rel(f) + " -> " + m.Value);
            }
            NoBreaches(breaches,
                "a Domain or Planning file resolves a target, template or boot sequence from process-wide " +
                "state instead of the run's own snapshot, so two concurrent runs could compile against " +
                "each other's declarations");
        }

        // A RESOLVED INDEX MUST NOT CACHE ITSELF. TargetIndex and TemplateIndex answer every question
        // about a declared target or FB type; each is built from one snapshot's declarations. A static
        // field on either would outlive the snapshot that filled it, which is precisely the shape this
        // whole layer was built to remove.
        [Fact]
        public void The_resolved_indexes_hold_no_state_of_their_own()
        {
            var breaches = new List<string>();
            foreach (var rel in new[] { "CodeGen/CodeGen/Mapping/TargetIndex.cs",
                                        "CodeGen/CodeGen/Mapping/TemplateIndex.cs" })
            {
                var full = Path.Combine(Root(), rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(full), rel + " is missing; the per-run index is what replaced the frozen registry");
                foreach (var raw in File.ReadAllLines(full))
                {
                    var line = raw.Trim();
                    // A static FIELD: declared static, ends the statement, and is not a method or an
                    // expression-bodied member. A const is a literal and carries no declaration.
                    if (!line.StartsWith("static", StringComparison.Ordinal) &&
                        !line.StartsWith("private static", StringComparison.Ordinal) &&
                        !line.StartsWith("internal static", StringComparison.Ordinal) &&
                        !line.StartsWith("public static", StringComparison.Ordinal)) continue;
                    if (line.Contains("(", StringComparison.Ordinal)) continue;      // a method
                    if (line.Contains("=>", StringComparison.Ordinal)) continue;     // an expression member
                    if (line.Contains(" const ", StringComparison.Ordinal)) continue;
                    if (!line.EndsWith(";", StringComparison.Ordinal)) continue;
                    breaches.Add(rel + " -> " + line);
                }
            }
            NoBreaches(breaches,
                "a resolved index carries static state, so it would outlive the configuration snapshot " +
                "that built it and answer a later run from an earlier run's declarations");
        }

        // ------------------------------------------------------------------------------------
        // A snapshot is read. Nothing writes back into one.
        // ------------------------------------------------------------------------------------

        // The declaration DTOs carry settable collections because that is how YamlDotNet populates
        // them; splitting each one into a parse type and a frozen model would add a parallel type per
        // configuration file and buy nothing the rule below does not.
        //
        // What actually matters is that nobody WRITES through them. The loaders hand out an
        // mtime-cached instance, so every run reading the shipped bundle holds the SAME object: one
        // Add() during a generation would be visible to every other generation in the process, and to
        // every later one until the file's timestamp changed. That is not a stale read - it is one run
        // editing another's declarations. `ProfileIsolationTests` proves the sharing is real; this
        // proves nothing exploits it.
        [Fact]
        public void No_production_code_writes_through_the_configuration_snapshot()
        {
            var snapshotMembers = string.Join("|", new[]
            {
                "Devices", "Generation", "Telemetry", "Rig", "Interlocks",
                "Templates", "Layout", "Security", "Manifest", "Targets",
            });
            var mutators = string.Join("|", new[] { "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll",
                                                    "Clear", "Insert", "Sort", "Reverse" });

            var breaches = new List<string>();
            foreach (var f in Production())
            {
                // The loaders and the snapshot BUILD the declarations; everyone else only reads them.
                if (ConfigurationLayer.Contains(Rel(f), StringComparer.OrdinalIgnoreCase)) continue;
                foreach (Match m in Regex.Matches(CodeOf(f),
                             @"[\w.]*(?:Cfg|cfg|config|Config)[.](?:" + snapshotMembers + @")[.][\w.\[\]]*[.](?:" + mutators + @")[(]"))
                    breaches.Add(Rel(f) + " -> " + m.Value);
            }
            NoBreaches(breaches,
                "production code mutates a collection reached through the configuration snapshot. The " +
                "loaders hand every run the same cached instance, so that edit reaches other runs' " +
                "declarations - copy what you need instead of writing back into the snapshot");
        }

        // ------------------------------------------------------------------------------------
        // The UI collects inputs and shows results. It does not know how a device is emitted.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void The_UI_never_reaches_into_a_device_emitter()
        {
            var breaches = new List<string>();
            foreach (var f in Production().Where(p => Rel(p).StartsWith("MapperUI/", StringComparison.Ordinal)))
                foreach (Match m in Regex.Matches(CodeOf(f), @"CodeGen[.]Devices[\w.]*"))
                    breaches.Add(Rel(f) + " -> " + m.Value);
            NoBreaches(breaches,
                "the UI names a device emitter, so adding a target would mean editing the front end");
        }

        // ------------------------------------------------------------------------------------
        // No plant instance is named in production C#. The twin supplies them.
        // ------------------------------------------------------------------------------------
        static readonly string[] PlantInstances =
        {
            "Bearing_PnP", "Bearing_Gripper", "Shaft_Hr", "Shaft_Vr",
            "Shaft_Gripper", "CoverPNP_Hr", "CoverPNP_Vr", "CoverPnp_Gripper",
            "PartInHopper", "BearingSensor", "ShaftSensor", "TopCoverSenosr",
            "Feed_Station", "Assembly_Station", "Disassembly_Station",
        };

        [Fact]
        public void No_plant_component_is_named_in_production_code()
        {
            var breaches = new List<string>();
            foreach (var f in Production())
            {
                var code = CodeOf(f);
                foreach (var name in PlantInstances)
                    if (code.Contains("\"" + name + "\"", StringComparison.Ordinal))
                        breaches.Add(Rel(f) + " -> " + name);
            }
            NoBreaches(breaches,
                "a plant instance is spelled in code, so this generator serves one plant rather than " +
                "whatever twin it is given");
        }

        // ------------------------------------------------------------------------------------
        // A device is emitted by ITS OWN BACKEND. Nothing else emits one.
        //
        // The template deployer used to emit the feed device, its topology and its hardware config as
        // well, so one artefact had two owners and the .dfbproj entry a run produced depended on which
        // of them ran first. Deleting either half changed generated bytes, which is what kept it there.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Only_a_target_backend_emits_a_device()
        {
            var emitters = new[]
            {
                "M262SysdevEmitter.Emit", "M262TopologyEmitter.Emit",
                "EaeDeviceWriter.EmitM580", "EaeDeviceWriter.EmitBx1",
                "RevPiDeviceEmitter.EmitDevice", "HwConfigVerbatimCopier.CopyFor",
            };
            var breaches = new List<string>();
            foreach (var f in Production())
            {
                var rel = Rel(f).Replace('\\', '/');
                // A backend, and the shared emitters they call, are the owners; the composition root
                // may still ASK a device whether it already exists in order to report it.
                if (rel.Contains("/Devices/")) continue;
                var code = CodeOf(f);
                foreach (var call in emitters)
                    if (code.Contains(call, StringComparison.Ordinal))
                        breaches.Add(rel + " -> " + call);
            }
            NoBreaches(breaches,
                "a device is emitted from outside its backend, so one artefact has two owners and the " +
                "bytes a run produces depend on which of them happened to run first");
        }

        // ------------------------------------------------------------------------------------
        // No static EAE document body is embedded in C#. A template file owns document structure.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void No_EAE_document_body_is_embedded_in_code()
        {
            var breaches = new List<string>();
            foreach (var f in Production())
            {
                var code = CodeOf(f);
                foreach (var marker in new[] { "<?xml version", "<FBType", "<SystemConfiguration" })
                    if (code.Contains(marker, StringComparison.Ordinal))
                        breaches.Add(Rel(f) + " -> " + marker);
            }
            NoBreaches(breaches,
                "a whole EAE document is built by string concatenation, so its structure cannot be " +
                "reviewed as the artefact it becomes");
        }

        // ------------------------------------------------------------------------------------
        // A deploy-time patch addresses a type by ROLE, never by filename.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void No_patch_addresses_a_deployed_type_by_spelling_its_filename()
        {
            // templates.yml says which type serves which role. A patch that spells the filename makes
            // the patch and the catalogue two owners of one fact, and the one that is wrong fails
            // SILENTLY - an absent .fbt is skipped, so the type simply never gains what the instance
            // parameters name. Resolving through the manifest means a renamed type is one YAML edit.
            var breaches = new List<string>();
            foreach (var f in Production())
                foreach (Match m in Regex.Matches(CodeOf(f),
                             @"(EditDeployedFbt|RequireDeployedFbt|FindDeployedFbt)\s*\([^)]*""[\w]+\.fbt"""))
                    breaches.Add(Rel(f) + " -> " + m.Value.Split('(')[0] + " with a literal filename");
            NoBreaches(breaches,
                "a deploy-time patch names a .fbt directly instead of asking templates.yml which type " +
                "serves the role it is patching");
        }

        // ------------------------------------------------------------------------------------
        // Every target is declared once, has one backend kind, and claims no identity twice.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Every_target_is_declared_once_and_owns_its_identity_alone()
        {
            var targets = TestConfig.Cfg.Targets.All;
            Assert.NotEmpty(targets);

            foreach (var g in targets.GroupBy(t => t.Plc.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
                Assert.Fail("device.yml declares target " + g.Key + " more than once");

            // Every registered target is backed by a declared row that names the backend kind that
            // emits it: adding another instance of an existing kind is then a device.yml edit alone.
            foreach (var t in targets)
            {
                var row = TestConfig.Cfg.Devices.Targets
                    .SingleOrDefault(d => d.Plc == t.Plc);
                Assert.True(row != null, "target " + t.Plc.Name + " is registered but device.yml declares no row for it");
                Assert.False(string.IsNullOrWhiteSpace(row!.BackendKind),
                    "target " + t.Plc.Name + " declares no backendKind, so nothing can say which backend emits it");
            }

            // Two targets sharing a resource name, an IO broker or a service port would each deploy
            // over the other: one lands and the other silently does not.
            void Unique(string what, Func<TargetDescriptor, string?> of)
            {
                foreach (var g in targets.Select(t => new { t.Plc.Name, Key = of(t) })
                             .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                             .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                             .Where(x => x.Count() > 1))
                    Assert.Fail(what + " " + g.Key + " is claimed by " + string.Join(" and ", g.Select(x => x.Name)));
            }
            Unique("resource name", t => t.ResourceName);
            Unique("IO broker", t => t.IoBroker);
            Unique("deploy service port", t => t.SimulationDeployPort.ToString());
            Unique("archive service port", t => t.SimulationArchivePort.ToString());
        }

        // ------------------------------------------------------------------------------------
        // A regex escape that did not survive the tool that wrote it.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void No_source_file_contains_a_literal_backspace()
        {
            // Twice now, a word-boundary escape has been written into this repository as the single
            // character U+0008. It compiles, it runs, and it matches nothing - so the scan that was
            // supposed to enforce a rule silently enforces nothing and reports PASS. A test that can
            // pass vacuously is worse than no test, so the character itself is banned.
            var breaches = new List<string>();
            foreach (var f in Production().Concat(TestSources()))
                if (File.ReadAllText(f).Contains((char)8))
                    breaches.Add(Rel(f));
            NoBreaches(breaches,
                "a source file contains a literal backspace, which is almost always a word-boundary " +
                "escape that was eaten by the tool that wrote it - the regex then matches nothing");
        }

        // ------------------------------------------------------------------------------------
        // WHAT EAE FIXES IS SPELLED ONCE.
        //
        // The null UUID is four different sentinels, and seven files each spelled it for themselves -
        // so which meaning any one of them carried could only be read from its surrounding comment.
        // The same for the vendor's device namespace and its embedded-resource type. These are not
        // configuration: changing one produces a project EAE will not load, so they stay typed - but
        // in ONE place, where each meaning is named.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void No_EAE_ABI_identity_is_spelled_outside_the_ABI()
        {
            var abi = Path.Combine("CodeGen", "CodeGen", "Artefacts", "Eae", "EaeAbi.cs");
            var breaches = new List<string>();
            foreach (var f in Production())
            {
                if (Rel(f).EndsWith(abi.Replace(Path.DirectorySeparatorChar, '/'), StringComparison.Ordinal))
                    continue;
                var code = CodeOf(f);
                foreach (Match m in Regex.Matches(code,
                             "\"(00000000-0000-0000-0000-[0-9a-f]{12}|SE[.]DPAC|EMB_RES_ECO|Runtime[.]Management|SE[.]AppBase)\""))
                    breaches.Add(Rel(f) + " -> " + m.Value);
            }
            NoBreaches(breaches,
                "an identity EAE fixes is spelled outside the one place that owns it, so which of its " +
                "meanings a use carries can only be read from the comment beside it");
        }

        // ------------------------------------------------------------------------------------
        // A FILE PINNED TO A LINE ENDING ACTUALLY CARRIES IT.
        //
        // A raw string literal inherits the newlines of the .cs file it sits in, so for these files the
        // line ending IS part of the artefact they write. .gitattributes pins them, but a pin only
        // governs what git checks out - an editor that rewrites a file with the other ending leaves it
        // pinned and wrong on disk, and the artefact silently changes length. That has now happened
        // three times, so it is checked rather than remembered.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Every_file_pinned_to_a_line_ending_carries_it_on_disk()
        {
            var attributes = Path.Combine(Root(), ".gitattributes");
            Assert.True(File.Exists(attributes), ".gitattributes is missing, so nothing pins them");

            var breaches = new List<string>();
            int pinned = 0;
            foreach (var raw in File.ReadAllLines(attributes))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var eol = line.Contains("eol=crlf", StringComparison.Ordinal) ? "crlf"
                        : line.Contains("eol=lf", StringComparison.Ordinal) ? "lf" : null;
                if (eol == null) continue;

                // A pattern containing a space is quoted; git needs the quotes and so does this.
                var pattern = line.StartsWith("\"", StringComparison.Ordinal)
                    ? line[1..line.IndexOf('"', 1)]
                    : line[..line.IndexOfAny(new[] { ' ', '\t' })];
                // HMI-owned files are excluded here as they are from every other rule in this file:
                // that module is separately owned. Two of its pinned files ARE lf on disk against a crlf
                // pin - a real finding, reported rather than silently fixed from outside the module.
                if (pattern.Contains("/Hmi/", StringComparison.Ordinal)) continue;

                var path = Path.Combine(Root(), pattern.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { breaches.Add(pattern + " -> pinned but not present"); continue; }

                pinned++;
                var bytes = File.ReadAllBytes(path);
                int crlf = 0, bare = 0;
                for (int i = 0; i < bytes.Length; i++)
                    if (bytes[i] == (byte)'\n') { if (i > 0 && bytes[i - 1] == (byte)'\r') crlf++; else bare++; }
                var actual = bare == 0 ? "crlf" : crlf == 0 ? "lf" : "MIXED";
                if (actual != eol) breaches.Add($"{pattern} -> pinned {eol}, on disk {actual}");
            }
            Assert.True(pinned > 0, ".gitattributes pins no file, so this rule proves nothing");
            NoBreaches(breaches,
                "a file whose line endings are part of the artefact it writes does not carry the " +
                "ending it is pinned to, so the bytes it emits are not the bytes that were reviewed");
        }

        static IEnumerable<string> TestSources() =>
            Directory.EnumerateFiles(Path.Combine(Root(), "MapperTests"), "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Path.Combine(Root(), "Gate"), "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Replace(Path.DirectorySeparatorChar, '/').Contains("/obj/")
                         && !f.Replace(Path.DirectorySeparatorChar, '/').Contains("/bin/"));

        // ------------------------------------------------------------------------------------
        // No mutable global carries state INTO a generation.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void No_mutable_static_carries_configuration_or_backends_between_runs()
        {
            // A settable static holding a declaration, a plan or a backend is state one run can leave
            // behind for the next, and state two concurrent runs share. Both are invisible until two
            // profiles are compiled at once and one of them silently gets the other's targets.
            var carriers = new[]
            {
                "CompilerConfiguration", "DeviceConfig", "GenerationConfig", "TelemetrySettings",
                "RigCatalog", "TemplateCatalog", "LayoutCatalog", "SecurityProfile",
                "ITargetBackend", "GenerationContext", "DeploymentProfile", "CompilerSession",
            };

            var breaches = new List<string>();
            foreach (var f in Production())
            {
                foreach (var line in CodeOf(f).Split('|'))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("static", StringComparison.Ordinal) &&
                        !t.Contains(" static ", StringComparison.Ordinal)) continue;
                    if (t.Contains("readonly", StringComparison.Ordinal)) continue;   // frozen on first use
                    if (t.Contains("const ", StringComparison.Ordinal)) continue;
                    // A settable static PROPERTY or a plain static FIELD of a carrier type.
                    var settable = t.Contains("set;", StringComparison.Ordinal) ||
                                   (t.EndsWith(";", StringComparison.Ordinal) &&
                                    !t.Contains("=>", StringComparison.Ordinal) &&
                                    !t.Contains("(", StringComparison.Ordinal));
                    if (!settable) continue;
                    foreach (var c in carriers)
                        if (Regex.IsMatch(t, "(?<![A-Za-z0-9_])" + Regex.Escape(c) + "(?![A-Za-z0-9_])"))
                            breaches.Add($"{Rel(f)} -> {t}");
                }
            }
            NoBreaches(breaches,
                "a mutable static carries a declaration, a plan or a backend, so one run can leave state " +
                "behind for the next and two concurrent runs share it");
        }

        // ------------------------------------------------------------------------------------
        // Every template is named once and every declared role resolves to exactly one type.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Every_template_is_declared_once_and_every_role_resolves()
        {
            var types = TestConfig.Cfg.Manifest.Types;
            Assert.NotEmpty(types);

            foreach (var g in types.GroupBy(t => t.Name, StringComparer.Ordinal).Where(x => x.Count() > 1))
                Assert.Fail("templates.yml declares " + g.Key + " more than once");

            // ForInfraRole throws when a role is served by none or by more than one, which is exactly
            // the condition worth failing on: a coin toss over which vocabulary drives the plant.
            foreach (var role in types.SelectMany(t => t.InfraRoles).Distinct())
                Assert.NotNull(TestConfig.Cfg.Manifest.ForInfraRole(role));
        }
    }
}
