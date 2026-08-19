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
        [Theory]
        [InlineData(PlcAssignment.M262, "M262_RES", "M262_dPAC")]
        [InlineData(PlcAssignment.M580, "RES0", "M580_dPAC")]
        [InlineData(PlcAssignment.BX1, "BX1_RES", "Soft_dPAC")]
        [InlineData(PlcAssignment.RevPi, "RevPi_RES", "Soft_dPAC")]
        public void RegisteredTargetsCarryTheirDeviceFacts(
            PlcAssignment plc, string resource, string deviceType)
        {
            var t = TargetRegistry.Of(plc);
            Assert.Equal(resource, t.ResourceName);
            Assert.Equal(deviceType, t.DeviceType);
            Assert.False(string.IsNullOrWhiteSpace(t.ResourceName));
        }

        [Fact]
        public void TwoTargetsSharingADeviceTypeAreDisambiguatedByName()
        {
            // BX1 and the RevPi are both Soft_dPAC, so Type alone cannot find either device.
            Assert.Equal(TargetRegistry.Of(PlcAssignment.BX1).DeviceType,
                         TargetRegistry.Of(PlcAssignment.RevPi).DeviceType);
            Assert.NotEqual(TargetRegistry.Of(PlcAssignment.BX1).DeviceName,
                            TargetRegistry.Of(PlcAssignment.RevPi).DeviceName);
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
