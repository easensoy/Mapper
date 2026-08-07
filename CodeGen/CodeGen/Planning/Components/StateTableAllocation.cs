using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation
{
    // Which state_table slot every reporter on the ring owns — sensors, actuators and processes alike.
    // Component slots are positional (sensors, then actuators, in the roster's id order); the part-present
    // sensor, the top-cover sensor and a process whose pinned slot is taken are placed here instead, so
    // the recipe and the layout can never disagree about one. Pure: derived only from the arguments, so a
    // second generation cannot inherit the first one's answer.
    public static class StateTableAllocation
    {
        // state_table's declared size, and so the number of reporters one ring can carry. It is the
        // ArraySize of state_table on ProcessRuntime_Generic_v1, ProcessStateBusHandler and
        // updateComponentState; all three must agree or a report writes past the end of one of them.
        public const int Capacity = 24;

        // Highest id a component may take. Above it sit the process and robot slots.
        private const int ComponentIdCeiling = 16;

        // The first actuator slot. The part-present sensor holds a RESERVED slot rather than a positional
        // one, so it must not push the actuator range up: every actuator id, and with it every recipe
        // Wait1Id, interlock SourceID and HCF binding, would shift by one.
        public static int ActuatorIdStart(IReadOnlyList<VueOneComponent> sensors) =>
            sensors.Count - sensors.Count(s => HandoffPlanner.IsPartAtAssembly(s.Name));

        // Name -> slot for everything that writes the table. A pinned process slot is only unambiguous
        // while the component sharing it reports on a DIFFERENT ring; a merged twin puts both in one table
        // and a WAIT there is satisfied by either. The PROCESS moves, never the component, whose slot is
        // also its actuator_id, its interlock SourceID and its HCF binding.
        public static IReadOnlyDictionary<string, int> Slots(StationContents contents,
            ControllerAllocation allocation, bool ringsMerged)
        {
            var catalog = RigCatalog.Current;
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? cover = null;

            for (int i = 0; i < contents.Sensors.Count; i++)
            {
                var n = contents.Sensors[i].Name;
                if (TemplateMap.IsTopCoverSensor(n)) { cover = n; continue; }   // placed below
                byName[n] = HandoffPlanner.IsPartAtAssembly(n) ? HandoffPlanner.PartAtAssembly.Id : i;
            }
            int actuatorIdStart = ActuatorIdStart(contents.Sensors);
            for (int i = 0; i < contents.Actuators.Count; i++)
                byName[contents.Actuators[i].Name] = TemplateMap.IsRobotTaskArm(contents.Actuators[i])
                    ? catalog.RobotActuatorId                 // appended to the order, so not positional
                    : actuatorIdStart + i;

            // Reserved slots the twin need not declare — an injected synth sensor and the task arm — are
            // ring writers like any other, so a placement must see them.
            foreach (var synth in catalog.SynthSensors)
                if (!byName.ContainsKey(synth.Name)) byName[synth.Name] = synth.Id;
            byName.TryAdd("Robot", catalog.RobotActuatorId);

            // Every pinned process slot is reserved before the first placement, so a reporter that has to
            // move cannot take a slot a process was already promised and start a chain of moves.
            var taken = new HashSet<int>(byName.Values);
            foreach (var pinned in catalog.ProcessSlots.Values) taken.Add(pinned);

            // Highest component-range slot free on the ring the cover reports onto, not a constant: with
            // a clamp that yields 6 (the rig-proven value), without one 16. A fixed 6 collides with the
            // Transfer on a merged ring; a fixed 14/15/16 drifts when no Clamp occupies a slot.
            if (cover != null)
            {
                // Free means free ON THE COVER'S RING: a Feed-side component's slot is available here
                // precisely because its report never arrives on the assembly ring. Process slots are
                // excluded whatever their ring, since a process may yet be relocated onto one.
                int slot = Enumerable.Range(0, ComponentIdCeiling + 1).Reverse().FirstOrDefault(
                    s => !catalog.ProcessSlots.Values.Contains(s) &&
                         !byName.Any(kv => kv.Value == s && SameRing(kv.Key, cover, allocation, ringsMerged)),
                    -1);
                if (slot < 0)
                    throw new InvalidOperationException(
                        $"[state_table] No free slot for the top-cover sensor '{cover}': every id in " +
                        $"[0..{ComponentIdCeiling}] is claimed by a reporter on its ring. The cover " +
                        "interlock cannot be placed without reading another component's report.");
                byName[cover] = slot;
                taken.Add(slot);
            }

            foreach (var (process, pinned) in catalog.ProcessSlots.OrderBy(p => p.Value))
            {
                var clash = byName.FirstOrDefault(kv =>
                    kv.Value == pinned && SameRing(kv.Key, process, allocation, ringsMerged));
                int slot = pinned;
                if (clash.Key != null)
                {
                    slot = Enumerable.Range(0, Capacity).FirstOrDefault(s => !taken.Contains(s), -1);
                    if (slot < 0)
                        throw new InvalidOperationException(
                            $"[state_table] '{process}' shares slot {pinned} with component " +
                            $"'{clash.Key}' on the same report ring, and all {Capacity} slots are taken, " +
                            "so neither can be moved. Both write that slot, so a WAIT on it is satisfied " +
                            "by whichever reported last. Widen state_table's ArraySize on " +
                            "ProcessRuntime_Generic_v1, ProcessStateBusHandler and updateComponentState.");
                }
                byName[process] = slot;
                taken.Add(slot);
            }

            RejectAmbiguousSlots(byName, allocation, ringsMerged);
            return byName;
        }

        // Two reporters on one ring writing one slot makes every WAIT on it mean two things, and the
        // build looks correct while it deadlocks. Sharing ACROSS rings is legitimate — each ring keeps its
        // own copy of the table — so only same-ring pairs are refused.
        private static void RejectAmbiguousSlots(IReadOnlyDictionary<string, int> byName,
            ControllerAllocation allocation, bool ringsMerged)
        {
            var bad = byName
                .GroupBy(kv => kv.Value)
                .SelectMany(g => g.SelectMany(a => g
                    .Where(b => string.Compare(a.Key, b.Key, StringComparison.Ordinal) < 0 &&
                                SameRing(a.Key, b.Key, allocation, ringsMerged))
                    .Select(b => $"slot {g.Key}: '{a.Key}' and '{b.Key}'")))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            if (bad.Count == 0) return;
            throw new InvalidOperationException(
                "[state_table] " + bad.Count + " slot(s) have two reporters on one ring — " +
                string.Join("; ", bad) + ". Each writes the same state_table entry, so every WAIT on it " +
                "is satisfied by whichever reported last. Give one of them a free slot in " +
                $"Config/smc-rig.yml or Config/layout.yml, within the {Capacity}-slot table.");
        }

        // Whether two reporters land in the same state_table: the Feed controller keeps its own ring, the
        // assembly side spans M580 and BX1 plus the Feed hardware spliced into it, and a merged twin folds
        // all of that into one.
        private static bool SameRing(string a, string b, ControllerAllocation allocation, bool ringsMerged)
        {
            if (ringsMerged) return true;
            static string Group(string n, ControllerAllocation alloc) =>
                RigCatalog.Current.CrossRingSegment.Contains(n, StringComparer.OrdinalIgnoreCase) ||
                !alloc.IsFeedSide(n) ? "assembly" : "feed";
            return Group(a, allocation) == Group(b, allocation);
        }
    }
}
