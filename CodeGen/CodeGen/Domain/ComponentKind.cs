namespace CodeGen.Models
{
    // WHAT a Control.xml component is to this compiler, independent of the token the twin spells.
    //
    // VueOne writes several tokens for things the compiler drives identically - Actuator and Robot are
    // both commanded over the report ring and both settle through a CAT protocol - and a twin from
    // another plant may spell them differently again. So the token is mapped to a KIND by declaration
    // (Config/twin-schema.yml) and every consumer asks about the kind.
    //
    // Nothing infers a kind. The predecessor was `!IsProcess && !IsSensor`, which made every token the
    // compiler did not recognise an actuator: a typo, a new VueOne role, or a token from a foreign twin
    // all became something the planner would try to command. An unmapped token is now refused by name.
    public enum ComponentKind
    {
        // Not a control component. Read, reported in the count, then dropped before a plan exists.
        Excluded = 0,

        // Driven by the compiler: commanded over the report ring, settles through a CAT protocol.
        Actuator,

        // Observed only: publishes a state onto the ring, is never commanded.
        Sensor,

        // Carries a recipe: commands actuators and waits on their reports.
        Process,
    }
}
