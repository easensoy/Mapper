using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Application;
using CodeGen.Configuration;
using Xunit;

namespace MapperTests
{
    /// AN UNVERIFIED PHYSICAL ASSUMPTION IS A DECLARATION, NOT A COMMENT.
    ///
    /// Control.xml carries control semantics. It says nothing about which solenoid moves an arm which
    /// way, and the compiler cannot find out - so the profile assumes, and the assumption is a standing
    /// risk on the rig. It used to live in a `#` comment above the channel rows it justified, which the
    /// YAML parser discards, so nobody generating a project ever saw it.
    public sealed class UnresolvedPhysicalFactTests
    {
        [Fact]
        public void The_shipped_profile_declares_its_unverified_assumptions()
        {
            var facts = TestConfig.Cfg.Rig.UnresolvedPhysicalFacts;
            Assert.NotEmpty(facts);
            Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Fact)));
            Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Risk)));
            Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.VerifyBy)));
        }

        [Fact]
        public void The_swivel_coil_direction_is_one_of_them()
        {
            // R-12. The consequence is a first-cycle collision, so it is the case this exists for.
            var facts = TestConfig.Cfg.Rig.UnresolvedPhysicalFacts;
            Assert.Contains(facts, f => f.Reference.Contains("R-12", StringComparison.Ordinal));
        }

        [Fact]
        public void Every_declared_assumption_is_reported_on_a_run()
        {
            var lines = new List<string>();
            GenerateProject.ReportUnresolvedPhysicalFacts(TestConfig.Cfg, lines.Add);

            // Not a summary count: each one says what it assumes, what breaks, and how to settle it.
            foreach (var f in TestConfig.Cfg.Rig.UnresolvedPhysicalFacts)
                Assert.Contains(lines, l => l.Contains(First(f.Fact), StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("IF WRONG:", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("VERIFY  :", StringComparison.Ordinal));

            static string First(string s) =>
                s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(4)
                 .Aggregate((a, b) => a + " " + b);
        }

        [Fact]
        public void A_profile_that_assumes_nothing_reports_nothing()
        {
            // Via a run's OWN bundle, not by clearing the shipped catalogue: the loaders hand every run
            // in a process the SAME cached declaration object, so mutating it here would empty it for
            // every other test too. (Writing this the short way did exactly that, which is what the
            // architecture rule against mutable shared configuration is protecting.)
            var root = Path.Combine(Path.GetTempPath(), "quiet_" + Guid.NewGuid().ToString("N")[..8]);
            var dst = Path.Combine(root, "Config");
            Directory.CreateDirectory(dst);
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Config")))
                    File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
                var rig = Path.Combine(dst, "smc-rig.yml");
                var text = File.ReadAllText(rig);
                File.WriteAllText(rig, text[..text.IndexOf("unresolvedPhysicalFacts:", StringComparison.Ordinal)]);

                var lines = new List<string>();
                GenerateProject.ReportUnresolvedPhysicalFacts(
                    CompilerConfiguration.Load(TestConfig.Cfg.Paths.Clone(), root), lines.Add);
                Assert.Empty(lines);
            }
            finally { try { Directory.Delete(root, true); } catch { /* temp */ } }
        }

        [Fact]
        public void An_assumption_with_no_stated_risk_is_refused()
        {
            var rig = new RigCatalog
            {
                UnresolvedPhysicalFacts = { new UnresolvedPhysicalFact { Fact = "a coil direction", VerifyBy = "look" } },
            };
            var m = Assert.Throws<InvalidOperationException>(() => RigCatalogValidator.Validate(rig)).Message;
            Assert.Contains("states no risk", m, StringComparison.Ordinal);
            Assert.Contains("nobody assessed", m, StringComparison.Ordinal);
        }

        [Fact]
        public void An_assumption_with_no_route_to_verification_is_refused()
        {
            var rig = new RigCatalog
            {
                UnresolvedPhysicalFacts = { new UnresolvedPhysicalFact { Fact = "a coil direction", Risk = "collision" } },
            };
            var m = Assert.Throws<InvalidOperationException>(() => RigCatalogValidator.Validate(rig)).Message;
            Assert.Contains("says how to verify nothing", m, StringComparison.Ordinal);
            Assert.Contains("unresolved forever", m, StringComparison.Ordinal);
        }
    }
}
