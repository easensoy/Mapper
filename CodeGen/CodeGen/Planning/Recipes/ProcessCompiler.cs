using System;
using System.Collections.Generic;
using System.Linq;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;
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
            // The one sensor crossing from the Feed controller to the assembly controller. It is a material
            // LEVEL, not a process state, so it stands in only as a fresh edge. -1 = no bridge available.
            public int MaterialBridgeId = -1;
            public int MaterialBridgeAsserted = 1;
            public int MaterialBridgeDeasserted;

            // What the backend could not represent exactly as the twin states it. Diagnostic only:
            // nothing here reaches an artefact, so a finding cannot move a byte.
            public readonly List<string> Findings = new();
        }

        public static RecipeArrays Compile(VueOneComponent process, Ctx ctx, ProcessHandoffPlan plan)
        {
            var arrays = new RecipeArrays();
            foreach (var kv in ctx.Ids) arrays.ComponentIds[kv.Key] = kv.Value;

            var states = OrderStatesByTransitionChain(process.States);
            foreach (var line in BuildTransitionTable(process.States, states)) arrays.TransitionTable.Add(line);
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
                    EmitGuard(t.Guard, process, state, leaves, ctx, plan, pos, at, graphs, rows,
                        arrays, settledHere, owned, entryPhase, armed);
                }
                if (entryPhase) { Work(); Announcement(); }
                if (rows.Count > before) firstRow[state.StateID] = before;
            }

            Serialize(process, states, rows, firstRow, arrays);
            arrays.OrderingSummary = $"'{process.Name}' compiled from Control.xml: {arrays.StepType.Count} rows.";
            return arrays;
        }

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
            if (exec is { RunsOnce: true })
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
            if (exec is { Alternates: true })
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
            int gateCount, Ctx ctx, ProcessHandoffPlan plan, Dictionary<string, int> pos,
            Dictionary<string, string> at, Dictionary<string, ActuatorGraph> graphs, List<Row> rows,
            RecipeArrays arrays, Dictionary<string, int> settledHere,
            Dictionary<string, List<OwnedMove>> owned, bool entryPhase, HashSet<string> armed)
        {
            switch (guard)
            {
                case null: return;
                case ConditionExpr.Ref r:
                    EmitCondition(process, state, r.Condition, gateCount, ctx, plan, pos, at, graphs,
                        rows, arrays, settledHere, owned, entryPhase, armed);
                    return;
                case ConditionExpr.All all:
                    foreach (var op in all.Operands)
                        EmitGuard(op, process, state, gateCount, ctx, plan, pos, at, graphs, rows,
                            arrays, settledHere, owned, entryPhase, armed);
                    return;
                case ConditionExpr.Any any:
                    EmitAlternatives(any, process, state, gateCount, ctx, plan, pos, at, graphs, rows,
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
            VueOneState state, int gateCount, Ctx ctx, ProcessHandoffPlan plan,
            Dictionary<string, int> pos, Dictionary<string, string> at,
            Dictionary<string, ActuatorGraph> graphs, List<Row> rows, RecipeArrays arrays,
            Dictionary<string, int> settledHere, Dictionary<string, List<OwnedMove>> owned,
            bool entryPhase, HashSet<string> armed)
        {
            var alternatives = new List<List<Row>>();
            foreach (var op in any.Operands)
            {
                var sub = new List<Row>();
                EmitGuard(op, process, state, gateCount, ctx, plan,
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

        private static void EmitCondition(VueOneComponent process, VueOneState state, VueOneCondition cond,
            int gateCount, Ctx ctx, ProcessHandoffPlan plan, Dictionary<string, int> pos,
            Dictionary<string, string> at, Dictionary<string, ActuatorGraph> graphs, List<Row> rows,
            RecipeArrays arrays, Dictionary<string, int> settledHere,
            Dictionary<string, List<OwnedMove>> owned, bool entryPhase, HashSet<string> armed)
        {
            var target = Resolve(cond, ctx.Twin);
            if (IsProcess(target)) { EmitHandoff(process, state, cond, gateCount, target, ctx, plan, rows, arrays, armed); return; }

            int id = SlotOf(target, ctx, process, state);
            int reached = StateNumberOf(cond, target, process, state, ctx);

            if (IsSensor(target))
            {
                int wait = ctx.SensorPresent.TryGetValue(target.Name.Trim(), out int p) ? p : reached;
                if (!pos.TryGetValue(target.ComponentID, out int c) || c != wait)
                {
                    // Ask before waiting: a level already true before this PLC started is announced once and
                    // never again. The name is VERBATIM, not the ring key, as BREQ's claim test is
                    // case-sensitive. See Docs/PATCH_RATIONALES P-3.
                    rows.Add(Row.Cmd(target.Name.Trim(), 0, state.StateID));
                    rows.Add(Row.Wait(id, wait, state.StateID));
                    pos[target.ComponentID] = wait;
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
                if (!Owns(owned, state, target))
                    arrays.Warnings.Add(
                        $"'{process.Name}' state '{state.Name}': condition '{cond.Name}' observes " +
                        $"'{target.Name}', whose reported states are the CAT's handshake rather than the twin's " +
                        "stop numbering, and this state does not own its movement; the step is sequenced by the " +
                        "owning process instead.");
                return;
            }

            // The CAT reports stops, so a motion state is already sequenced by the driving command's WAIT.
            if (!g.IsStop(named.StateID)) return;

            int settledAt = Settled(target, g.StopNumber(named.StateID), process, state, ctx);
            if (Restates(settledHere, target, settledAt)) return;

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
                Settled(target, g.StopNumber(cur), process, state, ctx) == settledAt) return;

            rows.Add(Row.Wait(id, settledAt, state.StateID));
            at[target.ComponentID] = named.StateID;
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
                    // Home is the stop the CAT settles to 0 -- the same protocol the commands themselves use.
                    int number = g.StopNumber(stop);
                    var proto = ctx.ProtocolOf(actuator);
                    int homeSettled = proto.SettledFor(CatProtocol.Home);
                    bool home = number == homeSettled ||
                                (!proto.Has(CatProtocol.Work1) && number == 4 && homeSettled == 0);
                    if (!home) continue;
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
            foreach (var name in p.Command.Keys)
                if (p.SettledFor(name) == stop) return (p.CommandFor(name), p.SettledFor(name));
            // A twin may number its returned-complete stop 4; both mean home, and the CAT publishes 0.
            if (p.Has(CatProtocol.Home) && stop == 4 && !p.Has(CatProtocol.Work1))
                return (p.CommandFor(CatProtocol.Home), p.SettledFor(CatProtocol.Home));
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

                var byPosition = new Dictionary<double, int>();
                // Whether a stop is a place or a number is the CAT's protocol, asked in one place.
                bool foldByGeometry = !ComponentType.IsProcess(c) && !ComponentType.IsSensor(c) &&
                    ctx.ProtocolOrNull(c) is { StopsAreGeometric: true };
                if (foldByGeometry)
                    foreach (var s in c.States.Where(s => s.StaticState))
                        if (!byPosition.ContainsKey(s.Position)) byPosition[s.Position] = s.StateNumber;
                foreach (var s in c.States.Where(s => s.StaticState))
                    _stop[s.StateID] = foldByGeometry ? byPosition[s.Position] : s.StateNumber;

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
        private static void EmitHandoff(VueOneComponent process, VueOneState state, VueOneCondition cond,
            int gateCount, VueOneComponent peer, Ctx ctx, ProcessHandoffPlan plan, List<Row> rows,
            RecipeArrays arrays, HashSet<string> armed)
        {
            if (SameName(peer, process)) return;                                   // self: the recipe is already here
            var refState = PeerState(peer, cond, ctx) ?? throw Fail(process, state, $"condition '{cond.Name}' does not name a state of '{peer.Name}'");
            if (IsInitialisationState(refState))
            {
                // A peer's Initialisation state asserts boot readiness, not the completion of a work cycle.
                arrays.Warnings.Add(
                    $"'{process.Name}' state '{state.Name}': condition '{cond.Name}' names the peer's " +
                    "Initialisation state, treated as a readiness assertion rather than a runtime phase.");
                return;
            }
            if (!ctx.ProcessIdByName.TryGetValue(peer.Name.Trim(), out int peerId))
                throw Fail(process, state, $"peer process '{peer.Name}' has no deployment id");

            if (plan.TransportFor(peer.Name, process.Name) != HandoffTransport.None)
            {
                // Fresh phase transition: the producer must be seen BEGINNING a cycle before its completion
                // counts. Only the FIRST wait on a producer arms, though -- a producer announces its entry
                // once per cycle, so re-arming would park a consumer forever on a second announcement that
                // never comes.
                var entry = EntryState(peer);
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
                return;
            }

            // The announcement does not reach this controller. The material bridge reports material ARRIVING,
            // so it may only stand for a handoff that is the SOLE gate of the transition, and only as a fresh
            // deasserted->asserted edge so a level already TRUE at boot cannot manufacture a cycle.
            if (ctx.MaterialBridgeId >= 0 && gateCount == 1)
            {
                arrays.Warnings.Add(
                    $"'{process.Name}' state '{state.Name}': '{cond.Name}' has no process-state route to this " +
                    $"controller; carried by the material sensor on the cross-controller segment as a fresh " +
                    $"deasserted->asserted handoff.");
                rows.Add(Row.Wait(ctx.MaterialBridgeId, ctx.MaterialBridgeDeasserted, state.StateID));
                rows.Add(Row.Wait(ctx.MaterialBridgeId, ctx.MaterialBridgeAsserted, state.StateID));
                return;
            }

            // ANDed with others, this process is already mid-cycle: the material arrived upstream, so demanding
            // a second arrival would stall it. The siblings sequence the step and this term is reported.
            if (gateCount > 1)
            {
                arrays.Warnings.Add(
                    $"'{process.Name}' state '{state.Name}': condition '{cond.Name}' has no route to this " +
                    $"controller; the step is sequenced by the other {gateCount - 1} condition(s) of the same " +
                    "transition. Add a transported phase to the model if this term must be evaluated.");
                return;
            }

            throw Fail(process, state,
                $"condition '{cond.Name}' names state '{refState.Name}' of '{peer.Name}', which is not " +
                "transported to this controller: no shared report ring, no process-phase cross-reference, " +
                "and no material bridge is configured to carry it");
        }

        // Cross-process conditions with each transport resolved. No cache, so nothing survives a generation.
        internal static ProcessHandoffPlan HandoffPlan(Ctx ctx) =>
            ProcessHandoffPlan.Derive(
                ctx.Twin, ctx.ProcessIdByName,
                (producer, consumer) => SameRing(producer, consumer, ctx),
                EntryState,
                (producer, cond) => PeerState(producer, cond, ctx),
                cond => TryResolve(cond, ctx.Twin),
                IsProcess,
                IsInitialisationState);

        private static void Serialize(VueOneComponent process, List<VueOneState> states, List<Row> rows,
            Dictionary<string, int> firstRow, RecipeArrays arrays)
        {
            int end = rows.Count;
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

            int DestRow(string fromStateId)
            {
                var dst = byId[fromStateId].Transitions.OrderBy(t => t.Priority).FirstOrDefault()?.DestinationStateID;
                int guard = 0;
                while (!string.IsNullOrEmpty(dst) && byId.ContainsKey(dst) && guard++ <= states.Count)
                {
                    if (firstRow.TryGetValue(dst, out int r)) return r;   // preserves loops (back-edge -> earlier row)
                    dst = byId[dst].Transitions.OrderBy(t => t.Priority).FirstOrDefault()?.DestinationStateID;
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
            arrays.NextStep.Add(TerminalLoopsHome(states) ? 0 : end);
            arrays.AltCount.Add(0);
            arrays.TermCount.Add(0);
            // END carries the last row's phase so the final publish does not report a phantom state 0.
            arrays.ProcessStateByRow.Add(
                arrays.ProcessStateByRow.Count > 0 ? arrays.ProcessStateByRow[^1] : 0);
        }

        private static bool TerminalLoopsHome(List<VueOneState> states)
        {
            var dst = states.Count == 0 ? null : states[^1].Transitions.OrderBy(t => t.Priority).FirstOrDefault()?.DestinationStateID;
            return states.Any(s => s.StateID == dst && IsInitialisationState(s));
        }

        private static VueOneState? EntryState(VueOneComponent process)
        {
            var ordered = OrderStatesByTransitionChain(process.States);
            return ordered.Count > 0 ? ordered[0] : null;
        }

        // The peer state a condition names (by StateID, else by the name after the slash).
        private static VueOneState? PeerState(VueOneComponent peer, VueOneCondition cond, Ctx ctx)
        {
            var c = ctx.Twin.ById(peer.ComponentID);
            return (c?.StateById(cond.ID) ?? c?.StateByName(After(cond.Name)))?.Source;
        }

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

        private static VueOneComponent? TryResolve(VueOneCondition cond, CodeGen.Domain.Twin.TwinModel twin)
        {
            if (!string.IsNullOrWhiteSpace(cond.ComponentID)) return twin.ById(cond.ComponentID)?.Source;
            var name = cond.Name?.IndexOf('/') is int i and >= 0 ? cond.Name.Substring(0, i).Trim() : cond.Name?.Trim();
            return twin.ByName(name)?.Source;
        }

        private static bool IsProcess(VueOneComponent? c) => ComponentType.IsProcess(c);
        private static bool IsSensor(VueOneComponent c) => ComponentType.IsSensor(c);
        private static bool SameName(VueOneComponent a, VueOneComponent b) => string.Equals(a.Name?.Trim(), b.Name?.Trim(), StringComparison.OrdinalIgnoreCase);
        private static bool SameRing(VueOneComponent a, VueOneComponent b, Ctx ctx) =>
            ctx.Rings.SameDomain(a.Name, b.Name);
        private static string After(string? s) => string.IsNullOrEmpty(s) ? string.Empty : (s.LastIndexOf('/') is int i and >= 0 ? s.Substring(i + 1) : s);

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
