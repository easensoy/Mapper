using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Models;
using System.IO;
using System.Text.Json;
using CodeGen.Configuration;

namespace CodeGen.Translation.Process.Recipes
{
    // Deterministic Control.xml -> recipe rows.
    //
    // COMMAND OWNERSHIP. Who moves an actuator is declared by the ACTUATOR: a transition whose
    // Sequence_Condition names Process/State means that process state issues the command driving it. A
    // condition on a PROCESS transition naming an actuator state is an observation, not an instruction.
    //
    // PHASE PROTOCOL. A process announces its chain's entry state and each state a peer waits on, so a
    // consumer waits for the entry phase and THEN the referenced phase and a value left behind last cycle
    // cannot release it. Where an announcement has no route here the material sensor may stand in.
    internal static class ProcessCompiler
    {
        public sealed class Ctx
        {
            public IReadOnlyDictionary<string, int> Ids = new Dictionary<string, int>();          // ComponentID -> state_table slot (scoped + deployment)
            public IReadOnlyDictionary<string, int> IdsByName = new Dictionary<string, int>();     // component name -> slot (deployment-allocated peers)
            public IReadOnlyDictionary<string, int> ProcessIdByName = new Dictionary<string, int>();
            public IReadOnlyDictionary<string, int> SensorPresent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // Which controller hosts each component, and so which report ring it publishes onto.
            public CodeGen.Domain.Twin.TwinModel Twin = null!;
            public ReportGraph Rings = null!;

            // CAT, command protocol and command sequence are decided by the plan, never by the CAT router.
            public IReadOnlyDictionary<string, string> CatType = new Dictionary<string, string>();
            public IReadOnlyDictionary<string, CatProtocol> Protocol = new Dictionary<string, CatProtocol>();
            // Components whose CAT runs a declared sequence instead of being walked to a numbered stop.
            public IReadOnlyDictionary<string, CatExecution> Execution = new Dictionary<string, CatExecution>();

            public CatExecution? ExecutionOf(VueOneComponent? c) =>
                c != null && Execution.TryGetValue((c.Name ?? string.Empty).Trim(), out var e) ? e : null;

            public string CatTypeOf(VueOneComponent c) =>
                CatType.TryGetValue((c.Name ?? string.Empty).Trim(), out var t) ? t : string.Empty;

            public CatProtocol? ProtocolOrNull(VueOneComponent? c) =>
                c != null && Protocol.TryGetValue((c.Name ?? string.Empty).Trim(), out var p) ? p : null;

            public CatProtocol ProtocolOf(VueOneComponent c) =>
                ProtocolOrNull(c) ?? throw new InvalidOperationException(
                    $"[CAT] '{CatTypeOf(c)}' declares no command protocol, so nothing can say which value " +
                    "drives it or which value means it arrived.");
            // Every guard leaf and what became of it. Filled as the compiler lowers; the plan then
            // proves it accounts for every leaf the twin declares.
            public readonly GuardCoverage Coverage = new();

            // What this deployment says a cross-process reference means where a plain wait will not do.
            public Configuration.HandoffPolicy Handoff = new();

            // A declared carrier's state_table slot, resolved once. Empty where nothing is declared.
            public IReadOnlyDictionary<string, int> CarrierSlots =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Each process's validated control flow, resolved once. Nothing walks a state machine for
            // itself: successor, entry state and execution order are all asked of the graph.
            public IReadOnlyDictionary<string, CodeGen.Domain.Twin.ProcessGraph> Graphs =
                new Dictionary<string, CodeGen.Domain.Twin.ProcessGraph>(StringComparer.OrdinalIgnoreCase);

            public CodeGen.Domain.Twin.ProcessGraph GraphOf(VueOneComponent process) =>
                Graphs.TryGetValue((process.Name ?? string.Empty).Trim(), out var g) ? g
                : throw new InvalidOperationException(
                    $"[Compile] '{process.Name}' has no resolved control flow; the plan builds one per " +
                    "process before any recipe is compiled.");

            // What the backend could not represent exactly as the twin states it. Diagnostic only:
            // nothing here reaches an artefact, so a finding cannot move a byte.
            public readonly List<string> Findings = new();
        }

        public static RecipeArrays Compile(VueOneComponent process, Ctx ctx, ProcessHandoffPlan plan)
        {
            var arrays = new RecipeArrays();
            foreach (var kv in ctx.Ids) arrays.ComponentIds[kv.Key] = kv.Value;

            var graph = ctx.GraphOf(process);
            var states = graph.Ordered;
            foreach (var line in graph.TransitionTable()) arrays.TransitionTable.Add(line);
            // A state the entry cannot reach never runs, so its guards are not compiled. Said out loud:
            // silently omitting model content is how a compiler stops being one.
            foreach (var dead in graph.Unreachable)
            {
                ctx.Findings.Add(
                    $"'{process.Name}' state '{dead.Name}' is not reachable from '{graph.Entry.Name}', " +
                    "so it never executes and its guards are not compiled.");
                foreach (var t in dead.Transitions)
                    foreach (var leaf in Leaves(t))
                        ctx.Coverage.Record(Leaf(process, dead, t, leaf,
                            GuardLeafOutcome.Unreachable,
                            $"'{dead.Name}' is not reachable from '{graph.Entry.Name}'"));
            }
            var announce = plan.AnnouncementsOf(process.Name?.Trim() ?? string.Empty);
            var owned = BuildOwnership(process, ctx);

            var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var at = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);      // actuator -> the StateID it now rests in
            var graphs = new Dictionary<string, ActuatorGraph>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<Row>();
            var firstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // Producers this recipe has already proven to be inside a fresh cycle (see EmitHandoff).
            var armed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int idx = 0; idx < states.Count; idx++)
            {
                var state = states[idx];
                int before = rows.Count;
                // Own work, THEN announce, THEN wait: an announcement is a claim about the plant. The entry
                // phase announces after its conditions instead. See Docs/PATCH_RATIONALES P-1.
                bool entryPhase = idx == 0;
                void Announcement()
                {
                    if (!announce.TryGetValue(state.StateID, out var kind)) return;
                    // On the ring under its own name; cross-controller it also leaves on the phase transport.
                    if (kind.HasFlag(HandoffTransport.Ring))
                        rows.Add(Row.Cmd(process.Name?.Trim() ?? string.Empty, state.StateNumber, state.StateID));
                    if (kind.HasFlag(HandoffTransport.CrossController))
                        rows.Add(Row.Cmd(ProcessPhaseTransport.CommandToken, state.StateNumber, state.StateID));
                }
                // The movements this state owns; each ends in its command's arrival WAIT.
                void Work() => EmitOwnedMoves(process, state, owned, ctx, pos, at, graphs, rows);
                if (!entryPhase) { Work(); Announcement(); }
                foreach (var t in state.Transitions.OrderBy(t => t.Priority))
                {
                    // Two requirements naming the SAME component at the SAME stop are one requirement.
                    var settledHere = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    int leaves = t.Guard?.References().Count ?? 0;
                    EmitGuard(t.Guard, process, state, t, leaves, ctx, plan, pos, at, graphs, rows,
                        arrays, settledHere, owned, entryPhase, armed);
                }
                if (entryPhase) { Work(); Announcement(); }
                if (rows.Count > before) firstRow[state.StateID] = before;
            }

