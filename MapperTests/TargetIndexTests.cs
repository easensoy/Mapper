using System;
using CodeGen.Mapping;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    // The registry is the one place that answers "what is this controller?", so an unregistered one has
    // to be an error rather than a blank that flows downstream as a device with no resource.
    public class TargetIndexTests
    {
        // Over the DECLARED targets, not a list repeated here: a target added to device.yml is covered
        // by this test the moment it is declared, which is the point of the set being open.
        [Fact]
        public void EveryRegisteredTargetCarriesItsDeviceFacts()
        {
            Assert.NotEmpty(TestConfig.Cfg.Targets.All);
            foreach (var t in TestConfig.Cfg.Targets.All)
            {
                Assert.Same(t, TestConfig.Cfg.Targets.Of(t.Plc));
                Assert.False(string.IsNullOrWhiteSpace(t.ResourceName));
                Assert.False(string.IsNullOrWhiteSpace(t.DeviceType));
                Assert.True(t.Plc.IsKnown);
            }
        }

        [Fact]
        public void TwoTargetsSharingADeviceTypeAreDisambiguatedByName()
        {
            // BX1 and the RevPi are both Soft_dPAC, so Type alone cannot find either device.
            Assert.Equal(TestConfig.Cfg.Targets.Of(PlcAssignment.Named("BX1")).DeviceType,
                         TestConfig.Cfg.Targets.Of(PlcAssignment.Named("RevPi")).DeviceType);
            Assert.NotEqual(TestConfig.Cfg.Targets.Of(PlcAssignment.Named("BX1")).DeviceName,
                            TestConfig.Cfg.Targets.Of(PlcAssignment.Named("RevPi")).DeviceName);
        }

        [Fact]
        public void AnUnknownTargetThrowsRatherThanReturningBlank()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => TestConfig.Cfg.Targets.Of(PlcAssignment.Unknown));
            Assert.Contains("not a supported deployment target", ex.Message);
        }

        [Fact]
        public void ExactlyOneTargetIsTheDefaultFeedHost()
        {
            // The Feed station has one home when nothing has been relocated; a second would make the
            // default ambiguous.
            Assert.True(TestConfig.Cfg.Targets.IsRegistered(TestConfig.Cfg.Targets.FeedTarget));
            Assert.True(TestConfig.Cfg.Targets.Of(TestConfig.Cfg.Targets.FeedTarget).HostsFeedStation);
            Assert.False(TestConfig.Cfg.Targets.Of(TestConfig.Cfg.Targets.FeedTarget).ReceivesRelocatedComponents);
        }
    }
}
