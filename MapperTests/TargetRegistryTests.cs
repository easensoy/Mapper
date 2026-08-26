using System;
using CodeGen.Mapping;
using CodeGen.Translation;
using Xunit;

namespace MapperTests
{
    // The registry is the one place that answers "what is this controller?", so an unregistered one has
    // to be an error rather than a blank that flows downstream as a device with no resource.
    public class TargetRegistryTests
    {
        // Over the DECLARED targets, not a list repeated here: a target added to device.yml is covered
        // by this test the moment it is declared, which is the point of the set being open.
        [Fact]
        public void EveryRegisteredTargetCarriesItsDeviceFacts()
        {
            Assert.NotEmpty(TargetRegistry.All);
            foreach (var t in TargetRegistry.All)
            {
                Assert.Same(t, TargetRegistry.Of(t.Plc));
                Assert.False(string.IsNullOrWhiteSpace(t.ResourceName));
                Assert.False(string.IsNullOrWhiteSpace(t.DeviceType));
                Assert.True(t.Plc.IsKnown);
            }
        }

        [Fact]
        public void TwoTargetsSharingADeviceTypeAreDisambiguatedByName()
        {
            // BX1 and the RevPi are both Soft_dPAC, so Type alone cannot find either device.
            Assert.Equal(TargetRegistry.Of(PlcAssignment.Named("BX1")).DeviceType,
                         TargetRegistry.Of(PlcAssignment.Named("RevPi")).DeviceType);
            Assert.NotEqual(TargetRegistry.Of(PlcAssignment.Named("BX1")).DeviceName,
                            TargetRegistry.Of(PlcAssignment.Named("RevPi")).DeviceName);
        }

        [Fact]
        public void AnUnknownTargetThrowsRatherThanReturningBlank()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => TargetRegistry.Of(PlcAssignment.Unknown));
            Assert.Contains("not a supported deployment target", ex.Message);
        }

        [Fact]
        public void ExactlyOneTargetIsTheDefaultFeedHost()
        {
            // The Feed station has one home when nothing has been relocated; a second would make the
            // default ambiguous.
            Assert.True(TargetRegistry.IsRegistered(TargetRegistry.FeedTarget));
            Assert.True(TargetRegistry.Of(TargetRegistry.FeedTarget).HostsFeedStation);
            Assert.False(TargetRegistry.Of(TargetRegistry.FeedTarget).ReceivesRelocatedComponents);
        }
    }
}
