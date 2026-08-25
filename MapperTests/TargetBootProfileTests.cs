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
