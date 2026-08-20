using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MapperTests
{
    /// The compiler is layered frontend -> IR -> passes -> backend, and the layering is only real if it
    /// cannot quietly reverse. A planning pass that reaches for the EAE XML builder, a device emitter or
    /// the UI stops being a pass and becomes emission, which is how "the plan decides" turns back into
    /// "whoever writes the file decides". These read the source and fail naming the file and the symbol.
    public sealed class CompilerBoundaryTests
    {
        // Layers that may not appear in the semantic half of the compiler.
        private static readonly string[] BackendNamespaces =
        {
            "CodeGen.Artefacts",     // EAE artefact emitters
            "CodeGen.Devices",       // per-target backends
            "CodeGen.Services",      // deployed-tree services (template deploy, logging sink)
            "CodeGen.Hmi",           // the HMI module
            "MapperUI",              // the WinForms front end
        };

        // Emitting is writing XML or touching the filesystem. A pass does neither.
        private static readonly string[] EmissionTypes =
        {
            "XDocument", "XElement", "XAttribute", "XNamespace", "SyslayBuilder",
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "CodeGen", "CodeGen", "Planning")))
                dir = dir.Parent;
            Assert.True(dir != null,
                "could not locate the repo from " + AppContext.BaseDirectory +
                "; this test reads source, so it needs the working tree.");
            return dir!.FullName;
        }

        private static IEnumerable<string> SemanticSources()
        {
            var core = Path.Combine(RepoRoot(), "CodeGen", "CodeGen");
            foreach (var layer in new[] { "Domain", "Planning" })
            {
                var dir = Path.Combine(core, layer);
                Assert.True(Directory.Exists(dir), $"the '{layer}' layer is missing: {dir}");
                foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                    yield return f;
            }
        }

        // Comments describe the boundary; only code can cross it.
        private static string CodeOf(string file)
        {
            var text = File.ReadAllText(file);
            text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return string.Join("\n", text.Split('\n')
                .Select(l => { int i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; }));
        }

        [Fact]
        public void The_semantic_layers_do_not_depend_on_the_EAE_backend_or_the_UI()
        {
            var breaches = new List<string>();
            foreach (var file in SemanticSources())
            {
                var code = CodeOf(file);
                foreach (var ns in BackendNamespaces)
                    foreach (Match m in Regex.Matches(code, $@"\b{Regex.Escape(ns)}[\w.]*"))
                        breaches.Add($"{Path.GetFileName(file)} -> {m.Value}");
            }
            Assert.True(breaches.Count == 0,
                "the semantic layers reference the backend or the UI:" +
                Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", breaches.Distinct()));
        }

        [Fact]
        public void A_planning_pass_neither_builds_XML_nor_touches_the_filesystem()
        {
            var breaches = new List<string>();
            foreach (var file in SemanticSources())
            {
                var code = CodeOf(file);
                foreach (var t in EmissionTypes)
                    if (Regex.IsMatch(code, $@"\b{Regex.Escape(t)}\b"))
                        breaches.Add($"{Path.GetFileName(file)} -> {t}");
                foreach (Match m in Regex.Matches(code, @"\b(File|Directory)\.(Write|Create|Delete|Move|Copy|Append|Open)\w*"))
                    breaches.Add($"{Path.GetFileName(file)} -> {m.Value}");
            }
            Assert.True(breaches.Count == 0,
                "a semantic-layer file emits or writes:" +
                Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", breaches.Distinct()));
        }

        // A generic compiler reasons about resources, capabilities, ownership, reachability and CAT
        // protocols. The moment it can name a controller, a plant instance or the rig catalogue, the
        // plant it was written for becomes the only plant it compiles.
        [Fact]
        public void Generic_planning_names_no_controller_no_plant_instance_and_no_rig_catalogue()
        {
            // PlcAssignment.Unknown is the ABSENCE of a target, not a target, so it stays readable.
            var forbidden = new[]
            {
                @"\bRigCatalog\b",
                @"PlcAssignment\.(M262|M580|BX1|RevPi)\b",
                @"""(M262|M580|BX1|RevPi|M262_dPAC|M580_dPAC|Soft_dPAC|Revolution_Pi)""",
                @"\bTopCoverSensor\b|\bTopCoverSenosr\b",
                @"""(Feeder|Checker|Transfer|Ejector|Clamp|Bearing_\w+|Shaft_\w+|Cover\w+|"
                    + @"PartAtAssembly|PartInHopper|BearingSensor|ShaftSensor|Feed_Station|"
                    + @"Assembly_Station|Disassembly|Cover_Station|Rejector)""",
            };
            var breaches = new List<string>();
            foreach (var file in SemanticSources())
            {
                var code = CodeOf(file);
                foreach (var rx in forbidden)
                    foreach (Match m in Regex.Matches(code, rx))
                        breaches.Add($"{Path.GetFileName(file)} -> {m.Value}");
            }
            Assert.True(breaches.Count == 0,
                "generic planning names a controller, a plant instance or the rig catalogue:" +
                Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", breaches.Distinct()));
        }

        [Fact]
        public void The_frontend_is_the_only_place_that_parses_VueOne_XML()
        {
            var core = Path.Combine(RepoRoot(), "CodeGen", "CodeGen");
            var readers = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.Combine("CodeGen", "bin"), StringComparison.Ordinal))
                .Where(f => !f.Contains(Path.Combine("CodeGen", "obj"), StringComparison.Ordinal))
                // Matching one of the twin's element names against an XML document IS parsing the twin.
                // A file that merely NAMES the schema (the mapping-rules workbook indexes its
                // documentation sections by these prefixes) is not a parser.
                .Where(f => Regex.IsMatch(CodeOf(f),
                    @"LocalName\s*==\s*""(Sequence_Condition|ConditionGroup|ConditionValue|Interlock_Condition|Condition|Component|State|Transition)"""))
                .Select(Path.GetFileName)
                .ToList();

            // One reader. A second place that knows the twin's element names is a second frontend, and the
            // two drift: that is how the ConditionGroup layer came to be read in one place and ignored in
            // every other.
            Assert.Equal(new[] { "SystemXmlReader.cs" }, readers.OrderBy(n => n, StringComparer.Ordinal));
        }
    }
}
