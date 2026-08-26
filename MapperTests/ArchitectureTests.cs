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
            "CodeGen/CodeGen/Translation/Interlocks/InterlockConfig.cs",
            "CodeGen/CodeGen/Input/Settings/MapperConfig.cs",
            "CodeGen/CodeGen/Mapping/TargetRegistry.cs",
            "CodeGen/CodeGen/Mapping/TargetBootstrap.cs",
            "CodeGen/CodeGen/Mapping/TemplateManifest.cs",
            "CodeGen/CodeGen/Mapping/TemplateMap.cs",
        };

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
