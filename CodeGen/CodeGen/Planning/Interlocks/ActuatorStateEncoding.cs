using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation.Interlocks
{
    // The state numbers a CAT actually reports on the ring, and the one folding rule that follows from
    // them. This is the generated FB contract rather than rig data, so it is named once here instead of
    // appearing as bare 0/2/4 at each comparison.
    internal static class ActuatorStateEncoding
    {
        public const int Home = 0;
        public const int Work = 2;
        public const int ReturnedFinished = 4;

        // A five-state actuator passes through ReturnedFinished only momentarily before settling to
        // Home, so a rule written against it could never match — report the stable value instead. The
        // centre-home swivel is excluded: its 4 is Work2, a real stop, so "block while it is at Work2"
        // has to survive.
        public static int Settled(VueOneComponent? source, int stateNumber) =>
            stateNumber == ReturnedFinished &&
            ComponentType.IsActuator(source) &&
            !TemplateMap.IsBranchedSevenState(source!)
                ? Home
                : stateNumber;
    }
}