            Serialize(process, graph, rows, firstRow, arrays);
            arrays.OrderingSummary = $"'{process.Name}' compiled from Control.xml: {arrays.StepType.Count} rows.";
            return arrays;
        }

        // Every leaf a transition's guard declares, in document order.
        internal static IReadOnlyList<VueOneCondition> Leaves(VueOneTransition t) =>
            t.Guard?.References() ?? (IReadOnlyList<VueOneCondition>)Array.Empty<VueOneCondition>();

        // What makes a leaf that leaf: the IDs the twin assigns, plus where the leaf sits in its own
        // transition's guard. Computed in exactly one place so the declaring side and the deciding side
        // cannot disagree about which leaf they are talking about.
        internal static GuardLeafId LeafId(VueOneComponent process, VueOneState state,
            VueOneTransition edge, VueOneCondition cond)
        {
            var leaves = Leaves(edge);
            int ordinal = 0;
            for (int i = 0; i < leaves.Count; i++)
                if (ReferenceEquals(leaves[i], cond)) { ordinal = i; break; }
            return new GuardLeafId(
                process.ComponentID ?? string.Empty, state.StateID ?? string.Empty,
                edge.TransitionID ?? string.Empty, cond.ComponentID ?? string.Empty,
                cond.ID ?? string.Empty, ordinal);
        }

        private static GuardLeaf Leaf(VueOneComponent process, VueOneState state, VueOneTransition edge,
            VueOneCondition cond, GuardLeafOutcome outcome, string why) =>
            new(LeafId(process, state, edge, cond),
                process.Name ?? string.Empty, state.Name ?? string.Empty,
                cond.Name ?? string.Empty, outcome, why);

        // Every leaf the twin declares for one process, with the outcome a compiler would have to give
        // it. Used to prove the lowering accounted for all of them.
        internal static IReadOnlyList<GuardLeaf> DeclaredLeaves(VueOneComponent process) =>
            (process.States ?? new List<VueOneState>())
                .SelectMany(st => (st.Transitions ?? new List<VueOneTransition>())
                    .SelectMany(t => Leaves(t).Select(c =>
                        Leaf(process, st, t, c, GuardLeafOutcome.Waited, string.Empty))))
                .ToList();

        private sealed class OwnedMove
        {
            public VueOneComponent Actuator = null!;
            public string OriginStateId = string.Empty;
            public string DestinationStateId = string.Empty;
            public string TransitionId = string.Empty;
        }

        // Ownership from every actuator's transitions, indexed by owning process state so lookup is direct.
        private static Dictionary<string, List<OwnedMove>> BuildOwnership(VueOneComponent process, Ctx ctx)
        {
            var res = new Dictionary<string, List<OwnedMove>>(StringComparer.OrdinalIgnoreCase);
            foreach (var actuator in ctx.Twin.Components.Where(c => c.IsActuator).Select(c => c.Source))
            {
                foreach (var s in actuator.States)
                    foreach (var t in s.Transitions.OrderBy(t => t.Priority))
                        foreach (var cond in t.Guard?.References()
                            ?? (IReadOnlyList<VueOneCondition>)Array.Empty<VueOneCondition>())
                        {
                            var owner = TryResolve(cond, ctx.Twin);
                            if (owner == null || !IsProcess(owner) || !SameName(owner, process)) continue;

                            var ownerState = PeerState(owner, cond, ctx)
                                ?? throw Fail(process, null,
                                    $"'{actuator.Name}' transition '{t.TransitionID}' is owned by condition " +
                                    $"'{cond.Name}', which names no state of '{owner.Name}'");
                            if (string.IsNullOrWhiteSpace(t.DestinationStateID))
                                throw Fail(process, ownerState,
                                    $"owns '{actuator.Name}' transition '{t.TransitionID}', which declares no " +
                                    "destination state, so the commanded movement cannot be derived");

                            var list = res.TryGetValue(ownerState.StateID, out var l)
                                ? l : (res[ownerState.StateID] = new List<OwnedMove>());
                            // One movement restated twice in the model is one command, not two.
                            if (list.Any(m => string.Equals(m.Actuator.ComponentID, actuator.ComponentID, StringComparison.OrdinalIgnoreCase) &&
                                              string.Equals(m.DestinationStateId, t.DestinationStateID, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            list.Add(new OwnedMove
                            {
                                Actuator = actuator,
                                OriginStateId = string.IsNullOrWhiteSpace(t.OriginStateID) ? s.StateID : t.OriginStateID,
                                DestinationStateId = t.DestinationStateID,
                                TransitionId = t.TransitionID,
                            });
                        }
            }
            return res;
        }

        // SEVERAL movements of one actuator are a sequence, run by following the chain from where the recipe
        // last left it. Two legs leaving the SAME origin is a fork the model does not resolve.
        private static void EmitOwnedMoves(VueOneComponent process, VueOneState state,
            Dictionary<string, List<OwnedMove>> owned, Ctx ctx, Dictionary<string, int> pos,
            Dictionary<string, string> at, Dictionary<string, ActuatorGraph> graphs, List<Row> rows)
        {
            if (!owned.TryGetValue(state.StateID, out var moves)) return;

            foreach (var group in moves.GroupBy(m => m.Actuator.ComponentID, StringComparer.OrdinalIgnoreCase))
            {
                var target = group.First().Actuator;
                var g = Graph(target, graphs, ctx);
                var pending = group.ToList();

                foreach (var fork in pending.GroupBy(m => m.OriginStateId, StringComparer.OrdinalIgnoreCase).Where(f => f.Count() > 1))
                    throw Fail(process, state,
                        $"owns {fork.Count()} movements of '{target.Name}' that all leave '{g.NameOf(fork.Key)}' " +
                        $"(transitions {string.Join(", ", fork.Select(m => m.TransitionId))}), so which command " +
                        "this state issues is ambiguous");

                if (pending.Count == 1) { DriveOwned(process, state, pending[0], target, g, ctx, pos, at, rows); continue; }

                // A leg that never becomes applicable belongs to the actuator's other pass through this state.
                for (bool moved = true; moved && pending.Count > 0; )
                {
                    string here = at.TryGetValue(target.ComponentID, out var cur) ? cur : g.StartId;
                    var leg = pending.FirstOrDefault(m => string.Equals(m.OriginStateId, here, StringComparison.OrdinalIgnoreCase));
                    moved = leg != null;
                    if (leg == null) break;
                    pending.Remove(leg);
                    DriveOwned(process, state, leg, target, g, ctx, pos, at, rows);
                }
            }
        }

        // A destination is normally a MOTION state, so the stop commanded is the first one reached through it.
        private static void DriveOwned(VueOneComponent process, VueOneState state, OwnedMove move,
            VueOneComponent target, ActuatorGraph g, Ctx ctx, Dictionary<string, int> pos,
            Dictionary<string, string> at, List<Row> rows)
        {
            int id = SlotOf(target, ctx, process, state);

            // The twin says WHEN a movement happens; where the CAT declares a sequence, the CAT says HOW.
            // A sequence that runs ONCE folds every movement the twin models into one handshake, so it is
            // emitted whole the first time and never again.
            var exec = ctx.ExecutionOf(target);
            if (exec is { Mode: ExecutionMode.RunOnce })
            {
                if (pos.ContainsKey(target.ComponentID)) return;
                EmitSequence(exec.Steps, target, id, state, rows);
                pos[target.ComponentID] = exec.FinalSettled;
                return;
            }

            var stopId = g.FirstStopVia(move.DestinationStateId)
                ?? throw Fail(process, state,
                    $"owns '{target.Name}' transition '{move.TransitionId}' toward '{g.NameOf(move.DestinationStateId)}', " +
                    "from which the actuator reaches no physical stop, so no command can be derived");

            // An ALTERNATING sequence is one step per movement, resuming from where the last one settled.
            if (exec is { Mode: ExecutionMode.Alternate })
            {
                var step = exec.StepFrom(pos.TryGetValue(target.ComponentID, out int last) ? last : null);
                EmitSequence(new[] { step }, target, id, state, rows);
                pos[target.ComponentID] = step.Settled;
                at[target.ComponentID] = stopId;
                return;
            }

            DriveTo(process, state, target, id, stopId, move.OriginStateId, at, g, rows, ctx);
        }

        // One command and the WAIT that proves it arrived, per declared step, in order.
        private static void EmitSequence(IReadOnlyList<ExecutionStep> steps, VueOneComponent target,
            int id, VueOneState state, List<Row> rows)
        {
            foreach (var s in steps)
            {
                rows.Add(Row.Cmd(TemplateMap.RingKey(target.Name), s.Command, state.StateID));
                rows.Add(Row.Wait(id, s.Settled, state.StateID));
            }
        }

        // A guard is a sum of products, and both operators keep their meaning. ALL operands are
        // separate requirements, emitted one after another. ANY operands are ALTERNATIVES: the step
        // releases when the FIRST of them holds, which a row cannot say on its own, so the alternatives
        // are laid down as one WAIT GROUP the engine evaluates as a disjunction (RecipeStep.AltCount /
        // TermCount). Where every alternative reduces to the same requirement the choice is vacuous and
        // one plain row stands for all of them, which is what a guard with no real choice produces.
        private static void EmitGuard(ConditionExpr? guard, VueOneComponent process, VueOneState state,
            VueOneTransition edge, int gateCount, Ctx ctx, ProcessHandoffPlan plan, Dictionary<string, int> pos,
            Dictionary<string, string> at, Dictionary<string, ActuatorGraph> graphs, List<Row> rows,
            RecipeArrays arrays, Dictionary<string, int> settledHere,
            Dictionary<string, List<OwnedMove>> owned, bool entryPhase, HashSet<string> armed)
        {
            switch (guard)
            {
                case null: return;
                case ConditionExpr.Ref r:
                    EmitCondition(process, state, edge, r.Condition, gateCount, ctx, plan, pos, at, graphs,
                        rows, arrays, settledHere, owned, entryPhase, armed);
                    return;
                case ConditionExpr.All all:
                    foreach (var op in all.Operands)
                        EmitGuard(op, process, state, edge, gateCount, ctx, plan, pos, at, graphs, rows,
                            arrays, settledHere, owned, entryPhase, armed);
                    return;
                case ConditionExpr.Any any:
                    EmitAlternatives(any, process, state, edge, gateCount, ctx, plan, pos, at, graphs, rows,
                        arrays, settledHere, owned, entryPhase, armed);
                    return;
                default:
                    throw Fail(process, state,
                        $"guard node {guard.GetType().Name} is one this compiler has no reading for");
            }
        }

        // Each alternative is compiled AS IF IT STOOD ALONE - its own dedup scopes - so one alternative
        // cannot silence a term another one needs. Only then are they compared: identical alternatives
        // are the same requirement written twice and collapse to one row.
        private static void EmitAlternatives(ConditionExpr.Any any, VueOneComponent process,
            VueOneState state, VueOneTransition edge, int gateCount, Ctx ctx, ProcessHandoffPlan plan,
            Dictionary<string, int> pos, Dictionary<string, string> at,
            Dictionary<string, ActuatorGraph> graphs, List<Row> rows, RecipeArrays arrays,
            Dictionary<string, int> settledHere, Dictionary<string, List<OwnedMove>> owned,
            bool entryPhase, HashSet<string> armed)
        {
            var alternatives = new List<List<Row>>();
            foreach (var op in any.Operands)
            {
                var sub = new List<Row>();
                EmitGuard(op, process, state, edge, gateCount, ctx, plan,
                    new Dictionary<string, int>(pos, StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(at, StringComparer.OrdinalIgnoreCase),
                    graphs, sub, arrays,
                    new Dictionary<string, int>(settledHere, StringComparer.OrdinalIgnoreCase),
                    owned, entryPhase, armed);
                alternatives.Add(sub);
            }

            // An alternative that adds no requirement is already met, so the disjunction is already met
            // and the step waits for none of it. Two things produce that, and the report says both rather
            // than claiming to know which: this recipe already proved the position, or the alternative
            // names something whose arrival this recipe cannot observe (a jaw or the task arm, whose
            // reported states are the CAT handshake). Either way the twin wrote a guard here.
            if (alternatives.Any(a => a.All(r => r.Step != StepType.Wait)))
            {
                ctx.Findings.Add(
                    $"'{process.Name}' state '{state.Name}': one of the {any.Operands.Count} alternative " +
                    "guards adds no requirement to this recipe - either already established here, or " +
                    "naming an arrival this recipe cannot observe - so the step does not wait on it.");
                // One alternative already holds, so the disjunction holds and none of its terms is a
                // requirement. Each leaf was compiled in the sub-pass; this is what became of it.
                foreach (var leaf in any.References())
                    ctx.Coverage.Record(Leaf(process, state, edge, leaf,
                        GuardLeafOutcome.AlreadyRequired,
                        "one alternative of this guard is already met, so the disjunction is met and " +
                        "none of its terms is a requirement"));
                return;
            }

            // A refresh is an ASK, not a requirement: it belongs before the group so every alternative
            // is tested against a level that was re-announced.
            foreach (var cmd in alternatives.SelectMany(a => a).Where(r => r.Step == StepType.Cmd))
                if (!rows.Any(r => r.Step == StepType.Cmd && string.Equals(r.Target, cmd.Target, StringComparison.Ordinal)))
                    rows.Add(cmd);

            var distinct = new List<List<Row>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var alt in alternatives)
            {
                var waits = alt.Where(r => r.Step == StepType.Wait).ToList();
                if (seen.Add(string.Join("|", waits.Select(w => w.WaitId + ":" + w.WaitState))))
                    distinct.Add(waits);
            }

            int head = rows.Count;
            foreach (var alt in distinct)
            {
                alt[0].Terms = alt.Count;
                rows.AddRange(alt);
            }
            // One alternative is a plain requirement and stays a plain row, so a guard with no real
            // choice in it produces exactly what it always did.
            if (distinct.Count > 1)
            {
                rows[head].Alt = distinct.Count;
                rows[head].GroupLen = rows.Count - head;
            }
            else rows[head].Terms = 0;
        }

        private static void EmitCondition(VueOneComponent process, VueOneState state, VueOneTransition edge,
            VueOneCondition cond, int gateCount, Ctx ctx, ProcessHandoffPlan plan, Dictionary<string, int> pos,
            Dictionary<string, string> at, Dictionary<string, ActuatorGraph> graphs, List<Row> rows,
            RecipeArrays arrays, Dictionary<string, int> settledHere,
            Dictionary<string, List<OwnedMove>> owned, bool entryPhase, HashSet<string> armed)
        {
            var target = Resolve(cond, ctx.Twin);
            void Covered(GuardLeafOutcome outcome, string why) =>
                ctx.Coverage.Record(Leaf(process, state, edge, cond, outcome, why));

            if (IsProcess(target)) { EmitHandoff(process, state, edge, cond, gateCount, target, ctx, plan, rows, arrays, armed); return; }

            int id = SlotOf(target, ctx, process, state);
            int reached = StateNumberOf(cond, target, process, state, ctx);

            if (IsSensor(target))
            {
                int wait = ctx.SensorPresent.TryGetValue(target.Name.Trim(), out int p) ? p : reached;
                if (pos.TryGetValue(target.ComponentID, out int already) && already == wait)
                    Covered(GuardLeafOutcome.AlreadyRequired,
                        $"this recipe already waits for '{target.Name}' at {wait}");
                else
                {
                    // Ask before waiting: a level already true before this PLC started is announced once and
                    // never again. The name is VERBATIM, not the ring key, as BREQ's claim test is
                    // case-sensitive. See Docs/PATCH_RATIONALES P-3.
                    rows.Add(Row.Cmd(target.Name.Trim(), 0, state.StateID));
                    rows.Add(Row.Wait(id, wait, state.StateID));
                    pos[target.ComponentID] = wait;
                    Covered(GuardLeafOutcome.Waited, $"waits for '{target.Name}' at {wait}");
                }
                return;
            }

            // Everything below is an OBSERVATION: a condition can only add "has REACHED a stop".
            var g = Graph(target, graphs, ctx);
            var named = PeerState(target, cond, ctx)
                ?? throw Fail(process, state, $"condition '{cond.Name}' does not name a state of '{target.Name}'");

            // A CAT that runs a declared sequence reports its OWN handshake, not the twin's stop numbering,
            // so its arrival is proved by the owning command's WAIT and an observation adds nothing.
            if (ctx.ExecutionOf(target) != null)
            {
                if (Owns(owned, state, target))
                {
                    Covered(GuardLeafOutcome.ProvedByOwnedCommand,
                        $"this state commands '{target.Name}'; the command's arrival WAIT is the requirement");
                    return;
                }
                arrays.Warnings.Add(
                    $"'{process.Name}' state '{state.Name}': condition '{cond.Name}' observes " +
                    $"'{target.Name}', whose reported states are the CAT's handshake rather than the twin's " +
                    "stop numbering, and this state does not own its movement; the step is sequenced by the " +
                    "owning process instead.");
                // Its CAT reports a handshake, not the twin's stops, so nothing can wait for the stop this
                // names. That is only harmless where THIS recipe already drove it: an earlier command's
                // arrival WAIT is the requirement. Where the recipe never drives it, nothing here can
                // observe the arrival at all, so the leaf would simply vanish - and generation stops.
                if (!OwnsAny(owned, target) || !at.ContainsKey(target.ComponentID))
                    throw Fail(process, state,
                        $"condition '{cond.Name}' observes '{target.Name}', whose CAT reports its own " +
                        "handshake rather than the twin's stop numbering, and this recipe never commands " +
                        "it - so the arrival it names can be observed by nothing here. Command it in this " +
                        "process, or state the requirement on something this recipe can see");
                Covered(GuardLeafOutcome.AlreadyRequired,
                    $"'{target.Name}' reports its CAT's handshake; an earlier command in this recipe " +
                    "already proved the position");
                return;
            }

            // The CAT reports stops, so a motion state is already sequenced by the driving command's WAIT.
            if (!g.IsStop(named.StateID))
            {
                Covered(GuardLeafOutcome.ProvedByOwnedCommand,
                    $"'{named.Name}' is a motion state, not a stop; the command driving '{target.Name}' " +
                    "through it is what sequences the step");
                return;
            }

            int settledAt = Settled(target, g.StopNumber(named.StateID), process, state, ctx);
            if (Restates(settledHere, target, settledAt))
            {
                Covered(GuardLeafOutcome.AlreadyRequired,
                    $"a sibling term of the same guard already requires '{target.Name}' at {settledAt}");
                return;
            }

            // An entry gate on an actuator this process never moves must consume a FRESH arrival. A process
            // that owns a movement of it is not armed, which would deadlock. See Docs/PATCH_RATIONALES P-2.
            if (entryPhase && !OwnsAny(owned, target))
            {
                var prev = g.PrevStopInto(named.StateID);
                if (prev != null)
                {
                    int armFrom = Settled(target, g.StopNumber(prev), process, state, ctx);
                    if (armFrom != settledAt) rows.Add(Row.Wait(id, armFrom, state.StateID));
                }
            }
            // Already established by a command in this recipe: that command's arrival WAIT proved it.
            if (at.TryGetValue(target.ComponentID, out var cur) && g.IsStop(cur) &&
                Settled(target, g.StopNumber(cur), process, state, ctx) == settledAt)
            {
                Covered(GuardLeafOutcome.AlreadyRequired,
                    $"an earlier command in this recipe already left '{target.Name}' at {settledAt}");
                return;
            }

            rows.Add(Row.Wait(id, settledAt, state.StateID));
            at[target.ComponentID] = named.StateID;
            Covered(GuardLeafOutcome.Waited, $"waits for '{target.Name}' at {settledAt}");
        }

        // An actuator advanced by one process and driven home by another is not stranded, it is being held.
        internal static IReadOnlyCollection<string> ProcessesCommandingHome(
            string ringName, CodeGen.Domain.Twin.TwinModel twin, Ctx ctx)
        {
            var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var actuator = twin.Components.FirstOrDefault(c => c.IsActuator &&
                string.Equals(TemplateMap.RingKey(c.Name), ringName?.Trim(), StringComparison.OrdinalIgnoreCase))?.Source;
            if (actuator == null) return res;

            var g = new ActuatorGraph(actuator, ctx);
            foreach (var s in actuator.States)
                foreach (var t in s.Transitions)
                {
                    var stop = g.FirstStopVia(t.DestinationStateID);
                    if (stop == null || !g.IsStop(stop)) continue;
                    // Which stop the number names is the CAT's declaration, asked in one place.
                    if (!string.Equals(ctx.ProtocolOf(actuator).StopFor(g.StopNumber(stop)),
                            CatProtocol.Home, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var cond in t.Guard?.References()
                        ?? (IReadOnlyList<VueOneCondition>)Array.Empty<VueOneCondition>())
                    {
                        var owner = TryResolve(cond, twin);
                        if (owner != null && IsProcess(owner)) res.Add(owner.Name.Trim());
                    }
                }
            return res;
        }

        // If not, the actuator's position at this process's entry is whatever another process left behind.
        private static bool OwnsAny(Dictionary<string, List<OwnedMove>> owned, VueOneComponent target) =>
            owned.Values.Any(l => l.Any(m =>
                string.Equals(m.Actuator.ComponentID, target.ComponentID, StringComparison.OrdinalIgnoreCase)));

        private static bool Owns(Dictionary<string, List<OwnedMove>> owned, VueOneState state, VueOneComponent target) =>
            owned.TryGetValue(state.StateID, out var m) &&
            m.Any(x => string.Equals(x.Actuator.ComponentID, target.ComponentID, StringComparison.OrdinalIgnoreCase));

        private static ActuatorGraph Graph(VueOneComponent target, Dictionary<string, ActuatorGraph> graphs, Ctx ctx) =>
            graphs.TryGetValue(target.ComponentID, out var g) ? g : graphs[target.ComponentID] = new ActuatorGraph(target, ctx);

        // The trajectory is the actuator's OWN shortest path from where this recipe last left it, commanding
        // every physical stop crossed, so an occupied stop costs nothing and a return branch follows its own.
        private static void DriveTo(VueOneComponent process, VueOneState state, VueOneComponent target, int id,
            string stopId, string declaredOrigin, Dictionary<string, string> at, ActuatorGraph g,
            List<Row> rows, Ctx ctx)
        {
            // Where this recipe has not moved the actuator, the owned transition declares the state it leaves
            // from. Taking that over Initial_State stops a process re-driving a cycle it did not perform.
            bool assumed = !at.TryGetValue(target.ComponentID, out var f);
            string from = !assumed ? f!
                : !string.IsNullOrEmpty(declaredOrigin) && g.Knows(declaredOrigin) ? declaredOrigin
                : g.StartId;
            var path = g.PathTo(from, stopId)
                ?? throw Fail(process, state, $"'{target.Name}' cannot reach '{g.NameOf(stopId)}' from '{g.NameOf(from)}' along its own transitions");

            int before = rows.Count;
            int last = g.IsStop(from) ? Settled(target, g.StopNumber(from), process, state, ctx) : -1;
            foreach (var sid in path)
            {
                if (!g.IsStop(sid)) continue;
                var (cmd, settled) = Command(target, g.StopNumber(sid), process, state, ctx);
                if (settled == last) continue;                     // same physical stop: already there, no new command
                rows.Add(Row.Cmd(TemplateMap.RingKey(target.Name), cmd, state.StateID));
                rows.Add(Row.Wait(id, settled, state.StateID));
                last = settled;
            }
            // The walk commanded nothing because the destination is where the actuator was ASSUMED to be.
            if (assumed && rows.Count == before && g.IsStop(stopId))
                rows.Add(Row.Wait(id, Settled(target, g.StopNumber(stopId), process, state, ctx), state.StateID));
            at[target.ComponentID] = stopId;
        }

        // The command driving this actuator to a stop and the value it publishes on arrival, both from the
        // CAT's own protocol. A stop the CAT has no command for fails generation rather than being guessed.
        private static (int cmd, int settled) Command(VueOneComponent target, int stop,
            VueOneComponent process, VueOneState state, Ctx ctx)
        {
            var p = ctx.ProtocolOf(target);
            // Which stop a twin State_Number names is the CAT's declaration, so one number naming a
            // place the CAT passes through - a returned-complete rest it settles away from - reads the
            // same here as it does in an interlock and in a timing leg.
            var named = p.StopFor(stop);
            if (named != null && p.Has(named)) return (p.CommandFor(named), p.SettledFor(named));
            throw Fail(process, state,
                $"'{target.Name}' stop State_Number {stop} is not a position its CAT " +
                $"({ctx.CatTypeOf(target)}) can be commanded to " +
                $"(it settles at {string.Join("/", p.Settled.Values.OrderBy(v => v))})");
        }

        private static int Settled(VueOneComponent target, int stop, VueOneComponent process,
            VueOneState state, Ctx ctx) => Command(target, stop, process, state, ctx).settled;

        // TRUE when a sibling condition already required this component at this stop; records it otherwise.
        private static bool Restates(Dictionary<string, int> settledHere, VueOneComponent target, int settled)
        {
            if (settledHere.TryGetValue(target.ComponentID, out int s) && s == settled) return true;
            settledHere[target.ComponentID] = settled;
            return false;
        }

        // An actuator's stop map + transition graph. A five-state actuator numbers its stops canonically
        // (0 Home / 2 Work / 4 returned-complete), so its State_Number IS the stop and <Position> must be
        // ignored: VueOne leaves it at 0 on several of them, which would alias Work onto Home and delete the
        // stroke. Only the centre-home swivel re-visits ONE place under two numberings, so only there does
        // geometry fold, taking the number from the first-declared state at that position.
        private sealed class ActuatorGraph
        {
            private readonly Dictionary<string, VueOneState> _byId = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<string>> _next = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _stop = new(StringComparer.OrdinalIgnoreCase);
            public readonly string StartId;

            public ActuatorGraph(VueOneComponent c, Ctx ctx)
            {
                foreach (var s in c.States) _byId[s.StateID] = s;
                foreach (var s in c.States)
                    _next[s.StateID] = s.Transitions.OrderBy(t => t.Priority)
                        .Select(t => t.DestinationStateID).Where(d => !string.IsNullOrEmpty(d) && _byId.ContainsKey(d)).ToList();

                // Which number names a stop - and whether two states at one place are one stop - is
                // the CAT's declaration, answered by the one owner of that question.
                foreach (var s in c.States.Where(s => s.StaticState))
                    _stop[s.StateID] = Interlocks.ActuatorStateEncoding.CanonicalNumber(c, s, ctx.CatType);

                StartId = (c.States.FirstOrDefault(s => s.InitialState) ?? c.States.FirstOrDefault())?.StateID ?? string.Empty;
            }

            public bool Knows(string id) => _byId.ContainsKey(id);
            public bool IsStop(string id) => _stop.ContainsKey(id);
            public int StopNumber(string id) => _stop[id];
            public string NameOf(string id) => _byId.TryGetValue(id, out var s) ? s.Name : id;

            // The stop the actuator arrives FROM: nearest stop walking the transitions backwards.
            public string? PrevStopInto(string stop)
            {
                if (string.IsNullOrEmpty(stop) || !_byId.ContainsKey(stop)) return null;
                var back = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _next)
                    foreach (var n in kv.Value)
                        (back.TryGetValue(n, out var l) ? l : back[n] = new List<string>()).Add(kv.Key);
                var q = new Queue<string>(); q.Enqueue(stop);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stop };
                while (q.Count > 0)
                    foreach (var p in back.TryGetValue(q.Dequeue(), out var l2) ? l2 : Enumerable.Empty<string>())
                    {
                        if (!seen.Add(p)) continue;
                        if (IsStop(p)) return p;
                        q.Enqueue(p);
                    }
                return null;
            }

            public string? FirstStopVia(string destination)
            {
                if (string.IsNullOrEmpty(destination) || !_byId.ContainsKey(destination)) return null;
                if (IsStop(destination)) return destination;
                var q = new Queue<string>(); q.Enqueue(destination);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { destination };
                while (q.Count > 0)
                    foreach (var n in _next.TryGetValue(q.Dequeue(), out var l) ? l : Enumerable.Empty<string>())
                    {
                        if (!seen.Add(n)) continue;
                        if (IsStop(n)) return n;
                        q.Enqueue(n);
                    }
                return null;
            }

            // Shortest route from -> to, excluding `from`; null when unreachable.
            public List<string>? PathTo(string from, string to)
            {
                if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return new List<string>();
                var prev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var q = new Queue<string>(); q.Enqueue(from);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
                while (q.Count > 0)
                {
                    var cur = q.Dequeue();
                    foreach (var n in _next.TryGetValue(cur, out var l) ? l : Enumerable.Empty<string>())
                    {
                        if (!seen.Add(n)) continue;
                        prev[n] = cur;
                        if (string.Equals(n, to, StringComparison.OrdinalIgnoreCase))
                        {
                            var path = new List<string>();
                            for (var x = n; x != null && prev.ContainsKey(x); x = prev[x]) path.Add(x);
                            path.Reverse();
                            return path;
                        }
                        q.Enqueue(n);
                    }
                }
                return null;
            }
        }

        // A cross-process condition becomes a WAIT on the peer's OWN announced State_Number wherever the plan
        // says it reaches this state_table. It is NEVER silently replaced by an unrelated level.
        private static void EmitHandoff(VueOneComponent process, VueOneState state, VueOneTransition edge,
            VueOneCondition cond, int gateCount, VueOneComponent peer, Ctx ctx, ProcessHandoffPlan plan, List<Row> rows,
            RecipeArrays arrays, HashSet<string> armed)
        {
            void Covered(GuardLeafOutcome outcome, string why) =>
                ctx.Coverage.Record(Leaf(process, state, edge, cond, outcome, why));

            if (SameName(peer, process))
            {
                Covered(GuardLeafOutcome.SelfReference, "the recipe is already in the process it names");
                return;                                                            // self: already here
            }
            var refState = PeerState(peer, cond, ctx) ?? throw Fail(process, state, $"condition '{cond.Name}' does not name a state of '{peer.Name}'");
            if (refState.InitialState)
            {
                // A producer's ENTRY phase. What that MEANS is a deployment decision - boot readiness, or
                // an ordinary phase to be waited for - and the two drive the plant differently, so it is
                // declared rather than assumed. An undeclared deployment is refused here.
                if (ctx.Handoff.PeerEntryPhase == Configuration.PeerEntryPhaseMeaning.Undeclared)
                    throw Fail(process, state,
                        $"condition '{cond.Name}' names the entry phase of '{peer.Name}', and this " +
                        "deployment does not declare what a producer's entry phase means " +
                        "(smc-rig.yml handoff.peerEntryPhase: readinessAssertion | runtimePhase). " +
                        "Reading it as boot readiness and reading it as a runtime phase drive the plant " +
                        "differently, so the compiler will not choose");
                if (ctx.Handoff.PeerEntryPhase == Configuration.PeerEntryPhaseMeaning.ReadinessAssertion)
                {
                    arrays.Warnings.Add(
                        $"'{process.Name}' state '{state.Name}': condition '{cond.Name}' names the peer's " +
                        "Initialisation state, treated as a readiness assertion rather than a runtime phase.");
                    Covered(GuardLeafOutcome.SatisfiedByDeclaration,
                        "smc-rig.yml declares a producer's entry phase to be a boot-readiness assertion, " +
                        "which the plant answers by having started");
                    return;
                }
                // runtimePhase: fall through and compile it like any other phase, which then needs a
                // transport that carries it - and is refused below where none exists.
            }
            if (!ctx.ProcessIdByName.TryGetValue(peer.Name.Trim(), out int peerId))
                throw Fail(process, state, $"peer process '{peer.Name}' has no deployment id");

            if (plan.TransportFor(peer.Name, process.Name) != HandoffTransport.None)
            {
                // Fresh phase transition: the producer must be seen BEGINNING a cycle before its completion
                // counts. Only the FIRST wait on a producer arms, though -- a producer announces its entry
                // once per cycle, so re-arming would park a consumer forever on a second announcement that
                // never comes.
                var entry = ctx.GraphOf(peer).Entry;
                int done = refState.StateNumber;
                bool armHere = armed.Add(peer.Name?.Trim() ?? string.Empty);
                if (armHere)
                {
                    if (entry != null && entry.StateNumber != done)
                        rows.Add(Row.Wait(peerId, entry.StateNumber, state.StateID));
                    else if (done == 0)
                        throw Fail(process, state,
                            $"condition '{cond.Name}' completes on State_Number 0, which is also the initial value of " +
                            $"a state_table slot, and '{peer.Name}' declares no earlier phase to arm against, so the " +
                            "completion could never be told apart from a slot that was merely never written");
                }
                rows.Add(Row.Wait(peerId, done, state.StateID));
                Covered(GuardLeafOutcome.Waited,
                    $"waits for '{peer.Name}' to announce phase {done} on a transport that reaches here");
                return;
            }

            // The announcement does not reach this controller. A CARRIER may stand in for it - but only
            // where the deployment declares that the carrier's proposition and the phase's coincide on
            // this plant. A carrier reports that MATERIAL ARRIVED; a phase reports that a PRODUCER GOT
            // SOMEWHERE, and nothing in the model makes those the same statement.
            var substitution = ctx.Handoff.CarrierFor(peer.Name, refState.Name);
            if (substitution != null &&
                ctx.CarrierSlots.TryGetValue(substitution.Carrier, out int carrierId))
            {
                arrays.Warnings.Add(
                    $"'{process.Name}' state '{state.Name}': '{cond.Name}' has no process-state route to this " +
                    $"controller; carried by '{substitution.Carrier}' as a fresh deasserted->asserted " +
                    $"handoff, declared because {substitution.Because}.");
                // A fresh edge, so a level already TRUE at boot cannot manufacture a cycle.
                rows.Add(Row.Wait(carrierId, substitution.Deasserted, state.StateID));
                rows.Add(Row.Wait(carrierId, substitution.Asserted, state.StateID));
                Covered(GuardLeafOutcome.Waited,
                    $"waits for the declared carrier '{substitution.Carrier}' to go " +
                    $"{substitution.Deasserted} -> {substitution.Asserted}");
                return;
            }

            throw Fail(process, state,
                $"condition '{cond.Name}' names state '{refState.Name}' of '{peer.Name}', which is not " +
                "transported to this controller: no shared report ring, no process-phase cross-reference, " +
                "and smc-rig.yml handoff.carriers declares nothing that stands for it. A term with no " +
                "route is a requirement the plant would never evaluate" +
                (gateCount > 1
                    ? $", and the other {gateCount - 1} condition(s) of this transition do not make it " +
                      "one - they are separate requirements, not a substitute for this one"
                    : string.Empty));
        }

        // Cross-process conditions with each transport resolved. No cache, so nothing survives a generation.
        internal static ProcessHandoffPlan HandoffPlan(Ctx ctx) =>
            ProcessHandoffPlan.Derive(ctx.Twin, ctx.ProcessIdByName, ctx.Graphs,
                (producer, consumer) => SameRing(producer, consumer, ctx));

        private static void Serialize(VueOneComponent process, CodeGen.Domain.Twin.ProcessGraph graph,
            List<Row> rows, Dictionary<string, int> firstRow, RecipeArrays arrays)
        {
            int end = rows.Count;
            var states = graph.Ordered;
            var byId = states.ToDictionary(s => s.StateID, s => s, StringComparer.OrdinalIgnoreCase);

            // Telemetry: a 1-based ordinal per declared state, from the twin's own declaration order so it is
            // stable across regenerations. 0 stays free for "no owning state", and two states sharing a name
            // still get distinct ordinals.
            var ordinalOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < process.States.Count; i++)
            {
                var s = process.States[i];
                if (string.IsNullOrEmpty(s.StateID) || ordinalOf.ContainsKey(s.StateID)) continue;
                ordinalOf[s.StateID] = i + 1;
                arrays.ProcessPhaseNames[i + 1] = s.Name ?? string.Empty;
            }

            // The row a state hands control to: its successor's first row, skipping states that laid
            // none down. The successor is the graph's answer, so this cannot spell it differently.
            int DestRow(string fromStateId)
            {
                var next = graph.Successor(byId[fromStateId]);
                int guard = 0;
                while (next != null && guard++ <= states.Count)
                {
                    if (firstRow.TryGetValue(next.StateID, out int r)) return r;   // a back-edge is a loop
                    next = graph.Successor(next);
                }
                return end;   // terminal -> END
            }

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                arrays.StepType.Add(r.Step);
                arrays.CmdTargetName.Add(r.Target ?? string.Empty);
                arrays.CmdStateArr.Add(r.CmdState);
                arrays.Wait1Id.Add(r.WaitId);
                arrays.Wait1State.Add(r.WaitState);
                arrays.AltCount.Add(r.Alt);
                arrays.TermCount.Add(r.Terms);
                // A wait GROUP is one requirement, so the row that heads it steps past the whole group;
                // the rows inside it are only ever read by the head's own evaluation.
                int after = r.GroupLen > 0 ? i + r.GroupLen : i + 1;
                bool last = after >= rows.Count || rows[after].StateId != r.StateId;
                arrays.NextStep.Add(last ? DestRow(r.StateId) : after);
                // Telemetry only: the row's owning VueOne state, resolved from the StateId it was built under.
                arrays.ProcessStateByRow.Add(
                    r.StateId != null && ordinalOf.TryGetValue(r.StateId, out var ord) ? ord : 0);
            }

            arrays.StepType.Add(StepType.End);
            arrays.CmdTargetName.Add(string.Empty);
            arrays.CmdStateArr.Add(0);
            arrays.Wait1Id.Add(0);
            arrays.Wait1State.Add(0);
            arrays.NextStep.Add(graph.IsEntry(graph.TerminalDestination) ? 0 : end);
            arrays.AltCount.Add(0);
            arrays.TermCount.Add(0);
            // END carries the last row's phase so the final publish does not report a phantom state 0.
            arrays.ProcessStateByRow.Add(
                arrays.ProcessStateByRow.Count > 0 ? arrays.ProcessStateByRow[^1] : 0);
        }


        private static VueOneState? PeerState(VueOneComponent peer, VueOneCondition cond, Ctx ctx) =>
            ctx.Twin.StateOf(peer, cond);

        private static int SlotOf(VueOneComponent target, Ctx ctx, VueOneComponent process, VueOneState state)
        {
            // A deployment-allocated cross-controller slot wins over the local scoped map.
            if (ctx.IdsByName.TryGetValue(target.Name.Trim(), out int nid))
                return nid;
            if (!string.IsNullOrWhiteSpace(target.ComponentID) && ctx.Ids.TryGetValue(target.ComponentID.Trim(), out int id))
                return id;
            throw Fail(process, state, $"'{target.Name}' has no state_table slot on this ring");
        }

        private static int StateNumberOf(VueOneCondition cond, VueOneComponent target, VueOneComponent process, VueOneState? state, Ctx ctx)
        {
            var st = PeerState(target, cond, ctx);
            if (st == null)
                throw Fail(process, state, $"condition '{cond.Name}' does not name a state of '{target.Name}'");
            return st.StateNumber;
        }

        private static VueOneComponent Resolve(VueOneCondition cond, CodeGen.Domain.Twin.TwinModel twin) =>
            TryResolve(cond, twin) ?? throw new InvalidOperationException(
                $"[Compile] condition '{cond.Name}' (ComponentID '{cond.ComponentID}') resolves to no component.");

        private static VueOneComponent? TryResolve(VueOneCondition cond, CodeGen.Domain.Twin.TwinModel twin) =>
            twin.ComponentOf(cond);

        private static bool IsProcess(VueOneComponent? c) => ComponentType.IsProcess(c);
        private static bool IsSensor(VueOneComponent c) => ComponentType.IsSensor(c);
        private static bool SameName(VueOneComponent a, VueOneComponent b) => string.Equals(a.Name?.Trim(), b.Name?.Trim(), StringComparison.OrdinalIgnoreCase);
        private static bool SameRing(VueOneComponent a, VueOneComponent b, Ctx ctx) =>
            ctx.Rings.SameDomain(a.Name, b.Name);

        private static InvalidOperationException Fail(VueOneComponent process, VueOneState? state, string why) =>
            new($"[Compile] '{process.Name}'{(state == null ? "" : $" state '{state.Name}'")}: {why}.");

        private sealed class Row
        {
            public int Step; public string? Target; public int CmdState; public int WaitId; public int WaitState; public string StateId = "";
            // A WAIT row that HEADS a group carries how many alternatives start here and how many
            // rows the group spans; a row that heads an alternative carries how many terms hold
            // together. Zero everywhere is a plain single-slot wait.
            public int Alt; public int Terms; public int GroupLen;
            public static Row Cmd(string t, int s, string id) => new() { Step = StepType.Cmd, Target = t, CmdState = s, StateId = id };
            public static Row Wait(int i, int s, string id) => new() { Step = StepType.Wait, WaitId = i, WaitState = s, StateId = id };
        }
    }
}
