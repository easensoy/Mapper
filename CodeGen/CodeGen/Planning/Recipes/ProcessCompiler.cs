using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Models;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;
using static CodeGen.Translation.Process.Recipes.RecipeCommandVocabulary;

namespace CodeGen.Translation.Process.Recipes
{
    // Deterministic Control.xml -> recipe rows.
    //
    // COMMAND OWNERSHIP. Who moves an actuator is declared by the ACTUATOR, not by the process: an actuator
    // transition whose Sequence_Condition names Process/State is the model stating that that process state issues
    // the command driving the actuator toward that transition's destination. A condition on a PROCESS transition
    // naming an actuator state is the opposite -- an observation the process waits on. Treating those observations
    // as instructions makes every process that merely watches an actuator also drive it, which is how a Transfer
    // owned by one process came to be commanded by three, and how a single "has it returned yet" condition
    // expanded into a whole advance-and-return stroke. So a process state emits exactly the movements it owns,
    // then waits on what its outgoing conditions observe.
    //
    // PHASE PROTOCOL. Every process announces two of its OWN model states on the ring: the entry state of its
    // transition chain (it has begun a cycle) and each state a peer waits on (it has reached that phase). A
    // consumer therefore waits for the producer's entry phase and THEN for the referenced phase, so it consumes a
    // fresh transition and can never be released by a value the producer left behind last cycle -- which also
    // means a completion number that happens to be 0 is never proof on its own. Where a peer's announcement has
    // no route to this controller the material sensor crossing the ring segment may stand in, but only for a
    // transition it solely gates and only as its own fresh deasserted->asserted edge; anything else fails
    // generation naming the process, condition and missing route.
    internal static class ProcessCompiler
    {
        public sealed class Ctx
        {
            public IReadOnlyDictionary<string, int> Ids = new Dictionary<string, int>();          // ComponentID -> state_table slot (scoped + deployment)
            public IReadOnlyDictionary<string, int> IdsByName = new Dictionary<string, int>();     // component name -> slot (deployment-allocated peers)
            public IReadOnlyList<VueOneComponent> All = Array.Empty<VueOneComponent>();
            public IReadOnlyDictionary<string, int> ProcessIdByName = new Dictionary<string, int>();
            public IReadOnlyDictionary<string, int> SensorPresent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int FeedProcessId = -1;          // the process that sits on the (separate) Feed ring
            public bool MergeFeedRing;              // no clamp: Feed shares the M580 ring -> every process announce is same-ring
            // The one sensor that DOES cross from the Feed controller to the assembly controller (it rides the
            // cross-ring segment). It is a material LEVEL, not a process state, so it can only stand in for a
            // Feed-side handoff as a freshly-armed edge -- see EmitHandoff. -1 = no bridge available.
            public int MaterialBridgeId = -1;
            public int MaterialBridgeAsserted = 1;
            public int MaterialBridgeDeasserted;
        }

        public static RecipeArrays Compile(VueOneComponent process, int processId, Ctx ctx)
        {
            var arrays = new RecipeArrays();
            foreach (var kv in ctx.Ids) arrays.ComponentRegistry[kv.Key] = kv.Value;

            var states = OrderStatesByTransitionChain(process.States);
            foreach (var line in BuildTransitionTable(process.States, states)) arrays.TransitionTable.Add(line);
            var announce = HandoffPlan(ctx).AnnouncementsOf(process.Name?.Trim() ?? string.Empty);
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
                // A phase is announced only once its OWN work is done. The announcement is a claim about the
                // plant -- 'bearing_pnp_home_pos' asserts the arm is home -- and a peer acts on it, so publishing
                // it before the movement that makes it true releases that peer into a machine that is still
                // moving. On the no-clamp model that is a collision: Disassembly announced its bearing-home phase
                // BEFORE homing the arm, Feed took that as "Disassembly finished", re-advanced the Transfer, and
                // Assembly began commanding the same swivel Disassembly was still carrying a bearing on. The
                // actuator interlocks cannot catch it -- they guard the Work1<->Work2 crossing, never the move out
                // of home. So: own work, THEN announce, THEN wait on the conditions.
                //
                // The ENTRY phase additionally announces after its entry CONDITIONS: a process has not begun a
                // cycle until it has been authorised to, otherwise every process publishes "I have started" the
                // moment the controllers are deployed, before any material arrived.
                bool entryPhase = idx == 0;
                void Announcement()
                {
                    if (!announce.TryGetValue(state.StateID, out var kind)) return;
                    // Ring: the producer reports its own State_Number under its own name. Cross-controller: the
                    // same number additionally leaves on the process-phase transport, whose command token is a
                    // backend protocol name, not a recipe decision.
                    if (kind.HasFlag(HandoffTransport.Ring))
                        rows.Add(Row.Cmd(process.Name?.Trim() ?? string.Empty, state.StateNumber, state.StateID));
                    if (kind.HasFlag(HandoffTransport.CrossController))
                        rows.Add(Row.Cmd(ProcessPhaseTransport.CommandToken, state.StateNumber, state.StateID));
                }
                // The phase's own work: the movements this state is declared to own. Each ends in the arrival WAIT
                // of the command it issued, so once Work() has run the phase really has been reached.
                void Work() => EmitOwnedMoves(process, state, owned, ctx, pos, at, graphs, rows, arrays);
                if (!entryPhase) { Work(); Announcement(); }
                foreach (var t in state.Transitions.OrderBy(t => t.Priority))
                {
                    // Conditions of one transition are ANDed, so two of them naming states of the SAME component
                    // that settle at the SAME stop (VueOne models a rest as both "ReturnedHome" and
                    // "ReturnedFinished") state one requirement, not two.
                    var settledHere = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var cond in t.Conditions)
                        EmitCondition(process, state, cond, t.Conditions.Count, ctx, pos, at, graphs, rows, arrays,
                            settledHere, owned, entryPhase, armed);
                }
                if (entryPhase) { Work(); Announcement(); }
                if (rows.Count > before) firstRow[state.StateID] = before;
            }

