using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Translation;
using CodeGen.Translation.Process;

namespace CodeGen.Hmi
{
    // One compiled recipe row, as the OPERATOR needs to read it.
    //
    // The controller needs six numbers per row; a person needs to know which thing moves, where it
    // is going and what is being waited for. Both come from the SAME typed RecipeArrays the compiler
    // produced - nothing here re-derives a recipe, re-parses a syslay literal or invents a value.
    //
    // Resolved says whether every reference in the row was resolvable. A row that names a target no
    // component answers to, a slot no reporter owns, or a state the twin never declared is marked
    // and reported: rendering a plausible label for it would be worse than saying nothing, because
    // the operator cannot tell the difference.
    // What the row DOES. The compiled recipe encodes a phase announcement and an actuator
    // command as the same StepType, so the distinction has to be carried, not re-inferred.
    // BARCODE IS NOT IMPLEMENTED, AND THIS IS THE PLACE IT WOULD HAVE TO LAND.
    //
    // A barcode scan selects WHICH recipe to run. The rows below are the recipe the Mapper already
    // compiled from the twin: one fixed sequence per process, addressed only by row index, with no
    // product identity anywhere in RecipeArrays and no way for anything outside the controller to
    // choose a starting row. So there is nothing here for a scanner to select between, and a barcode
    // screen drawn against this model could only ever display a string.
    //
    // Making it real is a CONTROL contract change, not a presentation one. It needs a product/recipe
    // input carrying PartNumber, RecipeID, StartIndex and StepCount, an engine that can be commanded
    // to a row rather than always entering at 0, and a Mapper that compiles one recipe table per
    // product. All three are outside this task's write boundary (they are FB internals and recipe
    // generation), so no barcode capability is claimed, offered, or half-built.

    internal enum HmiRowKind { Command, Phase, Wait, End, Unresolved }

    internal sealed record HmiRecipeRow(
        int Index,
        int StepType,
        HmiRowKind Kind,
        int PhaseValue,
        string PhaseName,
        string? CmdTargetKey,          // the ring/protocol key the recipe actually carries
        string? CmdTargetInstance,     // the emitted instance it resolves to, when it is a component
        string? CmdTargetDisplay,      // the operator label for it
        int CmdState,
        string? CmdStateName,
        int WaitSlot,
        string? WaitSourceKey,         // the instance that owns the slot, on the consuming ring
        string? WaitSourceDisplay,
        int WaitState,
        string? WaitStateName,
        // The wait source is owned by a DIFFERENT ring: a handshake carried across by transport.
        bool WaitCrossRing,
        int NextStep,
        string Text,
        bool Resolved);

    // Turns typed RecipeArrays into presentation rows. The ONE place a recipe becomes words.
    //
    // Every resolver it uses is the existing one: the command target through the same ring-key match
    // the role derivation uses, the wait source through HmiSlotIndex (so a slot is read on its own
    // ring and never globally), the state names through the component's own declared states.
    internal static class HmiRecipePresenter
    {
        internal static IReadOnlyList<HmiRecipeRow> Rows(
            RecipeArrays arrays,
            string processInstance,
            IReadOnlyList<HmiComponent> components,
            // Every process's own phase table, keyed by instance. A phase number means
            // nothing outside the process that declared it.
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> phasesOf,
            HmiSlotIndex rings,
            HmiDefinition def,
            List<string> diagnostics)
        {
            var text = def.Screens.RecipeText;
            var rows = new List<HmiRecipeRow>(arrays.Count);

            for (var i = 0; i < arrays.Count; i++)
            {
                var type = arrays.StepType[i];
                var phase = i < arrays.ProcessStateByRow.Count ? arrays.ProcessStateByRow[i] : -1;
                var phaseName = arrays.ProcessPhaseNames.TryGetValue(phase, out var pn) ? pn : string.Empty;

                var target = (arrays.CmdTargetName.ElementAtOrDefault(i) ?? string.Empty).Trim();
                var cmdState = arrays.CmdStateArr.ElementAtOrDefault(i);
                var slot = arrays.Wait1Id.ElementAtOrDefault(i);
                var waitState = arrays.Wait1State.ElementAtOrDefault(i);
                var next = arrays.NextStep.ElementAtOrDefault(i);

                string? tgtKey = null, tgtInst = null, tgtName = null, tgtState = null;
                string? srcKey = null, srcName = null, srcState = null;
                string line;
                var resolved = true;
                var crossRing = false;
                HmiRowKind kind;

                if (type == StepType.End)
                {
                    kind = HmiRowKind.End;
                    line = text.End;
                }
                else if (type == StepType.Cmd)
                {
                    tgtKey = target;
                    var comp = Component(target, components);
                    if (comp != null)
                    {
                        kind = HmiRowKind.Command;
                        tgtInst = comp.InstanceName;
                        tgtName = comp.DisplayName;
                        tgtState = Named(comp, cmdState);
                        line = tgtState == null
                            ? Unresolved(text, ref resolved, diagnostics,
                                $"'{processInstance}' row {i} commands '{target}' to state {cmdState}, " +
                                "which the twin does not name")
                            : Fill(text.Command, ("target", tgtName), ("state", tgtState));
                    }
                    else if (phasesOf.TryGetValue(target, out var ownPhases))
                    {
                        // A process announcing its own phase. The value IS the phase number, so the
                        // process's own phase table is what names it - never a component's states.
                        kind = HmiRowKind.Phase;
                        tgtName = HmiPlanner.Humanise(target);
                        tgtState = ownPhases.TryGetValue(cmdState, out var an) ? an : null;
                        line = tgtState == null
                            ? Unresolved(text, ref resolved, diagnostics,
                                $"'{processInstance}' row {i} announces phase {cmdState}, which the " +
                                "compiled phase table does not name")
                            : Fill(text.Phase, ("phase", tgtState));
                    }
                    else
                    {
                        kind = HmiRowKind.Unresolved;
                        line = Unresolved(text, ref resolved, diagnostics,
                            $"'{processInstance}' row {i} commands '{target}', which is neither a " +
                            "component of this model nor one of its processes");
                    }
                }
                else if (type == StepType.Wait)
                {
                    kind = HmiRowKind.Wait;
                    srcKey = rings.Resolve(processInstance, slot);

                    // A handshake legitimately waits on a slot another ring writes. Naming it is
                    // safe only when exactly one instance anywhere owns that number.
                    if (srcKey == null)
                    {
                        srcKey = rings.ResolveAnywhere(slot);
                        crossRing = srcKey != null;
                    }

                    if (srcKey == null)
                    {
                        line = Unresolved(text, ref resolved, diagnostics,
                            $"'{processInstance}' row {i} waits on slot {slot}, which no reporter owns " +
                            $"on ring '{rings.RingOf(processInstance)}' and which more than one " +
                            "instance claims elsewhere");
                    }
                    else
                    {
                        var src = components.FirstOrDefault(c =>
                            string.Equals(c.InstanceName, srcKey, StringComparison.Ordinal));
                        srcName = src?.DisplayName ?? HmiPlanner.Humanise(srcKey);
                        // A process slot names a PHASE, a component slot names a STATE. Reading a
                        // phase number out of a component's state table would label it confidently
                        // and wrongly, which is the one outcome worth refusing.
                        srcState = src != null
                            ? Named(src, waitState)
                            : phasesOf.TryGetValue(srcKey, out var srcPhases)
                                ? (srcPhases.TryGetValue(waitState, out var wn) ? wn : null)
                                : null;

                        line = srcState == null
                            ? Unresolved(text, ref resolved, diagnostics,
                                $"'{processInstance}' row {i} waits for '{srcKey}' state {waitState}, " +
                                "which the model does not name")
                            : Fill(text.Wait, ("source", srcName), ("state", srcState));
                    }
                }
                else
                {
                    kind = HmiRowKind.Unresolved;
                    line = Unresolved(text, ref resolved, diagnostics,
                        $"'{processInstance}' row {i} declares step type {type}, which this build " +
                        "has no presentation for");
                }

                if (!resolved) kind = HmiRowKind.Unresolved;
                rows.Add(new HmiRecipeRow(
                    i, type, kind, phase, phaseName,
                    tgtKey, tgtInst, tgtName, cmdState, tgtState,
                    slot, srcKey, srcName, waitState, srcState, crossRing,
                    next, line, resolved));
            }

            return rows;
        }

