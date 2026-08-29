using System;

namespace CodeGen.Models
{
    // The Control.xml <Type> vocabulary the SHIPPED schema maps, and the predicates over a component's
    // resolved KIND.
    //
    // The constants are the tokens Config/twin-schema.yml declares. They are spelled here because a
    // twin built in code has to state a token, not because the compiler classifies by them: the reader
    // resolves the token to a ComponentKind through the declaration, and these predicates read THAT.
    // So a twin that spells a role differently is a one-line declaration rather than a code change,
    // and no consumer can classify the same component differently from any other.
    public static class ComponentType
    {
        public const string Actuator   = "Actuator";
        public const string Sensor     = "Sensor";
        public const string Process    = "Process";
        public const string Robot      = "Robot";
        public const string NonControl = "NonControl";

        public static bool Is(VueOneComponent? c, string type) =>
            c != null && string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase);

        // Driven components. Robot-typed grippers and the task arm are actuators here because the
        // declaration says so - the predecessor answered this two ways (`Type == "Actuator"` in the
        // interlock encoder, `!IsProcess && !IsSensor` on the IR), so a Robot used as an interlock
        // source had its blocked state written in a vocabulary the CAT never publishes.
        public static bool IsActuator(VueOneComponent? c) => c?.Kind == ComponentKind.Actuator;
        public static bool IsSensor(VueOneComponent? c)   => c?.Kind == ComponentKind.Sensor;
        public static bool IsProcess(VueOneComponent? c)  => c?.Kind == ComponentKind.Process;
    }
}
