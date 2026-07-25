using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Models;
using static CodeGen.Translation.Process.Recipes.TransitionChainParser;
using static CodeGen.Translation.Process.Recipes.RecipeCommandVocabulary;

namespace CodeGen.Translation.Process.Recipes
{
    // Deterministic Control.xml -> recipe rows. A process state names an actuator ACTION; the state's outgoing
    // condition names the actuator state that ACTION must reach; the command trajectory to reach it comes from the
    // target's own State_Numbers via the CAT command protocol (never from a name string).
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

        [Flags]
        private enum Announce { None = 0, Ring = 1, CycleReady = 2 }

        public static RecipeArrays Compile(VueOneComponent process, int processId, Ctx ctx)
        {
            var arrays = new RecipeArrays();
            foreach (var kv in ctx.Ids) arrays.ComponentRegistry[kv.Key] = kv.Value;

            var states = OrderStatesByTransitionChain(process.States);
            foreach (var line in BuildTransitionTable(process.States, states)) arrays.TransitionTable.Add(line);
            var announce = ComputeAnnounce(process, ctx);

            var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var at = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);      // actuator -> the StateID it now rests in
            var graphs = new Dictionary<string, ActuatorGraph>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<Row>();
            var firstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int idx = 0; idx < states.Count; idx++)
            {
                var state = states[idx];
                int before = rows.Count;
                // A process reaches an ordinary phase as soon as the previous phase's work finished, so it
                // announces that phase before the conditions it then waits on. Its ENTRY phase is different: the
                // process has not begun a cycle until it has been authorised to, so the entry announcement is
                // emitted AFTER the entry conditions. Otherwise every process would publish "I have started" the
                // moment the controllers are deployed, before any material arrived.
                bool entryPhase = idx == 0;
                void Announcement()
                {
                    if (!announce.TryGetValue(state.StateID, out var kind)) return;
                    if (kind.HasFlag(Announce.Ring))       rows.Add(Row.Cmd(process.Name.Trim(), state.StateNumber, state.StateID));   // report own State_Number on the ring
                    if (kind.HasFlag(Announce.CycleReady)) rows.Add(Row.Cmd("cycle_ready", state.StateNumber, state.StateID));         // ... and across CycleReady to the Feed ring
                }
                if (!entryPhase) Announcement();
                foreach (var t in state.Transitions.OrderBy(t => t.Priority))
                {
                    // Conditions of one transition are ANDed, so two of them naming states of the SAME component
                    // that settle at the SAME stop (VueOne models a rest as both "ReturnedHome" and
                    // "ReturnedFinished") state one requirement, not two: without this the second sends the
                    // actuator a full lap back around its graph to re-reach the place it already holds.
                    var settledHere = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var cond in t.Conditions)
                        EmitCondition(process, state, cond, t.Conditions.Count, ctx, pos, at, graphs, rows, arrays, settledHere);
                }
                if (entryPhase) Announcement();
                if (rows.Count > before) firstRow[state.StateID] = before;
            }

            Serialize(process, states, rows, firstRow, arrays);
            arrays.OrderingSummary = $"'{process.Name}' compiled from Control.xml: {arrays.StepType.Count} rows.";
            return arrays;
        }

        private static void EmitCondition(VueOneComponent process, VueOneState state, VueOneCondition cond,
            int gateCount, Ctx ctx, Dictionary<string, int> pos,
            Dictionary<string, string> at, Dictionary<string, ActuatorGraph> graphs, List<Row> rows,
            RecipeArrays arrays, Dictionary<string, int> settledHere)
        {
            var target = Resolve(cond, ctx.All);
            if (IsProcess(target)) { EmitHandoff(process, state, cond, gateCount, target, ctx, rows, arrays); return; }

            int id = SlotOf(target, ctx, process, state);
            int reached = StateNumberOf(cond, target, process, state);

            if (IsSensor(target))
            {
                int wait = ctx.SensorPresent.TryGetValue(target.Name.Trim(), out int p) ? p : reached;
                if (!pos.TryGetValue(target.ComponentID, out int c) || c != wait)
                {
                    rows.Add(Row.Wait(id, wait, state.StateID));
                    pos[target.ComponentID] = wait;
                }
                return;
            }

            // A task arm (VcID=UR3e -> Robot_Task_CAT) runs its whole modeled move on one StartTask: its core
            // reports 1 while running, 2 on completion, 0 when reset. Fold every condition on it into that one
            // start(1)->done(2)->reset(2)->ready(0) handshake -- Robot never commands 3 or 5.
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

            // A jaw's direction is a physical wiring fact (INVARIANTS R-12: the rig is wired energise-to-GRIP
            // while the twin models the jaw geometry energise-to-OPEN), so the twin's stop must never pick the
            // direction or the part is dropped. It CAN say WHEN: once the jaw has acted, a condition naming the
            // stop it already holds is a hold-guard, and one naming the other stop is the opposite action. Only
            // the very first reference has nothing to compare against, and there the universal rule applies -- a
            // transfer must hold before it moves, so the first action on a jaw grips.
            if (IsGripper(target))
            {
                var (gc, gs) = !pos.TryGetValue(target.ComponentID, out int jaw) ? (1, 2)
                             : Command(target, reached, process, state).settled == jaw ? (0, jaw)   // holds: no action
                             : jaw == 2 ? (3, 0) : (1, 2);
                if (gc == 0 || Restates(settledHere, target, gs)) return;
                rows.Add(Row.Cmd(TemplateMap.RingKey(target.Name), gc, state.StateID));
                rows.Add(Row.Wait(id, gs, state.StateID));
                pos[target.ComponentID] = gs;
                return;
            }

            EmitTrajectory(process, state, cond, target, id, at, graphs, rows, settledHere);
        }

        // The command trajectory is the actuator's OWN shortest path from where this recipe last left it to the
        // state the condition names: every physical stop crossed on the way is commanded in order. So a process
        // state that names only a stroke's END (Checker/RisingFinished) still executes the whole stroke (down,
        // then up), a stop already occupied costs nothing (no duplicated stroke), and a transfer arm's return
        // branch is driven by the branch it is actually on -- no numeric thresholds, no state-name guessing.
        private static void EmitTrajectory(VueOneComponent process, VueOneState state, VueOneCondition cond,
            VueOneComponent target, int id, Dictionary<string, string> at,
            Dictionary<string, ActuatorGraph> graphs, List<Row> rows, Dictionary<string, int> settledHere)
        {
            if (!graphs.TryGetValue(target.ComponentID, out var g))
                graphs[target.ComponentID] = g = new ActuatorGraph(target);

            var dest = PeerState(target, cond)
                ?? throw Fail(process, state, $"condition '{cond.Name}' does not name a state of '{target.Name}'");
            if (g.IsStop(dest.StateID) &&
                Restates(settledHere, target, Settled(target, g.StopNumber(dest.StateID), process, state))) return;
            string from = at.TryGetValue(target.ComponentID, out var f) ? f : g.StartId;
            var path = g.PathTo(from, dest.StateID)
                ?? throw Fail(process, state, $"'{target.Name}' cannot reach '{dest.Name}' from '{g.NameOf(from)}' along its own transitions");

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
            at[target.ComponentID] = dest.StateID;
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
            int gateCount, VueOneComponent peer, Ctx ctx, List<Row> rows, RecipeArrays arrays)
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
                var entry = EntryState(peer);
                int done = refState.StateNumber;
                if (entry != null && entry.StateNumber != done)
                    rows.Add(Row.Wait(peerId, entry.StateNumber, state.StateID));
                else if (done == 0)
                    throw Fail(process, state,
                        $"condition '{cond.Name}' completes on State_Number 0, which is also the initial value of " +
                        $"a state_table slot, and '{peer.Name}' declares no earlier phase to arm against, so the " +
                        "completion could never be told apart from a slot that was merely never written");
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

        // Does `peer`'s own state announcement reach `consumer`'s state_table? Same ring (same controller, or a
        // merged Feed ring) carries it directly; CycleReady carries an assembly-side process to the Feed ring.
        private static bool Announces(VueOneComponent peer, VueOneComponent consumer, Ctx ctx) =>
            SameRing(consumer, peer, ctx) || (IsFeedSide(consumer, ctx) && !IsFeedSide(peer, ctx));


        // Which of THIS process's states peers reference, and by which transport the peer reads them: the shared
        // ring (or an M580 peer read by a Feed consumer, which also needs the ring announce) publish the number
        // as a self-named CMD; a Feed consumer of an M580 producer additionally needs it on CycleReady. A Feed
        // producer read by an M580 consumer publishes nothing here (that consumer uses PartAtAssembly instead).
        private static Dictionary<string, Announce> ComputeAnnounce(VueOneComponent process, Ctx ctx)
        {
            var res = new Dictionary<string, Announce>(StringComparer.OrdinalIgnoreCase);
            bool selfFeed = IsFeedSide(process, ctx);
            foreach (var q in ctx.All.Where(IsProcess))
            {
                if (SameName(q, process)) continue;
                foreach (var c in q.States.SelectMany(s => s.Transitions).SelectMany(t => t.Conditions))
                {
                    var tgt = TryResolve(c, ctx.All);
                    if (tgt == null || !IsProcess(tgt) || !SameName(tgt, process)) continue;
                    var s = PeerState(process, c);
                    // A reference to our Initialisation state is the peer asserting readiness; it produces no
                    // WAIT, so it must not make us announce a phase either -- an announcement no one consumes
                    // still writes the consumer's slot and would collide with the phase token that ships there.
                    if (s == null || IsInitialisationState(s)) continue;
                    var how = SameRing(q, process, ctx) ? Announce.Ring
                            : IsFeedSide(q, ctx) && !selfFeed ? Announce.CycleReady
                            : Announce.None;    // Feed producer read across rings -> consumer uses the material sensor
                    if (how == Announce.None) continue;
                    res[s.StateID] = res.GetValueOrDefault(s.StateID) | how;
                    // ... and the entry phase, which is what that consumer arms on.
                    var entry = EntryState(process);
                    if (entry != null) res[entry.StateID] = res.GetValueOrDefault(entry.StateID) | how;
                }
            }
            return res;
        }

        private static void Serialize(VueOneComponent process, List<VueOneState> states, List<Row> rows,
            Dictionary<string, int> firstRow, RecipeArrays arrays)
        {
            int end = rows.Count;
            var byId = states.ToDictionary(s => s.StateID, s => s, StringComparer.OrdinalIgnoreCase);

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
            }

            arrays.StepType.Add(StepType.End);
            arrays.CmdTargetName.Add(string.Empty);
            arrays.CmdStateArr.Add(0);
            arrays.Wait1Id.Add(0);
            arrays.Wait1State.Add(0);
            arrays.NextStep.Add(TerminalLoopsHome(states) ? 0 : end);
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
