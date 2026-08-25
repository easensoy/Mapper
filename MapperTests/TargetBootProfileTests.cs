using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    /// EAE identifies an FB by its ID, so a deployed resource's boot ids can never be regenerated: a new
    /// one makes EAE treat it as a new instance and strand the old. The profile that carries them is
    /// therefore checked before a plan exists, which is before anything is written.
    public sealed class TargetBootProfileTests
    {
        private static List<BootFbDeclaration> Sequence() => new()
        {
            new BootFbDeclaration
            { Role = "FB1", Type = "DPAC_FULLINIT", Namespace = "SE.DPAC", LayoutKey = "FB1" },
            new BootFbDeclaration
            { Role = "FB2", Type = "plcStart", Namespace = "SE.AppBase", LayoutKey = "FB2" },
        };

        private static TargetIdentity Target(PlcAssignment plc, params (string Role, string Id)[] boot) => new()
        {
            Plc = plc,
            ResourceName = plc + "_RES",
            DeviceType = "Some_dPAC",
            BootFbs = boot.Select(b => new TargetBootFb { Role = b.Role, Id = b.Id }).ToList(),
        };

        private static List<string> Errors(
            IEnumerable<TargetIdentity> targets, List<BootFbDeclaration>? sequence = null) =>
            TargetRegistry.BootProfileErrors(targets.ToList(), sequence ?? Sequence()).ToList();

        [Fact]
        public void A_complete_profile_raises_nothing()
        {
            Assert.Empty(Errors(new[]
            {
                Target(PlcAssignment.M262, ("FB1", "0123456789ABCDEF"), ("FB2", "FEDCBA9876543210")),
                Target(PlcAssignment.M580, ("FB1", "1111111111111111"), ("FB2", "2222222222222222")),
            }));
        }

        [Fact]
        public void A_role_the_sequence_declares_and_the_target_does_not_is_refused()
        {
            var errors = Errors(new[]
            {
                Target(PlcAssignment.M262, ("FB1", "0123456789ABCDEF"), ("FB9", "FEDCBA9876543210")),
            });
            Assert.Contains(errors, e => e.Contains("FB2") && e.Contains("exactly one"));
        }

        [Fact]
        public void A_target_that_declares_the_wrong_number_of_boot_fbs_is_refused()
        {
            var errors = Errors(new[] { Target(PlcAssignment.BX1, ("FB1", "0123456789ABCDEF")) });
            Assert.Contains(errors, e => e.Contains("declares 1 bootFbs") && e.Contains("2 role(s)"));
        }

        [Fact]
        public void One_id_on_two_targets_is_refused_because_EAE_loads_them_as_one_FB()
        {
            var errors = Errors(new[]
            {
                Target(PlcAssignment.M262, ("FB1", "0123456789ABCDEF"), ("FB2", "FEDCBA9876543210")),
                Target(PlcAssignment.RevPi, ("FB1", "0123456789ABCDEF"), ("FB2", "2222222222222222")),
            });
            Assert.Contains(errors, e => e.Contains("0123456789ABCDEF") && e.Contains("M262") &&
                                         e.Contains("RevPi"));
        }

        [Theory]
        [InlineData("")]                    // absent
        [InlineData("0123456789ABCDE")]     // too short
        [InlineData("0123456789ABCDEF0")]   // too long
        [InlineData("0123456789abcdef")]    // lower case: EAE writes upper
        [InlineData("0123456789ABCDEZ")]    // not hex
        public void A_malformed_id_is_refused(string id)
        {
            var errors = Errors(new[] { Target(PlcAssignment.M580, ("FB1", id), ("FB2", "2222222222222222")) });
            Assert.Contains(errors, e => e.Contains("not a 16-character upper-case hex"));
        }

        [Fact]
        public void A_sequence_with_no_roles_is_refused_rather_than_booting_nothing()
        {
            var errors = Errors(new[] { Target(PlcAssignment.M262) }, new List<BootFbDeclaration>());
            Assert.Contains(errors, e => e.Contains("no bootSequence"));
        }

        [Fact]
        public void A_sequence_role_missing_its_shape_is_refused()
        {
            var sequence = Sequence();
            sequence[1].LayoutKey = string.Empty;
            var errors = Errors(new[]
            {
                Target(PlcAssignment.M262, ("FB1", "0123456789ABCDEF"), ("FB2", "FEDCBA9876543210")),
            }, sequence);
            Assert.Contains(errors, e => e.Contains("FB2") && e.Contains("layoutKey"));
        }

        private static List<BringUpWire> Wires(params (string From, string To)[] rows) =>
            rows.Select(r => new BringUpWire { From = r.From, To = r.To }).ToList();

        private static List<string> WireErrors(List<BringUpWire> wires) =>
            TargetRegistry.BringUpErrors(wires, Sequence()).ToList();

        [Fact]
        public void A_bring_up_naming_only_declared_roles_raises_nothing()
        {
            Assert.Empty(WireErrors(Wires(
                ("START.COLD", "FB1.INIT"), ("FB2.FIRST_INIT", "FB2.ACK_FIRST"))));
        }

        [Fact]
        public void A_wire_to_a_role_no_target_boots_with_is_refused()
        {
            var errors = WireErrors(Wires(("START.COLD", "FB9.INIT")));
            Assert.Contains(errors, e => e.Contains("FB9") && e.Contains("no bootSequence role"));
        }

        [Theory]
        [InlineData("FB1")]        // no port
        [InlineData(".INIT")]      // no role
        [InlineData("FB1.")]       // port missing after the dot
        public void A_malformed_endpoint_is_refused(string endpoint)
        {
            var errors = WireErrors(Wires(("START.COLD", endpoint)));
            Assert.Contains(errors, e => e.Contains("is not a") && e.Contains("<role>.<PORT>"));
        }

        [Fact]
        public void The_same_wire_declared_twice_is_refused()
        {
            var errors = WireErrors(Wires(("START.COLD", "FB1.INIT"), ("START.COLD", "FB1.INIT")));
            Assert.Contains(errors, e => e.Contains("more than once"));
        }

        [Fact]
        public void Two_wires_into_one_destination_are_kept_because_cold_and_warm_both_start_it()
        {
            Assert.Empty(WireErrors(Wires(("START.COLD", "FB1.INIT"), ("START.WARM", "FB1.INIT"))));
        }

        [Fact]
        public void A_bring_up_with_no_wires_is_refused_rather_than_starting_nothing()
        {
            Assert.Contains(WireErrors(new List<BringUpWire>()), e => e.Contains("no bringUp"));
        }

        [Fact]
        public void The_shipped_bring_up_is_ordered_and_every_endpoint_resolves()
        {
            var wires = DeviceConfig.Current.BringUp;
            Assert.NotEmpty(wires);
            Assert.Empty(TargetRegistry.BringUpErrors(wires, DeviceConfig.Current.BootSequence));
            // Emission order is the artefact's order, so the rendered pairs follow the declaration.
            Assert.Equal(
                wires.Select(w => (w.From, w.To)).ToList(),
                TargetBootstrap.BringUp.ToList());
        }

        [Fact]
        public void Every_shipped_target_carries_a_complete_boot_profile()
        {
            // The real device.yml, joined and validated: each target answers every declared role, in order.
            var roles = DeviceConfig.Current.BootSequence.Select(b => b.Role).ToList();
            Assert.NotEmpty(roles);
            foreach (var target in TargetRegistry.All)
            {
                Assert.Equal(roles, target.BootFbs.Select(b => b.Role).ToList());
                Assert.All(target.BootFbs, b => Assert.Matches("^[0-9A-F]{16}$", b.Id));
            }
            // and no id is shared, which EAE would load as a single FB.
            var ids = TargetRegistry.All.SelectMany(t => t.BootFbs).Select(b => b.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }
}
