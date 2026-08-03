using System;

namespace CodeGen.Models
{
    // The Control.xml <Type> vocabulary. These are the twin schema's own names, not rig data — but
    // every consumer that asks "is this an actuator / a sensor / a process" has to agree on them, so
    // they are spelled once here instead of being re-quoted at each comparison.
    public static class ComponentType
    {
        public const string Actuator   = "Actuator";
        public const string Sensor     = "Sensor";
        public const string Process    = "Process";
        public const string Robot      = "Robot";
        public const string NonControl = "NonControl";

        public static bool Is(VueOneComponent? c, string type) =>
            c != null && string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase);

        public static bool IsActuator(VueOneComponent? c) => Is(c, Actuator);
        public static bool IsSensor(VueOneComponent? c)   => Is(c, Sensor);
        public static bool IsProcess(VueOneComponent? c)  => Is(c, Process);

    }
}
