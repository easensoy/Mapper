using CodeGen.Translation;

namespace CodeGen.Mapping
{
    // Which EAE device each controller deploys onto. The sysdev Type alone does not identify a device --
    // BX1 and the RevPi are both Soft_dPAC -- so a name disambiguates the two that share one.
    public static class PlcTargets
    {
        public const string DeviceNamespace = "SE.DPAC";

        public static string DeviceType(PlcAssignment plc) => plc switch
        {
            PlcAssignment.M262  => "M262_dPAC",
            PlcAssignment.M580  => "M580_dPAC",
            PlcAssignment.BX1   => "Soft_dPAC",
            PlcAssignment.RevPi => "Soft_dPAC",
            _ => string.Empty,
        };

        // Null where the Type already identifies the device on its own.
        public static string? DeviceName(PlcAssignment plc) => plc switch
        {
            PlcAssignment.BX1   => "BX1",
            PlcAssignment.RevPi => "Revolution_Pi",
            _ => null,
        };
    }
}