        // The instances this recipe COMMANDS - derived from the rows, so it cannot disagree with them.
        internal static IReadOnlyList<string> Owned(IReadOnlyList<HmiRecipeRow> rows) =>
            rows.Where(r => r.Kind == HmiRowKind.Command && r.CmdTargetInstance != null)
                .Select(r => r.CmdTargetInstance!)
                .Distinct(StringComparer.Ordinal).ToList();

        // The instances this recipe only OBSERVES: waited on, never commanded.
        internal static IReadOnlyList<string> Observed(
            IReadOnlyList<HmiRecipeRow> rows, IReadOnlyList<HmiComponent> components)
        {
            var commanded = rows.Where(r => r.Kind == HmiRowKind.Command)
                .Select(r => r.CmdTargetInstance)
                .Where(x => x != null).ToHashSet(StringComparer.Ordinal)!;

            return rows.Where(r => r.StepType == StepType.Wait && r.WaitSourceKey != null)
                .Select(r => r.WaitSourceKey!)
                .Where(n => !commanded.Contains(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        // The phases this recipe passes through, in the order the rows reach them.
        internal static IReadOnlyList<HmiStateName> Phases(IReadOnlyList<HmiRecipeRow> rows)
        {
            var seen = new List<HmiStateName>();
            foreach (var r in rows.Where(r => r.PhaseName.Length > 0))
                if (!seen.Any(s => s.Value == r.PhaseValue))
                    seen.Add(new HmiStateName(r.PhaseValue, r.PhaseName));
            return seen;
        }

        // The twin's OWN name for a state value, or null when it declares none.
        //
        // HmiComponent.StateName falls back to the number so a tile always has something to draw.
        // That fallback is exactly wrong here: "Wait until Slide is 42" reads like a label and is
        // indistinguishable from a real one, so a miss has to be a miss.
        private static string? Named(HmiComponent c, int value) =>
            c.States.FirstOrDefault(s => s.Value == value)?.Name;

        // The ONE command-target resolver: the ring key first (that is what the recipe carries),
        // then the instance name (sensor refresh rows carry the verbatim name).
        private static HmiComponent? Component(string target, IReadOnlyList<HmiComponent> components)
        {
            var t = target.Trim();
            if (t.Length == 0) return null;
            return components.FirstOrDefault(c =>
                       string.Equals(TemplateMap.RingKey(c.InstanceName), t, StringComparison.Ordinal))
                ?? components.FirstOrDefault(c =>
                       string.Equals(c.InstanceName, t, StringComparison.OrdinalIgnoreCase));
        }

        private static string Unresolved(
            HmiRecipeTextPolicy text, ref bool resolved, List<string> diagnostics, string why)
        {
            resolved = false;
            diagnostics.Add(why + " - the row is marked unresolved rather than given a plausible label.");
            return text.Unresolved;
        }

        private static string Fill(string template, params (string Key, string Value)[] parts) =>
            parts.Aggregate(template, (acc, p) => acc.Replace("{" + p.Key + "}", p.Value, StringComparison.Ordinal));
    }
}
