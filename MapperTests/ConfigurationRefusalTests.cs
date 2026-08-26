using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Translation;
using CodeGen.Devices.Core;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MapperTests
{
    /// A configuration value that is wrong in a way nothing rejects does not fail: it generates, deploys
    /// and behaves differently - an FB drawn somewhere else, an INIT wire that was never emitted, an
    /// actuator driven the wrong way. These pin that each such value is REFUSED rather than defaulted.
    public sealed class ConfigurationRefusalTests
    {
        private static readonly IDeserializer Yaml = YamlDeclarations.Reader;

        private static T Parse<T>(string yaml) => Yaml.Deserialize<T>(yaml);

        // ---- telemetry policy ----------------------------------------------------------------

        [Theory]
        [InlineData("drawnAt: rooster")]          // a misspelling of a real value
        [InlineData("drawnAt: origin")]           // plausible, but not one of them
        [InlineData("broughtUpBy: stationn")]
        [InlineData("broughtUpBy: ring-head")]
        [InlineData("broughtUpBy: terminator")]   // a real resource role, but not one that starts a connection
        public void An_unknown_telemetry_policy_value_is_refused_rather_than_defaulted(string line)
        {
            Assert.ThrowsAny<Exception>(() =>
                Parse<MqttConnectionDeclaration>($"plc: M262\ninstance: X\n{line}\n"));
        }

        [Fact]
        public void A_value_that_differs_only_in_case_still_means_what_it_says()
        {
            // Refusing a misspelling is what matters. A case variant of a real value resolves to that
            // value, which is not the same thing as falling back to a default.
            var c = Parse<MqttConnectionDeclaration>("plc: M262\ninstance: X\ndrawnAt: bandhead\n");
            Assert.Equal(ConnectionPlacement.BandHead, c.DrawnAt);
        }

        [Fact]
        public void The_declared_telemetry_policy_values_parse_to_what_they_say()
        {
            var c = Parse<MqttConnectionDeclaration>(
                "plc: M580\ninstance: X\ndrawnAt: roster\nbroughtUpBy: ringHead\n");
            Assert.Equal(ConnectionPlacement.Roster, c.DrawnAt);
            Assert.Equal(ConnectionStarter.RingHead, c.BroughtUpBy);
        }

        [Fact]
        public void Every_shipped_connection_names_a_target_a_backend_implements()
        {
            foreach (var c in TelemetrySettings.Current.Connections)
                Assert.True(TargetRegistry.IsRegistered(c.Plc),
                    $"telemetry.yml declares a connection for {c.Plc}, which no backend implements");
            var plcs = TelemetrySettings.Current.Connections.Select(c => c.Plc).ToList();
            Assert.Equal(plcs.Count, plcs.Distinct().Count());
        }

        [Fact]
        public void A_connection_started_by_a_role_its_resource_lacks_would_never_be_brought_up()
        {
            // Stated as the rule the planner applies: a starter role resolves to an instance, and an
            // empty one means nothing raises the INITO that opens the broker.
            foreach (var c in TelemetrySettings.Current.Connections)
                Assert.True(Enum.IsDefined(c.BroughtUpBy),
                    $"{c.Plc} declares an undefined starter");
        }

        // ---- execution declarations ------------------------------------------------------------

        [Theory]
        [InlineData("mode: alternating")]
        [InlineData("mode: stop-driven")]
        [InlineData("mode: once")]
        [InlineData("mode: toggle")]
        public void An_unknown_execution_mode_is_refused(string line)
        {
            Assert.ThrowsAny<Exception>(() =>
                Parse<CatExecutionDeclaration>($"cat: X\n{line}\n"));
        }

        [Fact]
        public void Two_rows_claiming_one_component_are_refused_rather_than_ordered()
        {
            // Both rows claim a Robot on that CAT; which machine it runs would be whichever was written
            // first, so the pair is refused instead.
            var rows = new List<CatExecutionDeclaration>
            {
                Row("A", "", ExecutionMode.RunOnce, (1, 2)),
                Row("", "Robot", ExecutionMode.Alternate, (1, 2), (3, 0)),
            };
            var errors = Errors(rows);
            Assert.Contains(errors, e => e.Contains("both claim"));
        }

        [Fact]
        public void A_row_claiming_neither_a_cat_nor_a_type_claims_everything_and_is_refused()
        {
            Assert.Contains(Errors(new List<CatExecutionDeclaration>
            {
                Row("", "", ExecutionMode.Alternate, (1, 2), (3, 0)),
            }), e => e.Contains("every component there is"));
        }

        [Fact]
        public void A_sequence_with_no_steps_has_nothing_to_execute()
        {
            Assert.Contains(Errors(new List<CatExecutionDeclaration> { Row("A", "", ExecutionMode.RunOnce) }),
                e => e.Contains("declares no steps"));
        }

        [Fact]
        public void Alternating_over_one_step_has_nothing_to_alternate_with()
        {
            Assert.Contains(Errors(new List<CatExecutionDeclaration>
            {
                Row("A", "", ExecutionMode.Alternate, (1, 2)),
            }), e => e.Contains("alternates over"));
        }

        [Fact]
        public void Two_steps_settling_at_one_value_leave_resumption_undecided()
        {
            Assert.Contains(Errors(new List<CatExecutionDeclaration>
            {
                Row("A", "", ExecutionMode.Alternate, (1, 2), (3, 2)),
            }), e => e.Contains("settles at 2"));
        }

        [Fact]
        public void A_stop_driven_row_declaring_steps_would_run_none_of_them()
        {
            Assert.Contains(Errors(new List<CatExecutionDeclaration>
            {
                Row("A", "", ExecutionMode.StopDriven, (1, 2)),
            }), e => e.Contains("stopDriven but declares"));
        }

        [Fact]
        public void An_alternating_sequence_rotates_over_any_number_of_steps()
        {
            // Three steps, so the two-step case cannot be the rule. Resting at a step's arrival value
            // resumes at the NEXT one and wraps; resting at none, or at a value no step produces,
            // starts again.
            var row = Row("A", "", ExecutionMode.Alternate, (1, 2), (3, 4), (5, 6));
            Assert.Equal(1, row.StepFrom(null).Command);
            Assert.Equal(3, row.StepFrom(2).Command);
            Assert.Equal(5, row.StepFrom(4).Command);
            Assert.Equal(1, row.StepFrom(6).Command);
            Assert.Equal(1, row.StepFrom(99).Command);
        }

        [Fact]
        public void The_shipped_execution_rows_are_disjoint_and_every_component_gets_at_most_one()
        {
            Assert.Empty(Errors(RigCatalog.Current.Execution));
            // Asked the way the plan asks it: a claim that is ambiguous throws rather than picking.
            foreach (var t in TemplateCatalog.Current.Templates)
                foreach (var componentType in new string?[] { null, "Actuator", "Robot", "Sensor" })
                    TemplateManifest.ExecutionFor(t.Name, componentType);   // must not throw
        }

        // ---- bring-up ---------------------------------------------------------------------------

        [Fact]
        public void A_bring_up_naming_an_event_a_resource_does_not_raise_is_refused()
        {
            var errors = TargetRegistry.BringUpErrors(
                new List<BringUpWire> { new() { From = "START.HOT", To = "FB1.INIT" } },
                DeviceConfig.Current.BootSequence).ToList();
            Assert.Contains(errors, e => e.Contains("HOT") && e.Contains("does not raise"));
        }

        [Fact]
        public void The_shipped_bring_up_names_only_events_a_resource_raises()
        {
            Assert.Empty(TargetRegistry.BringUpErrors(
                DeviceConfig.Current.BringUp, DeviceConfig.Current.BootSequence));
        }

        // ---- target registration -----------------------------------------------------------------

        [Fact]
        public void One_backend_per_target_and_one_target_row_per_backend()
        {
            var backends = TargetRegistry.Backends.Select(b => b.Target).ToList();
            Assert.Equal(backends.Count, backends.Distinct().Count());

            var declared = DeviceConfig.Current.Targets.Select(t => t.Plc).ToList();
            Assert.Equal(declared.Count, declared.Distinct().Count());
            // Neither half may carry a target the other does not: the registry refuses the join, so a
            // registry that resolves at all is proof both agree.
            Assert.Equal(backends.OrderBy(p => p), declared.OrderBy(p => p));
            Assert.Equal(declared.Count, TargetRegistry.All.Count);
        }

        [Fact]
        public void Adding_a_target_takes_a_backend_and_a_declaration_and_no_second_array()
        {
            // The supported set IS the registered backends: nothing else enumerates the targets, so a
            // new controller cannot be half-added by editing one list and forgetting another.
            foreach (var t in TargetRegistry.All)
                Assert.Contains(t.Plc, TargetRegistry.Backends.Select(b => b.Target));
            foreach (var b in TargetRegistry.Backends)
                Assert.Contains(b.Target, TargetRegistry.All.Select(t => t.Plc));
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static CatExecutionDeclaration Row(
            string cat, string componentType, ExecutionMode mode, params (int Cmd, int Settled)[] steps) =>
            new()
            {
                Cat = cat,
                ComponentType = componentType,
                Mode = mode,
                Steps = steps.Select(s => new ExecutionStepDeclaration
                { Command = s.Cmd, Settled = s.Settled }).ToList(),
            };

        // The catalogue reports every fault at once, so a fixture reads them the same way.
        private static List<string> Errors(IReadOnlyList<CatExecutionDeclaration> rows)
        {
            var catalog = new RigCatalog { Execution = rows.ToList() };
            try
            {
                RigCatalogValidator.Validate(catalog);
                return new List<string>();
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Split('\n').Select(l => l.Trim()).ToList();
            }
        }

        // ---- an emitted FB has exactly one owner --------------------------------------------------

        [Fact]
        public void An_emitted_FB_nothing_owns_is_refused_rather_than_mirrored_by_a_default()
        {
            // The mirror used to fall back to whichever target hosts the station. A wrong owner
            // deploys perfectly well and simply never runs, so there is no safe default.
            var allocation = new ControllerAllocation(
                new DeploymentRoster(DeploymentProfile.AsPlaced(TestConfig.Cfg)));

            var ex = Assert.Throws<InvalidOperationException>(
                () => SysresFbMirror.BucketFor("SomeFbNobodyDeclares", allocation, Cfg()));
            Assert.Contains("no target owns it", ex.Message);
        }

        [Fact]
        public void Every_owner_a_target_can_supply_actually_answers()
        {
            var allocation = new ControllerAllocation(
                new DeploymentRoster(DeploymentProfile.AsPlaced(TestConfig.Cfg)));

            // A roster component, a declared telemetry connection, and a declared IO broker are the
            // three ways an FB gets an owner; each must resolve without the deleted fallback.
            foreach (var target in TargetRegistry.All.Where(t => t.IoBroker != null))
                Assert.Equal(target.Plc, SysresFbMirror.BucketFor(target.IoBroker!, allocation, Cfg()));

            foreach (var connection in TelemetrySettings.Current.Connections)
                Assert.Equal(connection.Plc, SysresFbMirror.BucketFor(connection.Instance, allocation, Cfg()));
        }

        [Fact]
        public void A_broker_declared_by_two_targets_would_be_two_owners()
        {
            // Proved on the shipped registry: no broker is claimed twice, and the join refuses one
            // that is. Both halves matter - the check is only worth having if it is also satisfied.
            var claimed = TargetRegistry.All.Where(t => t.IoBroker != null)
                .Select(t => t.IoBroker!).ToList();
            Assert.Equal(claimed.Count, claimed.Distinct(StringComparer.Ordinal).Count());
        }
        // A test is its own composition root: it reads the declarations the same way a run does.
        private static CompilerConfiguration Cfg() => TestConfig.Cfg;

    }
}
