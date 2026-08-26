using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation.Interlocks
{
    // What value a rule should be written against, given the twin's own numbering. A twin may number a
    // place the CAT only passes through - a five-state cylinder's returned-complete rest - and a rule
    // written against that number could never match, because the core has already settled away from it
    // by the time anything reads the slot. Which numbers name which stop is the CAT's DECLARATION, so
    // this asks the protocol rather than carrying a constant of its own.
    internal static class ActuatorStateEncoding
    {
        // The stop every CAT settles to at rest, in the shared stop vocabulary.
        public const int Home = 0;

        public static int Settled(VueOneComponent? source, int stateNumber,
            IReadOnlyDictionary<string, string> catTypes, Mapping.TemplateIndex manifest)
        {
            if (!ComponentType.IsActuator(source)) return stateNumber;
            if (!catTypes.TryGetValue((source!.Name ?? string.Empty).Trim(), out var cat)) return stateNumber;
            var protocol = manifest.ProtocolOrNull(cat);
            var stop = protocol?.StopFor(stateNumber);
            // A number the CAT gives no stop is a motion state or outside its vocabulary; either way it
            // is not a rule's business and is left exactly as the twin wrote it.
            return stop == null ? stateNumber : protocol!.SettledFor(stop);
        }

        // The same question asked of a STATE rather than a bare number, which is what a CAT declaring
        // geometric stops needs: a twin may re-visit one physical place under two branch numberings, and
        // then the place - the <Position> - is the identity, not the number beside it.
        //
        // Which of the two it is, is the CAT's declaration (StopsAreGeometric), so it is asked here
        // rather than decided again by every pass that needs a stop.
        public static int StopAt(VueOneComponent? source, VueOneState state,
            IReadOnlyDictionary<string, string> catTypes, Mapping.TemplateIndex manifest) =>
            Settled(source, CanonicalNumber(source, state, catTypes, manifest), catTypes, manifest);

        // The number the twin gives the FIRST state declared at this place. Under a geometric CAT two
        // states at one position are one stop, so they must resolve to one number or a rule written
        // against one branch would not match the other.
        public static int CanonicalNumber(VueOneComponent? source, VueOneState state,
            IReadOnlyDictionary<string, string> catTypes, Mapping.TemplateIndex manifest)
        {
            if (source == null || !Geometric(source, catTypes, manifest)) return state.StateNumber;
            var first = source.States
                .FirstOrDefault(s => s.StaticState && s.Position == state.Position);
            return first?.StateNumber ?? state.StateNumber;
        }

        // Declared by the CAT the component is deployed as, never inferred from how its states look.
        // The CAT is the one the PLAN selected, read from the same map every other pass reads, so this
        // cannot resolve a component to a different CAT than the rest of the run does.
        public static bool Geometric(VueOneComponent? source,
            IReadOnlyDictionary<string, string> catTypes, Mapping.TemplateIndex manifest)
        {
            if (!ComponentType.IsActuator(source)) return false;
            return catTypes.TryGetValue((source!.Name ?? string.Empty).Trim(), out var cat)
                   && manifest.ProtocolOrNull(cat) is { StopsAreGeometric: true };
        }
    }
}