            Serialize(process, states, rows, firstRow, arrays);
            arrays.OrderingSummary = $"'{process.Name}' compiled from Control.xml: {arrays.StepType.Count} rows.";
            return arrays;
        }

        // One movement a process state is declared to own, read off the ACTUATOR's own transition.
        private sealed class OwnedMove
        {
            public VueOneComponent Actuator = null!;
            public string OriginStateId = string.Empty;
            public string DestinationStateId = string.Empty;
            public string TransitionId = string.Empty;
        }

        // Command ownership, taken from every actuator's own transitions: a Sequence_Condition naming
        // Process/State means that process state commands this actuator toward that transition's destination.
        // Indexed by the owning process state so compiling a state is a lookup, not a search.
        private static Dictionary<string, List<OwnedMove>> BuildOwnership(VueOneComponent process, Ctx ctx)
        {
            var res = new Dictionary<string, List<OwnedMove>>(StringComparer.OrdinalIgnoreCase);
            foreach (var actuator in ctx.All)
            {
                if (IsProcess(actuator) || IsSensor(actuator)) continue;
                foreach (var s in actuator.States)
                    foreach (var t in s.Transitions.OrderBy(t => t.Priority))
                        foreach (var cond in t.Conditions)
                        {
                            var owner = TryResolve(cond, ctx.All);
                            if (owner == null || !IsProcess(owner) || !SameName(owner, process)) continue;

                            var ownerState = PeerState(owner, cond)
                                ?? throw Fail(process, null,
                                    $"'{actuator.Name}' transition '{t.TransitionID}' is owned by condition " +
                                    $"'{cond.Name}', which names no state of '{owner.Name}'");
                            if (string.IsNullOrWhiteSpace(t.DestinationStateID))
                                throw Fail(process, ownerState,
                                    $"owns '{actuator.Name}' transition '{t.TransitionID}', which declares no " +
                                    "destination state, so the commanded movement cannot be derived");

                            var list = res.TryGetValue(ownerState.StateID, out var l)
                                ? l : (res[ownerState.StateID] = new List<OwnedMove>());
                            // The model routinely restates one movement (the same origin->destination declared
                            // twice); that is one command, not two.
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

        // The movements this state owns, in a deterministic order. Where a state owns SEVERAL movements of the
        // same actuator the model is describing a sequence, not a choice -- a Checker owned both
        // ReturnedHome->Lowering and Down->Rising by one process state performs the whole down-then-up stroke.
        // They are executed by following the chain from where the recipe last left the actuator: each leg whose
        // ORIGIN is the current position runs, and running it advances the position for the next. Two legs
        // leaving the SAME origin is a fork the model does not resolve, and generation fails rather than pick one.
        private static void EmitOwnedMoves(VueOneComponent process, VueOneState state,
            Dictionary<string, List<OwnedMove>> owned, Ctx ctx, Dictionary<string, int> pos,
            Dictionary<string, string> at, Dictionary<string, ActuatorGraph> graphs, List<Row> rows,
            RecipeArrays arrays)
        {
            if (!owned.TryGetValue(state.StateID, out var moves)) return;

            foreach (var group in moves.GroupBy(m => m.Actuator.ComponentID, StringComparer.OrdinalIgnoreCase))
            {
                var target = group.First().Actuator;
                var g = Graph(target, graphs);
                var pending = group.ToList();

                foreach (var fork in pending.GroupBy(m => m.OriginStateId, StringComparer.OrdinalIgnoreCase).Where(f => f.Count() > 1))
                    throw Fail(process, state,
                        $"owns {fork.Count()} movements of '{target.Name}' that all leave '{g.NameOf(fork.Key)}' " +
                        $"(transitions {string.Join(", ", fork.Select(m => m.TransitionId))}), so which command " +
                        "this state issues is ambiguous");

                if (pending.Count == 1) { DriveOwned(process, state, pending[0], target, g, ctx, pos, at, rows); continue; }

                // Follow the chain. A leg that never becomes applicable belongs to the actuator's other pass
                // through this state (the model reuses one process state across both directions of a cycle).
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

        // Issue the owned command. A destination is normally a MOTION state, so the stop actually commanded is the
        // first stop the actuator reaches through it -- which is also what the CAT can report back.
        private static void DriveOwned(VueOneComponent process, VueOneState state, OwnedMove move,
            VueOneComponent target, ActuatorGraph g, Ctx ctx, Dictionary<string, int> pos,
            Dictionary<string, string> at, List<Row> rows)
        {
            int id = SlotOf(target, ctx, process, state);

            // A task arm (VcID=UR3e -> Robot_Task_CAT) runs its whole modeled move on one StartTask: its core
            // reports 1 while running, 2 on completion, 0 when reset. Every movement the model gives it folds
            // into that single start(1)->done(2)->reset(2)->ready(0) handshake -- Robot never commands 3 or 5.
            if (TemplateMap.IsRobotTaskArm(target))
            {
                if (pos.ContainsKey(target.ComponentID)) return;
                rows.Add(Row.Cmd(TemplateMap.RingKey(target.Name), 1, state.StateID));
                rows.Add(Row.Wait(id, 2, state.StateID));
                rows.Add(Row.Cmd(TemplateMap.RingKey(target.Name), 2, state.StateID));
                rows.Add(Row.Wait(id, 0, state.StateID));
                pos[target.ComponentID] = 0;
                return;
            }

            var stopId = g.FirstStopVia(move.DestinationStateId)
                ?? throw Fail(process, state,
                    $"owns '{target.Name}' transition '{move.TransitionId}' toward '{g.NameOf(move.DestinationStateId)}', " +
                    "from which the actuator reaches no physical stop, so no command can be derived");

            // A jaw's direction is a physical wiring fact (REVERTED_FIXES R-12: the rig is wired energise-to-GRIP
            // while the twin models the jaw geometry energise-to-OPEN), so the twin's destination must never pick
            // the direction or the part is dropped. The model still says WHEN: each owned movement is one jaw
            // action, and a transfer must hold before it moves, so the first action on a jaw grips and they
            // alternate from there.
            if (IsGripper(target))
            {
                var (gc, gs) = !pos.TryGetValue(target.ComponentID, out int jaw) ? (1, 2) : jaw == 2 ? (3, 0) : (1, 2);
                rows.Add(Row.Cmd(TemplateMap.RingKey(target.Name), gc, state.StateID));
                rows.Add(Row.Wait(id, gs, state.StateID));
                pos[target.ComponentID] = gs;
                at[target.ComponentID] = stopId;
                return;
            }

            DriveTo(process, state, target, id, stopId, at, g, rows);
        }

        private static void EmitCondition(VueOneComponent process, VueOneState state, VueOneCondition cond,
            int gateCount, Ctx ctx, Dictionary<string, int> pos,
            Dictionary<string, string> at, Dictionary<string, ActuatorGraph> graphs, List<Row> rows,
            RecipeArrays arrays, Dictionary<string, int> settledHere,
            Dictionary<string, List<OwnedMove>> owned, bool entryPhase, HashSet<string> armed)
        {
            var target = Resolve(cond, ctx.All);
            if (IsProcess(target)) { EmitHandoff(process, state, cond, gateCount, target, ctx, rows, arrays, armed); return; }

            int id = SlotOf(target, ctx, process, state);
            int reached = StateNumberOf(cond, target, process, state);

            if (IsSensor(target))
            {
                int wait = ctx.SensorPresent.TryGetValue(target.Name.Trim(), out int p) ? p : reached;
                if (!pos.TryGetValue(target.ComponentID, out int c) || c != wait)
                {
                    // Ask before waiting. A sensor announces a level only on the edge that produced it, so a
                    // level that was already true before this PLC started is announced once (at INIT) and never
                    // again -- and that one frame is lost if the consuming ring is not up yet, leaving the WAIT
                    // dead until the sensor is physically toggled. Addressing the sensor drives its CAT through
                    // sample-then-report, so the WAIT below always evaluates a freshly read input. The state
                    // value carries no meaning here: nothing on a sensor consumes state_cmd, the frame is only
                    // a request to report. Bounded by construction -- one request, one report.
                    //
                    // NOT RingKey here. BREQ's test is `component_state_in.dest_name = name`, case-sensitive ST
                    // string equality. Actuators are claimable in lower case because the injector lower-cases
                    // actuator_name; a sensor is parameterised with its component name verbatim, so a
                    // lower-cased target would circle the ring unclaimed and the sensor would never answer.
                    rows.Add(Row.Cmd(target.Name.Trim(), 0, state.StateID));
                    rows.Add(Row.Wait(id, wait, state.StateID));
                    pos[target.ComponentID] = wait;
                }
                return;
            }

            // Everything below is an OBSERVATION. The process is watching an actuator it may or may not own; the
            // command, if this state issues one at all, was already emitted from the ownership the actuator itself
            // declares. So a condition can only add a requirement that the actuator has REACHED a stop.
            var g = Graph(target, graphs);
            var named = PeerState(target, cond)
                ?? throw Fail(process, state, $"condition '{cond.Name}' does not name a state of '{target.Name}'");

            // A jaw and the task arm do not report the twin's stop numbering: the jaw's direction is inverted by
            // physical wiring (R-12) and the arm folds its whole move into one 1/2/0 handshake. Their arrival is
            // proved by the owning command's own WAIT, so an observation adds nothing -- but only where this
            // state really is the owner. Anywhere else the model is asking for something the ring cannot express.
            if (IsGripper(target) || TemplateMap.IsRobotTaskArm(target))
            {
                if (!Owns(owned, state, target))
                    arrays.Warnings.Add(
                        $"'{process.Name}' state '{state.Name}': condition '{cond.Name}' observes " +
                        $"'{target.Name}', whose reported states are the CAT's handshake rather than the twin's " +
                        "stop numbering, and this state does not own its movement; the step is sequenced by the " +
                        "owning process instead.");
                return;
            }

            // The CAT reports stops, so a condition naming a motion state is already sequenced by the arrival
            // WAIT of whichever command drives through it.
            if (!g.IsStop(named.StateID)) return;

            int settledAt = Settled(target, g.StopNumber(named.StateID), process, state);
            if (Restates(settledHere, target, settledAt)) return;

            // An ENTRY gate on an actuator this process NEVER MOVES has to consume a fresh arrival. The process
            // re-enters its first phase the moment the gate reads true, and an actuator it does not drive is
            // wherever the last cycle left it -- if that is the observed stop the gate is already true and the
            // process restarts immediately, re-doing work that was finished. That is how a no-clamp Assembly,
            // whose entry gate is "Transfer advanced" and which never touches the Transfer, looped straight back
            // and drove the swivel to Work1 to pick up the bearing Disassembly had just released there.
            //
            // A process that DOES own a movement of that actuator is not exposed: its own cycle moves the
            // actuator off the observed stop, so by the time it loops the gate genuinely has to be re-satisfied.
            // Arming it there would be actively wrong -- the no-clamp Disassembly observes a Transfer that is
            // still advanced because it is holding the part Disassembly is about to take, and demanding a
            // departure first would deadlock it against its own return command.
            //
            // The arming value is the stop the actuator's own graph says it arrives FROM, so where that settles
            // to the same value the arming collapses to one WAIT and a gate confirming a resting position is
            // untouched.
            if (entryPhase && !OwnsAny(owned, target))
            {
                var prev = g.PrevStopInto(named.StateID);
                if (prev != null)
                {
                    int armFrom = Settled(target, g.StopNumber(prev), process, state);
                    if (armFrom != settledAt) rows.Add(Row.Wait(id, armFrom, state.StateID));
                }
            }
            // Already established by a command in this recipe: that command's arrival WAIT proved it.
            if (at.TryGetValue(target.ComponentID, out var cur) && g.IsStop(cur) &&
                Settled(target, g.StopNumber(cur), process, state) == settledAt) return;

            rows.Add(Row.Wait(id, settledAt, state.StateID));
            at[target.ComponentID] = named.StateID;
        }

        // Which processes the model declares as commanding this actuator back to a home stop, keyed by the recipe's
        // ring name. Ownership lives on the actuator's own transitions, so this answers the question the
        // per-process stranded-actuator guard cannot: an actuator advanced by one process and driven home by
        // another is not stranded, it is being held for the downstream station.
        internal static IReadOnlyCollection<string> ProcessesCommandingHome(
            string ringName, IReadOnlyList<VueOneComponent> all)
        {
            var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var actuator = all.FirstOrDefault(c => !IsProcess(c) && !IsSensor(c) &&
                string.Equals(TemplateMap.RingKey(c.Name), ringName?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (actuator == null) return res;

            var g = new ActuatorGraph(actuator);
            foreach (var s in actuator.States)
                foreach (var t in s.Transitions)
                {
                    var stop = g.FirstStopVia(t.DestinationStateID);
                    if (stop == null || !g.IsStop(stop)) continue;
                    // Home is the stop the CAT settles to 0 -- the same protocol the commands themselves use.
                    int number = g.StopNumber(stop);
                    bool home = IsSevenStateCommandable(actuator) ? number == 0 : number is 0 or 4;
                    if (!home) continue;
                    foreach (var cond in t.Conditions)
                    {
                        var owner = TryResolve(cond, all);
                        if (owner != null && IsProcess(owner)) res.Add(owner.Name.Trim());
                    }
                }
            return res;
        }

        // Does this process command this actuator anywhere in its cycle? If not, the actuator's position at the
        // process's entry is whatever another process left behind.
        private static bool OwnsAny(Dictionary<string, List<OwnedMove>> owned, VueOneComponent target) =>
            owned.Values.Any(l => l.Any(m =>
                string.Equals(m.Actuator.ComponentID, target.ComponentID, StringComparison.OrdinalIgnoreCase)));

        private static bool Owns(Dictionary<string, List<OwnedMove>> owned, VueOneState state, VueOneComponent target) =>
            owned.TryGetValue(state.StateID, out var m) &&
            m.Any(x => string.Equals(x.Actuator.ComponentID, target.ComponentID, StringComparison.OrdinalIgnoreCase));

        private static ActuatorGraph Graph(VueOneComponent target, Dictionary<string, ActuatorGraph> graphs) =>
            graphs.TryGetValue(target.ComponentID, out var g) ? g : graphs[target.ComponentID] = new ActuatorGraph(target);

        // The commanded trajectory is the actuator's OWN shortest path from where this recipe last left it to the
        // stop the owned movement reaches: every physical stop crossed on the way is commanded in order. So an
        // owned movement whose destination lies beyond an intermediate stop still executes the whole stroke, a
        // stop already occupied costs nothing (no duplicated stroke), and a transfer arm's return branch is driven
        // by the branch it is actually on -- no numeric thresholds, no state-name guessing.
        private static void DriveTo(VueOneComponent process, VueOneState state, VueOneComponent target, int id,
            string stopId, Dictionary<string, string> at, ActuatorGraph g, List<Row> rows)
        {
            // Where this recipe has not yet moved the actuator, its position is only ASSUMED -- the model's
            // Initial_State describes where the cycle starts, never where the arm physically is at deploy.
            bool assumed = !at.TryGetValue(target.ComponentID, out var f);
            string from = assumed ? g.StartId : f!;
            var path = g.PathTo(from, stopId)
                ?? throw Fail(process, state, $"'{target.Name}' cannot reach '{g.NameOf(stopId)}' from '{g.NameOf(from)}' along its own transitions");

            int before = rows.Count;
            int last = g.IsStop(from) ? Settled(target, g.StopNumber(from), process, state) : -1;
            foreach (var sid in path)
            {
                if (!g.IsStop(sid)) continue;
                var (cmd, settled) = Command(target, g.StopNumber(sid), process, state);
                if (settled == last) continue;                     // same physical stop: already there, no new command
                rows.Add(Row.Cmd(TemplateMap.RingKey(target.Name), cmd, state.StateID));
                rows.Add(Row.Wait(id, settled, state.StateID));
                last = settled;
            }
            // The walk commanded nothing because the destination is where the actuator was ASSUMED to be. The
            // owned movement is still a requirement to be AT that stop, so emit the confirming WAIT rather than
            // let it vanish on an assumption. Once a prior command in this recipe has established the position
            // the assumption is gone and the walk speaks for itself.
            if (assumed && rows.Count == before && g.IsStop(stopId))
                rows.Add(Row.Wait(id, Settled(target, g.StopNumber(stopId), process, state), state.StateID));
            at[target.ComponentID] = stopId;
        }

        // The CAT command that drives an actuator to a physical stop, from the CAT's shape only: Five-state
        // Work(2)<-1 / Home(0 or the returned-finished 4, which the runtime settles to 0)<-3; centre-home swivel
        // Work1(2)<-1 / Work2(4)<-3 / Home(0)<-5. A stop the CAT has no command for fails generation.
        private static (int cmd, int settled) Command(VueOneComponent target, int stop,
            VueOneComponent process, VueOneState state)
        {
            if (IsSevenStateCommandable(target))
                return stop switch { 2 => (1, 2), 4 => (3, 4), 0 => (5, 0),
                    _ => throw Fail(process, state, $"'{target.Name}' stop State_Number {stop} is not a centre-home position (0/2/4)") };
            return stop switch { 2 => (1, 2), 0 or 4 => (3, 0),
                _ => throw Fail(process, state, $"'{target.Name}' stop State_Number {stop} is not a five-state position (0/2/4)") };
        }

        private static int Settled(VueOneComponent target, int stop, VueOneComponent process, VueOneState state) =>
            Command(target, stop, process, state).settled;

        // TRUE when a sibling condition of the same transition already required this component at this stop --
        // the AND-group is restating one requirement. Records it otherwise.
        private static bool Restates(Dictionary<string, int> settledHere, VueOneComponent target, int settled)
        {
            if (settledHere.TryGetValue(target.ComponentID, out int s) && s == settled) return true;
            settledHere[target.ComponentID] = settled;
            return false;
        }

        // An actuator's stop map + transition graph. A five-state actuator numbers its own stops canonically
        // (0 Home / 2 Work / 4 returned-complete), so its State_Number IS the stop and geometry is irrelevant --
        // VueOne routinely leaves <Position> at 0 on several of them, and folding those together would alias Work
        // onto Home and silently delete the whole stroke. Only the centre-home swivel re-visits ONE physical
        // place under two different branch numberings, so only there does <Position> decide which stops are the
        // same, taking the number from the first-declared state at that position (its primary cycle's numbering).
        private sealed class ActuatorGraph
        {
            private readonly Dictionary<string, VueOneState> _byId = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<string>> _next = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _stop = new(StringComparer.OrdinalIgnoreCase);
            public readonly string StartId;

            public ActuatorGraph(VueOneComponent c)
            {
                foreach (var s in c.States) _byId[s.StateID] = s;
                foreach (var s in c.States)
                    _next[s.StateID] = s.Transitions.OrderBy(t => t.Priority)
                        .Select(t => t.DestinationStateID).Where(d => !string.IsNullOrEmpty(d) && _byId.ContainsKey(d)).ToList();

                var byPosition = new Dictionary<double, int>();
                bool foldByGeometry = IsSevenStateCommandable(c);
                if (foldByGeometry)
                    foreach (var s in c.States.Where(s => s.StaticState))
                        if (!byPosition.ContainsKey(s.Position)) byPosition[s.Position] = s.StateNumber;
                foreach (var s in c.States.Where(s => s.StaticState))
                    _stop[s.StateID] = foldByGeometry ? byPosition[s.Position] : s.StateNumber;

                StartId = (c.States.FirstOrDefault(s => s.InitialState) ?? c.States.FirstOrDefault())?.StateID ?? string.Empty;
            }

            public bool IsStop(string id) => _stop.ContainsKey(id);
            public int StopNumber(string id) => _stop[id];
            public string NameOf(string id) => _byId.TryGetValue(id, out var s) ? s.Name : id;

            // The stop an owned movement actually commands. A transition's destination is normally a MOTION state
            // (Advancing, Lowering, TurningWork) which the CAT cannot report; the physical stop that motion ends
            // at is the first stop reachable from it, and that is what the command drives to and waits on.
            // The stop the actuator arrives FROM to reach this one: nearest stop walking the transitions
            // backwards. Used to arm an entry gate so it needs a genuine arrival rather than a held level.
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

        // A cross-process condition becomes a WAIT on the peer's OWN announced State_Number wherever that
        // announcement is actually transported into this process's state_table -- the shared ring (same
        // controller, or a merged Feed ring) or CycleReady (an assembly-side process reporting to the Feed ring).
        // A peer Initialisation condition is model readiness, not a runtime gate. When the announcement cannot
        // reach this process the condition is NEVER silently replaced by an unrelated level: either a sibling
        // condition on the same AND-transition already sequences it (reported, not dropped), or the configured
        // material bridge stands in as a freshly-ARMED edge, or generation fails naming the missing route.
        private static void EmitHandoff(VueOneComponent process, VueOneState state, VueOneCondition cond,
            int gateCount, VueOneComponent peer, Ctx ctx, List<Row> rows, RecipeArrays arrays,
            HashSet<string> armed)
        {
            if (SameName(peer, process)) return;                                   // self: the recipe is already here
            var refState = PeerState(peer, cond) ?? throw Fail(process, state, $"condition '{cond.Name}' does not name a state of '{peer.Name}'");
            if (IsInitialisationState(refState))
            {
                // A peer's Initialisation state asserts boot readiness, not the completion of a work cycle; the
                // phase protocol already orders the processes. Recorded so the drop is visible in the report.
                arrays.Warnings.Add(
                    $"'{process.Name}' state '{state.Name}': condition '{cond.Name}' names the peer's " +
                    "Initialisation state, treated as a readiness assertion rather than a runtime phase.");
                return;
            }
            if (!ctx.ProcessIdByName.TryGetValue(peer.Name.Trim(), out int peerId))
                throw Fail(process, state, $"peer process '{peer.Name}' has no deployment id");

            if (Announces(peer, process, ctx))
            {
                // Fresh phase transition: the producer must be seen BEGINNING a cycle before its completion of
                // that cycle counts, so a value held over from the previous cycle cannot release this process.
                //
                // Only the FIRST wait on a given producer arms, though. The arming proves the producer has begun a
                // fresh cycle, and once this recipe has established that, every later phase of the same producer
                // is by construction inside that cycle. Re-arming would demand a SECOND entry announcement, and a
                // producer announces its entry once per cycle -- so a consumer that waits on two of its phases
                // would park forever on the second arming while the producer waited on the consumer to restart.
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

            // The peer's announcement does not reach this controller. The one route that does is the material
            // bridge: the sensor riding the cross-controller segment, which reports material ARRIVING at this
            // station. It may therefore only stand for a handoff that is the SOLE gate of the transition -- the
            // consumer waiting to start on that arrival -- and only as a fresh deasserted->asserted edge, so a
            // level already TRUE at boot or redeploy cannot manufacture a cycle.
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

            // ANDed with other conditions, this process is already mid-cycle: the material arrived and was
            // consumed upstream, so demanding a second arrival would stall it forever. The sibling conditions --
            // which the phase protocol has already ordered -- sequence the step, and this term is reported rather
            // than represented by a signal that does not mean the same thing.
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
                "transported to this controller: no shared ring, no CycleReady, and no material bridge is " +
                "configured to carry it");
        }

        // The twin's own cross-process conditions with each transport resolved. Derived from the context it is
        // asked about and returned by value -- no cache, so nothing survives between generations.
        internal static ProcessHandoffPlan HandoffPlan(Ctx ctx) =>
            ProcessHandoffPlan.Derive(
                ctx.All, ctx.ProcessIdByName,
                (producer, consumer) => Transport(producer, consumer, ctx),
                EntryState,
                (producer, cond) => PeerState(producer, cond),
                cond => TryResolve(cond, ctx.All),
                IsProcess,
                IsInitialisationState);

        // Where a producer's phase can physically land in a consumer's state_table. Sharing a report ring
        // carries it directly. Otherwise the generic process-phase cross-reference does -- but the receiving
        // slot (rdy_id) is declared on the Process1_Generic TYPE, so every instance shares one slot and only
        // ONE consumer in the project can receive a cross-controller phase. That consumer is the one whose ring
        // no producer's report reaches, i.e. the ring holding no other process: the remaining direction is
        // covered by the material bridge instead. Widening this needs rdy_id on the instance interface.
        private static HandoffTransport Transport(VueOneComponent producer, VueOneComponent consumer, Ctx ctx)
        {
            if (SameRing(consumer, producer, ctx)) return HandoffTransport.Ring;
            return SoleCrossControllerConsumer(ctx) is { } sole && SameName(consumer, sole)
                ? HandoffTransport.CrossController
                : HandoffTransport.None;
        }

        // The one process that may receive cross-controller phases: the sole process on its ring. With two
        // rings and one process alone on one of them, that process is the only possible receiver, so the
        // single type-level rdy_id is unambiguous. Any other shape returns null and the caller falls back.
        private static VueOneComponent? SoleCrossControllerConsumer(Ctx ctx)
        {
            var procs = ctx.All.Where(IsProcess).ToList();
            var alone = procs.Where(p => procs.Count(q => SameRing(p, q, ctx)) == 1).ToList();
            return alone.Count == 1 ? alone[0] : null;
        }

        // Does `peer`'s own state announcement reach `consumer`'s state_table?
        private static bool Announces(VueOneComponent peer, VueOneComponent consumer, Ctx ctx) =>
            Transport(peer, consumer, ctx) != HandoffTransport.None;

        private static void Serialize(VueOneComponent process, List<VueOneState> states, List<Row> rows,
            Dictionary<string, int> firstRow, RecipeArrays arrays)
        {
            int end = rows.Count;
            var byId = states.ToDictionary(s => s.StateID, s => s, StringComparer.OrdinalIgnoreCase);

            // Telemetry: a 1-based ordinal per declared state, taken from the twin's own declaration
            // order so it is stable across regenerations and independent of how the chain was walked.
            // 0 stays free to mean "no owning state". Two states may share a name in the twin; they still
            // get distinct ordinals, so a phase is never conflated with a different one.
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
                bool last = i + 1 >= rows.Count || rows[i + 1].StateId != r.StateId;
                arrays.NextStep.Add(last ? DestRow(r.StateId) : i + 1);
                // Telemetry only: the row's owning VueOne state, so the engine can report the phase the
                // model names. Resolved from the same StateId the row was built under, never guessed.
                arrays.ProcessStateByRow.Add(
                    r.StateId != null && ordinalOf.TryGetValue(r.StateId, out var ord) ? ord : 0);
            }

            arrays.StepType.Add(StepType.End);
            arrays.CmdTargetName.Add(string.Empty);
            arrays.CmdStateArr.Add(0);
            arrays.Wait1Id.Add(0);
            arrays.Wait1State.Add(0);
            arrays.NextStep.Add(TerminalLoopsHome(states) ? 0 : end);
            // END carries the last row's phase so the final publish does not report a phantom state 0.
            arrays.ProcessStateByRow.Add(
                arrays.ProcessStateByRow.Count > 0 ? arrays.ProcessStateByRow[^1] : 0);
        }

        private static bool TerminalLoopsHome(List<VueOneState> states)
        {
            var dst = states.Count == 0 ? null : states[^1].Transitions.OrderBy(t => t.Priority).FirstOrDefault()?.DestinationStateID;
            return states.Any(s => s.StateID == dst && IsInitialisationState(s));
        }

        // The first state of a process's transition chain: the phase it announces when a cycle begins.
        private static VueOneState? EntryState(VueOneComponent process)
        {
            var ordered = OrderStatesByTransitionChain(process.States);
            return ordered.Count > 0 ? ordered[0] : null;
        }

        // The peer state a condition names (by StateID, else by the name after the slash).
        private static VueOneState? PeerState(VueOneComponent peer, VueOneCondition cond) =>
            peer.States.FirstOrDefault(s => string.Equals(s.StateID?.Trim(), cond.ID?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? peer.States.FirstOrDefault(s => string.Equals(s.Name?.Trim(), After(cond.Name), StringComparison.OrdinalIgnoreCase));

        private static int SlotOf(VueOneComponent target, Ctx ctx, VueOneComponent process, VueOneState state)
        {
            // A deployment-allocated cross-controller slot (cover/robot/synth) is authoritative: the report lands
            // there over the cross-PLC ring, overriding whatever positional id the local scoped map assigned.
            if (ctx.IdsByName.TryGetValue(target.Name.Trim(), out int nid))
                return nid;
            if (!string.IsNullOrWhiteSpace(target.ComponentID) && ctx.Ids.TryGetValue(target.ComponentID.Trim(), out int id))
                return id;
            throw Fail(process, state, $"'{target.Name}' has no state_table slot on this ring");
        }

        private static int StateNumberOf(VueOneCondition cond, VueOneComponent target, VueOneComponent process, VueOneState? state)
        {
            var st = target.States.FirstOrDefault(s =>
                        string.Equals(s.StateID?.Trim(), cond.ID?.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? target.States.FirstOrDefault(s =>
                        string.Equals(s.Name?.Trim(), After(cond.Name), StringComparison.OrdinalIgnoreCase));
            if (st == null)
                throw Fail(process, state, $"condition '{cond.Name}' does not name a state of '{target.Name}'");
            return st.StateNumber;
        }

        private static VueOneComponent Resolve(VueOneCondition cond, IReadOnlyList<VueOneComponent> all) =>
            TryResolve(cond, all) ?? throw new InvalidOperationException(
                $"[Compile] condition '{cond.Name}' (ComponentID '{cond.ComponentID}') resolves to no component.");

        private static VueOneComponent? TryResolve(VueOneCondition cond, IReadOnlyList<VueOneComponent> all)
        {
            if (!string.IsNullOrWhiteSpace(cond.ComponentID))
                return all.FirstOrDefault(c => string.Equals(c.ComponentID?.Trim(), cond.ComponentID.Trim(), StringComparison.OrdinalIgnoreCase));
            var name = cond.Name?.IndexOf('/') is int i and >= 0 ? cond.Name.Substring(0, i).Trim() : cond.Name?.Trim();
            return string.IsNullOrEmpty(name) ? null
                : all.FirstOrDefault(c => string.Equals(c.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsProcess(VueOneComponent? c) => c != null && string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase);
        private static bool IsSensor(VueOneComponent c) => string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase);
        // Grippers are Type "Robot" but not the task arm: a Five-state jaw whose two strokes share one rest sensor.
        private static bool IsGripper(VueOneComponent c) => string.Equals(c.Type, "Robot", StringComparison.OrdinalIgnoreCase) && !TemplateMap.IsRobotTaskArm(c);
        private static bool SameName(VueOneComponent a, VueOneComponent b) => string.Equals(a.Name?.Trim(), b.Name?.Trim(), StringComparison.OrdinalIgnoreCase);
        private static bool IsFeedSide(VueOneComponent p, Ctx ctx) => ctx.ProcessIdByName.TryGetValue(p.Name?.Trim() ?? "", out int pid) && pid == ctx.FeedProcessId;
        private static bool SameRing(VueOneComponent a, VueOneComponent b, Ctx ctx) => ctx.MergeFeedRing || IsFeedSide(a, ctx) == IsFeedSide(b, ctx);
        private static string After(string? s) => string.IsNullOrEmpty(s) ? string.Empty : (s.LastIndexOf('/') is int i and >= 0 ? s.Substring(i + 1) : s);

        private static InvalidOperationException Fail(VueOneComponent process, VueOneState? state, string why) =>
            new($"[Compile] '{process.Name}'{(state == null ? "" : $" state '{state.Name}'")}: {why}.");

        private sealed class Row
        {
            public int Step; public string? Target; public int CmdState; public int WaitId; public int WaitState; public string StateId = "";
            public static Row Cmd(string t, int s, string id) => new() { Step = StepType.Cmd, Target = t, CmdState = s, StateId = id };
            public static Row Wait(int i, int s, string id) => new() { Step = StepType.Wait, WaitId = i, WaitState = s, StateId = id };
        }
    }
}
