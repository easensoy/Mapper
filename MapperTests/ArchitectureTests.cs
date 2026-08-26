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
        // declaration), the COMPOSITION ROOT (it builds the snapshot), a REGISTRY frozen from a
        // declaration on first use, or the compatibility facade the prebuilt VueOne runner links
        // against by signature - which forwards and decides nothing.
        static readonly string[] ConfigurationLayer =
        {
            "CodeGen/CodeGen/Configuration/CompilerConfiguration.cs",
            "CodeGen/CodeGen/Application/GenerateProject.cs",
            "CodeGen/CodeGen/IO/GenerationConfig.cs",
            "CodeGen/CodeGen/IO/TemplateCatalog.cs",
            "CodeGen/CodeGen/IO/RigCatalog.cs",
            "CodeGen/CodeGen/Deployment/DeviceConfig.cs",
            "CodeGen/CodeGen/Deployment/TelemetrySettings.cs",
            "CodeGen/CodeGen/Planning/Interlocks/InterlockConfig.cs",
            "CodeGen/CodeGen/Input/Settings/MapperConfig.cs",
            "CodeGen/CodeGen/Mapping/TargetRegistry.cs",
            "CodeGen/CodeGen/Mapping/TargetBootstrap.cs",
            "CodeGen/CodeGen/Mapping/TemplateManifest.cs",
            "CodeGen/CodeGen/Mapping/TemplateMap.cs",
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
                // A registry projects one declaration and freezes it; it must hold no settable state,
                // which the mutable-static rule proves separately.
                bool registry = rel.Contains("/Mapping/", StringComparison.Ordinal);

                if (!loader && !snapshot && !root && !facade && !registry)
                    breaches.Add(rel + " -> not a loader, the snapshot, the composition root, the facade or a registry");
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
                             @"(DeviceConfig|GenerationConfig|TelemetrySettings|RigCatalog|InterlockConfig|TemplateCatalog)[.]Current"))
                    breaches.Add(Rel(f) + " -> " + m.Value);
            }
            NoBreaches(breaches,
                "a planner, validator, renderer or UI reloads configuration instead of being handed the " +
                "immutable snapshot, so two parts of one run could see different declarations");
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
        // Every target is declared once, has one backend kind, and claims no identity twice.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Every_target_is_declared_once_and_owns_its_identity_alone()
        {
            var targets = TargetRegistry.All;
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
                "RigCatalog", "InterlockConfig", "TemplateCatalog", "LayoutCatalog", "SecurityProfile",
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
            var types = TemplateManifest.Types;
            Assert.NotEmpty(types);

            foreach (var g in types.GroupBy(t => t.Name, StringComparer.Ordinal).Where(x => x.Count() > 1))
                Assert.Fail("templates.yml declares " + g.Key + " more than once");

            // ForInfraRole throws when a role is served by none or by more than one, which is exactly
            // the condition worth failing on: a coin toss over which vocabulary drives the plant.
            foreach (var role in types.SelectMany(t => t.InfraRoles).Distinct())
                Assert.NotNull(TemplateManifest.ForInfraRole(role));
        }
    }
}
