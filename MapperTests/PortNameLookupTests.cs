using MapperUI.Services;
using Xunit;

namespace MapperTests
{
    public class PortNameLookupTests
    {
        // [Fact]
        public void Process1GenericReturnsStateRptCmdAdptrPorts()
        {
            Assert.Equal("stateRptCmdAdptr_out", SystemInjector.StateRprtOut("Process1_Generic"));
            Assert.Equal("stateRptCmdAdptr_in", SystemInjector.StateRprtIn("Process1_Generic"));
        }

        // [Fact]
        public void FiveStateActuatorReturnsStateRprtCmdPorts()
        {
            Assert.Equal("stateRprtCmd_out", SystemInjector.StateRprtOut("Five_State_Actuator_CAT"));
            Assert.Equal("stateRprtCmd_in", SystemInjector.StateRprtIn("Five_State_Actuator_CAT"));
        }

        // [Fact]
        public void SensorBoolReturnsStateRprtCmdPorts()
        {
            Assert.Equal("stateRprtCmd_out", SystemInjector.StateRprtOut("Sensor_Bool_CAT"));
            Assert.Equal("stateRprtCmd_in", SystemInjector.StateRprtIn("Sensor_Bool_CAT"));
        }
    }
}
