using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.RevPi;
using CodeGen.Mapping;
using CodeGen.Translation;
using CodeGen.Validation.Output;
using CodeGen.Validation.Plan;
using Xunit;

// MapperConfig's routing switches are STATIC (FeedStationController, RevPiComponents) and
// ComponentRegistry caches its partition off them, so these tests mutate global state and must never
// run concurrently with each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MapperTests
{
    /// Restores the global routing statics after every test so one test can never leak its
    /// selection into the next (and so a failing test cannot poison the whole run).
    public abstract class RevPiTestBase : IDisposable
    {
        private readonly FeedController _controller = MapperConfig.FeedStationController;
        private readonly IReadOnlySet<string> _components = MapperConfig.RevPiComponents;

        protected static void SelectM262()
        {
            MapperConfig.FeedStationController = FeedController.M262;
            MapperConfig.RevPiComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// The supported RevPi mode: named Feed components move, M262 keeps the rest.
        protected static void SelectRevPiComponents(params string[] names)
        {
            MapperConfig.FeedStationController = FeedController.M262;
            MapperConfig.RevPiComponents = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        protected static void SelectRevPiFullSwap()
        {
            MapperConfig.FeedStationController = FeedController.RevPi;
            MapperConfig.RevPiComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            MapperConfig.FeedStationController = _controller;
            MapperConfig.RevPiComponents = _components;
            GC.SuppressFinalize(this);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Selection contract + component partition
    // ---------------------------------------------------------------------------------------------
    public sealed class RevPiSelectionTests : RevPiTestBase
    {
        [Fact] // (1) default M262 selection remains valid
        public void Default_selection_is_M262_and_keeps_the_feed_station_on_M262()
        {
            SelectM262();
            Assert.False(MapperConfig.PartialRevPi);
            var byName = ComponentRegistry.ByName;
            Assert.Equal(PlcAssignment.M262, byName["Feeder"].Plc);
            Assert.Equal(PlcAssignment.M262, byName["Checker"].Plc);
            Assert.DoesNotContain(byName.Values, e => e.Plc == PlcAssignment.RevPi);
        }

        [Fact] // (16) Feeder/Checker are allocated to the RevPi when selected
        public void Selecting_Feeder_and_Checker_relocates_them_onto_the_RevPi()
        {
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            Assert.True(MapperConfig.PartialRevPi);
            var byName = ComponentRegistry.ByName;
            Assert.Equal(PlcAssignment.RevPi, byName["Feeder"].Plc);
            Assert.Equal(PlcAssignment.RevPi, byName["Checker"].Plc);
            Assert.Equal(PlcAssignment.RevPi, byName["PartInHopper"].Plc);
        }

        [Fact] // (17) they are absent from M262 once relocated — no dual hosting
        public void Relocated_components_are_no_longer_hosted_on_M262()
        {
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            var m262 = ComponentRegistry.ByName.Values
                .Where(e => e.Plc == PlcAssignment.M262)
                .Select(e => e.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Feeder", m262);
            Assert.DoesNotContain("Checker", m262);
            Assert.DoesNotContain("PartInHopper", m262);
        }

        [Fact] // the partial swap must KEEP M262 — it is a coexistence mode, not a replacement
        public void Partial_swap_keeps_the_rest_of_the_feed_station_on_M262()
        {
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            var byName = ComponentRegistry.ByName;
            Assert.Equal(PlcAssignment.M262, byName["Transfer"].Plc);
            Assert.Contains(byName.Values, e => e.Plc == PlcAssignment.M262);
        }

        [Fact] // (19) no component may be owned by two controllers
        public void Every_component_is_hosted_by_exactly_one_controller()
        {
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            var byName = ComponentRegistry.ByName;
            // The dictionary is keyed by name, so a duplicate host would surface as a name collision.
            Assert.Equal(byName.Count, byName.Values.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count());
            // Every DEVICE-HOSTED component must name a real controller. Boot rows (FB1/FB2-class
            // scaffolding, Column -1) are deliberately unassigned and are excluded by design.
            foreach (var e in byName.Values.Where(e => e.Row != LayoutRow.Boot))
                Assert.True(e.Plc is PlcAssignment.M262 or PlcAssignment.M580
                                  or PlcAssignment.BX1 or PlcAssignment.RevPi,
                    $"component '{e.Name}' has no controller assignment ({e.Plc})");
        }

        [Fact] // M580/BX1 are fixed and must be untouched by any RevPi selection
        public void RevPi_selection_never_moves_M580_or_BX1_components()
        {
            SelectM262();
            var before = ComponentRegistry.ByName.Values
                .Where(e => e.Plc is PlcAssignment.M580 or PlcAssignment.BX1)
                .ToDictionary(e => e.Name, e => e.Plc, StringComparer.Ordinal);

            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            var after = ComponentRegistry.ByName;
            foreach (var (name, plc) in before)
                Assert.Equal(plc, after[name].Plc);
        }

        [Fact] // relocation must preserve canvas coordinates so the Feed band renders unchanged
        public void Relocation_preserves_canvas_coordinates()
        {
            SelectM262();
            var before = ComponentRegistry.ByName["Feeder"];
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            var after = ComponentRegistry.ByName["Feeder"];
            Assert.Equal(before.X, after.X);
            Assert.Equal(before.Y, after.Y);
            Assert.Equal(before.Column, after.Column);
        }

        [Fact] // the relocated components land on the RevPi RESOURCE, not just the RevPi bucket
        public void Relocated_components_are_bound_to_the_RevPi_resource()
        {
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            var expected = ControllerMap.ResourceForPlc(PlcAssignment.RevPi);
            Assert.False(string.IsNullOrWhiteSpace(expected));
            Assert.Equal(expected, ComponentRegistry.ByName["Feeder"].Resource);
        }

        [Fact] // switching back must fully restore the M262 partition (cache correctness)
        public void Switching_back_to_M262_restores_the_default_partition()
        {
            SelectM262();
            var baseline = ComponentRegistry.ByName.ToDictionary(kv => kv.Key, kv => kv.Value.Plc, StringComparer.Ordinal);
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            SelectM262();
            var restored = ComponentRegistry.ByName;
            foreach (var (name, plc) in baseline)
                Assert.Equal(plc, restored[name].Plc);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fail-loud guards on an invalid or unsupportable selection  (25)
    // ---------------------------------------------------------------------------------------------
    public sealed class RevPiSelectionGuardTests : RevPiTestBase
    {
        [Fact]
        public void Default_M262_selection_raises_no_problems()
        {
            SelectM262();
            Assert.Empty(RevPiSelectionValidator.Validate());
        }

        [Fact]
        public void Supported_per_component_swap_raises_no_problems()
        {
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            Assert.Empty(RevPiSelectionValidator.Validate());
        }

        [Fact] // the whole-Feed swap strands Transfer/Ejector/Robot/PartAtAssembly with no Modbus IO
        public void Whole_feed_swap_is_rejected_because_the_coupler_cannot_serve_it()
        {
            SelectRevPiFullSwap();
            var problems = RevPiSelectionValidator.Validate();
            Assert.NotEmpty(problems);
            Assert.Contains(problems, p => p.Contains("not supported", StringComparison.OrdinalIgnoreCase));
            Assert.Throws<RevPiSelectionValidator.InvalidRevPiSelectionException>(
                RevPiSelectionValidator.ThrowIfInvalid);
        }

        [Fact] // ComponentRegistry silently DISCARDS RevPiComponents on the full-swap branch
        public void Combining_full_swap_with_an_explicit_component_set_is_rejected()
        {
            MapperConfig.FeedStationController = FeedController.RevPi;
            MapperConfig.RevPiComponents = new HashSet<string>(new[] { "Feeder" }, StringComparer.OrdinalIgnoreCase);
            var problems = RevPiSelectionValidator.Validate();
            Assert.Contains(problems, p => p.Contains("mutually exclusive", StringComparison.OrdinalIgnoreCase));
        }

        [Fact] // a component with no Modbus signal would deploy unable to actuate
        public void Routing_an_uncovered_component_to_the_RevPi_is_rejected()
        {
            SelectRevPiComponents("Transfer");
            var problems = RevPiSelectionValidator.Validate();
            Assert.NotEmpty(problems);
            Assert.Contains(problems, p => p.Contains("Transfer", StringComparison.Ordinal));
        }

        [Fact] // the covered set is derived from the coupler tables, never hardcoded
        public void Coupler_coverage_is_exactly_Feeder_Checker_and_PartInHopper()
        {
            Assert.Equal(
                new[] { "Checker", "Feeder", "PartInHopper" },
                RevPiIoBrokerInjector.CoveredComponents.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Address roles  (4, 5)
    // ---------------------------------------------------------------------------------------------
    public sealed class RevPiAddressTests : RevPiTestBase
    {
        [Fact] // (4) host and container are separate layer-2 endpoints
        public void Configured_host_and_container_addresses_are_distinct()
        {
            var cfg = new MapperConfig();
            Assert.False(string.IsNullOrWhiteSpace(cfg.RevPiHostIp));
            Assert.False(string.IsNullOrWhiteSpace(cfg.RevPiTargetIp));
            Assert.NotEqual(cfg.RevPiHostIp, cfg.RevPiTargetIp);
        }

        [Fact] // the container is EAE's deploy target; .6 is what the EAE compiler itself resolved
        public void Container_address_is_the_compiler_proven_endpoint()
        {
            Assert.Equal("192.168.1.6", new MapperConfig().RevPiTargetIp);
        }

        [Fact] // (5) the RevPi must not collide with the HMI host or the HMI panel container
        public void RevPi_addresses_do_not_collide_with_the_HMI()
        {
            var cfg = new MapperConfig();
            Assert.NotEqual(cfg.HmiHostIp, cfg.RevPiHostIp);
            Assert.NotEqual(cfg.HmiHostIp, cfg.RevPiTargetIp);
            Assert.NotEqual(cfg.HmiInternalRuntimeIp, cfg.RevPiHostIp);
            Assert.NotEqual(cfg.HmiInternalRuntimeIp, cfg.RevPiTargetIp);
        }

        [Fact]
        public void Equal_host_and_container_addresses_are_rejected()
        {
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            var cfg = new MapperConfig { RevPiHostIp = "192.168.1.7", RevPiTargetIp = "192.168.1.7" };
            var problems = TopologyAddressValidator.ValidateRevPiRoles(cfg).ToList();
            Assert.Contains(problems, p => p.IsError && p.Detail.Contains("cannot share one address", StringComparison.Ordinal));
        }

        [Fact]
        public void Malformed_or_empty_addresses_are_rejected()
        {
            SelectRevPiComponents("Feeder", "Checker", "PartInHopper");
            Assert.Contains(
                TopologyAddressValidator.ValidateRevPiRoles(new MapperConfig { RevPiHostIp = "not-an-ip" }),
                p => p.IsError);
            Assert.Contains(
                TopologyAddressValidator.ValidateRevPiRoles(new MapperConfig { RevPiTargetIp = "" }),
                p => p.IsError);
        }

        [Fact] // address roles are only asserted when a RevPi is actually being generated
        public void Address_role_checks_are_silent_when_no_RevPi_is_selected()
        {
            SelectM262();
            var cfg = new MapperConfig { RevPiHostIp = "192.168.1.7", RevPiTargetIp = "192.168.1.7" };
            Assert.Empty(TopologyAddressValidator.ValidateRevPiRoles(cfg));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Topology collision detection over real Equipment_*.json shapes
    // ---------------------------------------------------------------------------------------------
    public sealed class TopologyAddressCollisionTests : RevPiTestBase, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "MapperTests_topo_" + Guid.NewGuid().ToString("N"));

        private string TopologyDir
        {
            get
            {
                var d = Path.Combine(_root, "Topology");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private void WriteEquipment(string file, string identifier, string catalog, string ip, string domain) =>
            File.WriteAllText(Path.Combine(TopologyDir, file), $$"""
            {
              "catalogReference": "{{catalog}}",
              "identifier": "{{identifier}}",
              "components": [
                { "interfaces": [ { "identifier": "eth0",
                    "endpoints": [ { "identifier": "IP Address", "ipAddress": "{{ip}}", "domain": "{{domain}}" } ] } ] }
              ]
            }
            """);

        private const string Domain = "db72f221-ece1-4b82-8132-731ce655044e";

        [Fact]
        public void Unique_addresses_produce_no_violations()
        {
            SelectM262();
            WriteEquipment("Equipment_A.json", "A", "SoftdpacContainer_V01.00_01.00", "192.168.1.6", Domain);
            WriteEquipment("Equipment_B.json", "B", "Workstation_V01.00_01.00", "192.168.1.2", Domain);
            Assert.Empty(TopologyAddressValidator.Validate(_root, null));
        }

        [Fact] // a duplicated CONTAINER address is fatal — it is EAE's deploy/login target
        public void Duplicate_container_address_is_a_fatal_error()
        {
            SelectM262();
            WriteEquipment("Equipment_A.json", "Softdpac_2", "HMIP6_SoftdpacContainer_V01.00_01.00", "192.168.1.1", Domain);
            WriteEquipment("Equipment_B.json", "Softdpac_3", "SoftdpacContainer_V01.00_01.00", "192.168.1.1", Domain);
            var v = TopologyAddressValidator.Validate(_root, null);
            Assert.Contains(v, x => x.IsError && x.Detail.Contains("192.168.1.1", StringComparison.Ordinal));
        }

        [Fact] // duplicated HOST NICs warn but must not reject known-good output
        public void Duplicate_host_nic_address_warns_but_does_not_fail()
        {
            SelectM262();
            WriteEquipment("Equipment_A.json", "HMIP6_1", "HMIP6_V01.00_01.00", "192.168.1.2", Domain);
            WriteEquipment("Equipment_B.json", "NIC_1", "NIC_EAE_V01.00_01.00", "192.168.1.2", Domain);
            var v = TopologyAddressValidator.Validate(_root, null);
            Assert.NotEmpty(v);
            Assert.DoesNotContain(v, x => x.IsError);
        }

        [Fact] // the same address in DIFFERENT broadcast domains is not a collision
        public void Same_address_in_different_domains_is_not_a_collision()
        {
            SelectM262();
            WriteEquipment("Equipment_A.json", "A", "SoftdpacContainer_V01.00_01.00", "192.168.1.6", Domain);
            WriteEquipment("Equipment_B.json", "B", "SoftdpacContainer_V01.00_01.00", "192.168.1.6",
                "2131fbdd-0a41-4e41-abfb-a14a5ca9218d");
            Assert.DoesNotContain(TopologyAddressValidator.Validate(_root, null), x => x.IsError);
        }

        [Fact] // 0.0.0.0 is "unconfigured", not a claim on an address
        public void Unconfigured_endpoints_are_ignored()
        {
            SelectM262();
            WriteEquipment("Equipment_A.json", "A", "HMIP6_V01.00_01.00", "0.0.0.0", Domain);
            WriteEquipment("Equipment_B.json", "B", "Workstation_V01.00_01.00", "0.0.0.0", Domain);
            Assert.Empty(TopologyAddressValidator.Validate(_root, null));
        }

        public new void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
            base.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Template artefacts the RevPi device depends on  (13, 14, 15)
    // ---------------------------------------------------------------------------------------------
    public sealed class RevPiTemplateArtefactTests
    {
        // Walk up from the test bin to the repo root (it holds the Template Library).
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Template Library")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        [Fact] // (13) the Modbus broker composite must be staged
        public void PLC_RW_REVPI_composite_is_staged()
        {
            var zip = Path.Combine(RepoRoot(), "Template Library", "Composite", "PLC_RW_REVPI.zip");
            Assert.True(File.Exists(zip), $"missing RevPi broker composite: {zip}");
        }

        [Fact] // (15) the Modbus master hardware config must be staged
        public void RevPi_modbus_hcf_is_staged_and_is_a_modbus_master()
        {
            var hcf = Path.Combine(RepoRoot(), "Template Library", "RevPi", "RevPiIO.modbus.hcf");
            Assert.True(File.Exists(hcf), $"missing RevPi Modbus hcf: {hcf}");
            var text = File.ReadAllText(hcf);
            Assert.Contains("Modbus", text, StringComparison.OrdinalIgnoreCase);
            // The .hcf LinkNames resolve against the broker FB id, so the two must agree.
            Assert.Contains(RevPiIoBrokerInjector.BrokerFbId, text, StringComparison.Ordinal);
        }

        [Fact] // device.yml is the deployed source of the RevPi addresses
        public void device_yml_is_copied_beside_the_assembly()
        {
            var yml = Path.Combine(AppContext.BaseDirectory, "Config", "device.yml");
            Assert.True(File.Exists(yml), $"device.yml not deployed to the output directory: {yml}");
        }
    }
}
