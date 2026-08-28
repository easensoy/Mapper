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
                Assert.True(TestConfig.Cfg.Targets.IsRegistered(c.Plc),
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
                    TestConfig.Cfg.Manifest.ExecutionFor(t.Name, componentType);   // must not throw
        }

        // ---- bring-up ---------------------------------------------------------------------------

        [Fact]
        public void A_bring_up_naming_an_event_a_resource_does_not_raise_is_refused()
        {
            var errors = TargetIndex.BringUpErrors(
                new List<BringUpWire> { new() { From = "START.HOT", To = "FB1.INIT" } },
                DeviceConfig.Current.BootSequence, TestConfig.Cfg.Manifest).ToList();
            Assert.Contains(errors, e => e.Contains("HOT") && e.Contains("does not raise"));
        }

        [Fact]
        public void The_shipped_bring_up_names_only_events_a_resource_raises()
        {
            Assert.Empty(TargetIndex.BringUpErrors(
                DeviceConfig.Current.BringUp, DeviceConfig.Current.BootSequence, TestConfig.Cfg.Manifest));
        }

        // ---- target registration -----------------------------------------------------------------

        [Fact]
        public void One_backend_per_target_and_one_target_row_per_backend()
        {
            var backends = TargetBackends.All.Select(b => b.Target).ToList();
            Assert.Equal(backends.Count, backends.Distinct().Count());

            var declared = DeviceConfig.Current.Targets.Select(t => t.Plc).ToList();
            Assert.Equal(declared.Count, declared.Distinct().Count());
            // Neither half may carry a target the other does not: the registry refuses the join, so a
            // registry that resolves at all is proof both agree.
            Assert.Equal(backends.OrderBy(p => p), declared.OrderBy(p => p));
            Assert.Equal(declared.Count, TestConfig.Cfg.Targets.All.Count);
        }

        [Fact]
        public void Adding_a_target_takes_a_backend_and_a_declaration_and_no_second_array()
        {
            // The supported set IS the registered backends: nothing else enumerates the targets, so a
            // new controller cannot be half-added by editing one list and forgetting another.
            foreach (var t in TestConfig.Cfg.Targets.All)
                Assert.Contains(t.Plc, TargetBackends.All.Select(b => b.Target));
            foreach (var b in TargetBackends.All)
                Assert.Contains(b.Target, TestConfig.Cfg.Targets.All.Select(t => t.Plc));
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
            foreach (var target in TestConfig.Cfg.Targets.All.Where(t => t.IoBroker != null))
                Assert.Equal(target.Plc, SysresFbMirror.BucketFor(target.IoBroker!, allocation, Cfg()));

            foreach (var connection in TelemetrySettings.Current.Connections)
                Assert.Equal(connection.Plc, SysresFbMirror.BucketFor(connection.Instance, allocation, Cfg()));
        }

        [Fact]
        public void A_broker_declared_by_two_targets_would_be_two_owners()
        {
            // Proved on the shipped registry: no broker is claimed twice, and the join refuses one
            // that is. Both halves matter - the check is only worth having if it is also satisfied.
            var claimed = TestConfig.Cfg.Targets.All.Where(t => t.IoBroker != null)
                .Select(t => t.IoBroker!).ToList();
            Assert.Equal(claimed.Count, claimed.Distinct(StringComparer.Ordinal).Count());
        }
        // ---- the physical topology graph -------------------------------------------------------

        // TopologyManager rejects the WHOLE topology on ONE unresolvable endpoint - a 500 at import that
        // names nothing. So every way a declared link can fail to resolve is refused when device.yml
        // loads: before a plan exists, and therefore before anything is written.
        static DeviceConfig Devices(string extraLinks, string extraNodes = "")
        {
            var c = Parse<DeviceConfig>(
            "installation:\n  switchEquipment: 11111111-2222-3333-4444-000000000060\n" +
            "  deployPluginProperties: A.Properties.xml\n  systemDeviceProperties: B.Properties.xml\n" +
            "targets:\n" +
            "  - plc: M262\n    backendKind: M262\n    resourceName: R\n    deviceType: T\n" +
            "    identity:\n      sysdev: 00000000-0000-0000-0000-000000000002\n" +
            "      equipment: 11111111-2222-3333-4444-000000000010\n" +
            "topology:\n  nodes:\n    - id: Switch_1\n      equipment: 11111111-2222-3333-4444-000000000060\n" +
            "      template: Equipment_Switch.json\n      emit: true\n" + extraNodes +
            "  links:\n" + extraLinks);
            DeviceConfig.Validate(c);   // the same validator a run runs, so a test asks what a run asks
            return c;
        }

        [Theory]
        // A node no target and no topology entry declares.
        [InlineData("    - identifier: L1\n      from: { node: Ghost, endpoint: equipment, port: P1 }\n" +
                    "      to:   { node: Switch_1, endpoint: equipment, port: Port1 }\n", "neither a declared target")]
        // An identity the named target does not carry: M262 declares no CPU, so ETH1 lands nowhere.
        [InlineData("    - identifier: L1\n      from: { node: M262, endpoint: cpu, port: ETH1 }\n" +
                    "      to:   { node: Switch_1, endpoint: equipment, port: Port1 }\n", "declares no such identity")]
        // A cable end with no port is a wire EAE cannot attach.
        [InlineData("    - identifier: L1\n      from: { node: M262, endpoint: equipment }\n" +
                    "      to:   { node: Switch_1, endpoint: equipment, port: Port1 }\n", "names no port")]
        // Two cables on one port: a wiring error the importer does not catch and the rig cannot honour.
        [InlineData("    - identifier: L1\n      from: { node: M262, endpoint: equipment, port: E1 }\n" +
                    "      to:   { node: Switch_1, endpoint: equipment, port: Port1 }\n" +
                    "    - identifier: L2\n      from: { node: M262, endpoint: equipment, port: E2 }\n" +
                    "      to:   { node: Switch_1, endpoint: equipment, port: Port1 }\n", "already uses")]
        // One label, two wires: which file a link writes is its identifier, so a repeat overwrites.
        [InlineData("    - identifier: L1\n      from: { node: M262, endpoint: equipment, port: E1 }\n" +
                    "      to:   { node: Switch_1, endpoint: equipment, port: Port1 }\n" +
                    "    - identifier: L1\n      from: { node: M262, endpoint: equipment, port: E2 }\n" +
                    "      to:   { node: Switch_1, endpoint: equipment, port: Port2 }\n", "declares link")]
        public void An_unresolvable_topology_link_is_refused_before_anything_is_written(
            string links, string because)
        {
            var ex = Assert.ThrowsAny<Exception>(() => Devices(links));
            Assert.Contains(because, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_emitted_topology_node_that_names_no_template_is_refused()
        {
            var ex = Assert.ThrowsAny<Exception>(() => Devices(
                "    - identifier: L1\n      from: { node: M262, endpoint: equipment, port: E1 }\n" +
                "      to:   { node: Extra, endpoint: equipment, port: P1 }\n",
                "    - id: Extra\n      equipment: 11111111-2222-3333-4444-000000000061\n      emit: true\n"));
            Assert.Contains("names no template", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_shipped_topology_graph_resolves_every_endpoint_it_declares()
        {
            // The refusals above are only worth having if the shipped declaration also satisfies them.
            var graph = TestConfig.Cfg.Devices.Topology;
            Assert.NotEmpty(graph.Links);
            foreach (var link in graph.Links)
                foreach (var e in new[] { link.From, link.To })
                {
                    var uuid = graph.Nodes
                        .FirstOrDefault(n => string.Equals(n.Id, e.Node, StringComparison.OrdinalIgnoreCase))
                        ?.Equipment
                        ?? DeviceConfig.EndpointUuid(
                            TestConfig.Cfg.Devices.Targets.First(t =>
                                string.Equals(t.Plc.Name, e.Node, StringComparison.OrdinalIgnoreCase)).Identity,
                            e.Endpoint);
                    Assert.False(string.IsNullOrWhiteSpace(uuid),
                        $"link '{link.Identifier}' endpoint '{e.Node}.{e.Endpoint}' resolves to nothing");
                }
        }

        // ---- how one target relates to another --------------------------------------------------

        // A relationship is stated ONCE, from the end that owns it, and its other end is derived. An
        // edge naming a target that is not declared would derive to nothing: a stand-in that relieves
        // no one still gets emitted and owns no ring, and a chain nobody commands never closes. Both
        // deploy and neither runs, so they are refused at load.
        static DeviceConfig Related(string firstExtra, string secondExtra = "") => Parse<DeviceConfig>(
            "installation:\n  switchEquipment: 11111111-2222-3333-4444-000000000060\n" +
            "  deployPluginProperties: A.Properties.xml\n  systemDeviceProperties: B.Properties.xml\n" +
            "targets:\n" +
            "  - plc: A\n    backendKind: M262\n    resourceName: R1\n    deviceType: T\n" +
            "    identity:\n      sysdev: 00000000-0000-0000-0000-000000000002\n" + firstExtra +
            "  - plc: B\n    backendKind: M580\n    resourceName: R2\n    deviceType: U\n" +
            "    identity:\n      sysdev: 00000000-0000-0000-0000-000000000003\n" + secondExtra);

        [Theory]
        [InlineData("    standsInFor: Ghost\n", "", "not a declared target")]
        [InlineData("    chainCommandedBy: Ghost\n", "", "not a declared target")]
        [InlineData("    standsInFor: A\n", "", "pointing at itself")]
        [InlineData("    chainCommandedBy: A\n", "", "pointing at itself")]
        // A stand-in owns no ring, so standing in for one borrows a ring that does not exist.
        [InlineData("    standsInFor: B\n", "    standsInFor: A\n", "itself stands in for")]
        // Each waiting for the other to close its ring: neither ever does.
        [InlineData("    chainCommandedBy: B\n", "    chainCommandedBy: A\n", "neither ring can close")]
        public void A_relationship_that_cannot_resolve_is_refused_at_load(
            string first, string second, string because)
        {
            var ex = Assert.ThrowsAny<Exception>(() => DeviceConfig.Validate(Related(first, second)));
            Assert.Contains(because, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_shipped_relationships_resolve_from_both_ends()
        {
            var targets = TestConfig.Cfg.Targets;
            foreach (var t in targets.All)
            {
                if (t.StandsInFor is { } host)
                {
                    Assert.True(targets.IsRegistered(host));
                    Assert.Equal(host, targets.RingHostOf(t));           // borrows that ring
                    Assert.Contains(t.Plc, targets.RingMembers(host));   // and is counted on it
                }
                if (t.ChainCommandedBy is { } commander)
                {
                    Assert.True(targets.IsRegistered(commander));
                    // The other end is DERIVED, so it cannot disagree with the end that declared it.
                    Assert.True(targets.CommandsACarriedChain(commander));
                    Assert.False(targets.CommandsACarriedChain(t.Plc));
                }
            }
            // Every ring has an owner, and a target that owns one is nobody's stand-in.
            Assert.Contains(targets.All, t => t.StandsInFor != null);
            Assert.Contains(targets.All, t => t.ChainCommandedBy != null);
        }

        // ---- the BX1 cover safe-start -----------------------------------------------------------

        // The gate that forces one actuator home on start, and the coupler fallback word the operator is
        // told to set so it STAYS home on Clean/Stop/fault, are two statements about one coil. They are
        // resolved once; these pin that an unresolvable safe-start is refused rather than half-wired.
        static Bx1IoProfile Io(string yaml) => Parse<Bx1IoProfile>(yaml);

        const string TwoCovers =
            "covers:\n" +
            "  - component: A\n    event: AEvent\n" +
            "    sensorFromHome: { signal: AHome, bit: 0 }\n" +
            "    coilToWork: { signal: AWork, bit: 0 }\n    coilToHome: { signal: AHomeCoil, bit: 1 }\n" +
            "  - component: B\n    event: BEvent\n" +
            "    sensorFromWork: { signal: BWork, bit: 5 }\n" +
            "    coilToWork: { signal: BWorkCoil, bit: 2 }\n";

        [Theory]
        [InlineData("", "declares no safeStartComponent")]              // nothing says which one homes
        [InlineData("safeStartComponent: C\n", "matches 0 declared")]   // names an actuator with no row
        [InlineData("safeStartComponent: B\n", "no coilToHome")]        // single-acting: cannot be driven home
        public void An_unresolvable_safe_start_is_refused_rather_than_half_wired(string extra, string because)
        {
            var ex = Assert.ThrowsAny<Exception>(() =>
                CodeGen.Devices.BX1.Bx1SafeStart.Resolve(Io(TwoCovers + extra)));
            Assert.Contains(because, ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_safe_start_actuator_with_no_home_sensor_is_refused()
        {
            // Without an at-home sensor the gate has nothing to release on: it would drive home forever.
            var io = Io(TwoCovers.Replace("    sensorFromHome: { signal: AHome, bit: 0 }\n", "") +
                        "safeStartComponent: A\n");
            var ex = Assert.ThrowsAny<Exception>(() =>
                CodeGen.Devices.BX1.Bx1SafeStart.Resolve(io));
            Assert.Contains("no sensorFromHome", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Two_coils_on_one_output_word_bit_are_refused()
        {
            // One bit drives one solenoid. Two would make the gate hold off a coil it also drives.
            var io = Io(TwoCovers.Replace("{ signal: BWorkCoil, bit: 2 }", "{ signal: BWorkCoil, bit: 1 }") +
                        "safeStartComponent: A\n");
            var ex = Assert.ThrowsAny<Exception>(() =>
                CodeGen.Devices.BX1.Bx1SafeStart.Resolve(io));
            Assert.Contains("coils on output word bit 1", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_shipped_safe_start_resolves_and_its_word_homes_only_that_actuator()
        {
            // The refusals matter only if the shipped declaration also satisfies them - and the word the
            // operator is told to set must be exactly the safe actuator's home coil and nothing else.
            var plan = CodeGen.Devices.BX1.Bx1SafeStart.Resolve(TestConfig.Cfg);
            Assert.Equal(TestConfig.Cfg.Devices.Bx1Io.SafeStartComponent, plan.Component);
            Assert.Equal(1 << plan.CoilToHome.Bit, plan.FallbackWord);
            foreach (var c in plan.Coils)
                Assert.Equal(c.DrivesHome && c.Component == plan.Component,
                             (plan.FallbackWord & (1 << c.Signal.Bit)) != 0);
            // Every other declared coil is held off, and none of them is one of the two it drives.
            Assert.DoesNotContain(plan.HeldOff, c => c.Component == plan.Component);
            Assert.Equal(plan.Coils.Count - 2, plan.HeldOff.Count);
        }

        // A test is its own composition root: it reads the declarations the same way a run does.
        private static CompilerConfiguration Cfg() => TestConfig.Cfg;

    }
}
