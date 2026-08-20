using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation
{
    // Which state_table slot every reporter on the ring owns: sensors, actuators and processes alike.
    // Component slots are positional (sensors, then actuators, in the roster's id order); reserved and
    // computed slots are placed here too. Pure, so a second generation cannot inherit the first's answer.
    public static class StateTableAllocation
    {
        // The declared FLOOR on how many reporters one ring can carry, from Config/config.yaml. The
        // capacity a run deploys is DERIVED (see Required), and the FBTs declaring state_table match it.
        public static int Capacity => GenerationConfig.Current.StateTableCapacity;

        // One past the highest slot any reporter took, never below the floor. Raising it only appends.
        public static int Required(IReadOnlyDictionary<string, int> slots) =>
            Math.Max(Capacity, slots.Count == 0 ? 0 : slots.Values.Max() + 1);

        // Boundary between the positional component range and the slots reserved above it. Declared
        // rather than computed: moving it would renumber components.


        // Name -> slot for everything that writes the table. On a clash the PROCESS moves, never the
        // component, whose slot is also its actuator_id, its interlock SourceID and its HCF binding.
        public static IReadOnlyDictionary<string, int> Slots(StationContents contents,
            ReportGraph rings, IReadOnlyDictionary<string, int> reservations, PlantFacts facts)
        {
            int componentIdCeiling = facts.ComponentIdCeiling;
            // A PROCESS reservation is the one that may have to move, because a merged ring can put a
            // component on the same slot. Both kinds come from the one stableSlot column.
            var processNames = new HashSet<string>(contents.Processes, StringComparer.OrdinalIgnoreCase);
            var processSlots = reservations
                .Where(kv => processNames.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            int? Reserved(string? n) =>
                n != null && reservations.TryGetValue(n, out int v) ? v : (int?)null;
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? cover = null;
            int positional = 0;
            // A component the roster does not rank is new. It takes a slot ABOVE everything ranked, so
            // adding one leaves every existing id where it is.
            var appended = new List<string>();
            bool Ranked(string? n) => n != null && contents.Ranked.Contains(n.Trim());

            for (int i = 0; i < contents.Sensors.Count; i++)
            {
                var n = contents.Sensors[i].Name;
                if (!Ranked(n) && Reserved(n) == null) { appended.Add(n.Trim()); continue; }
                // The cover sensor's SLOT is chosen below, but it still consumes a positional index here:
                // skipping it would pull every actuator id down by one.
                if (facts.TakesRingScopedSlot(n)) { cover = n; positional++; continue; }
                // A reserved sensor does NOT consume a positional index, else every actuator id shifts.
                var fixedSlot = Reserved(n);
                if (fixedSlot.HasValue) { byName[n] = fixedSlot.Value; continue; }
                byName[n] = positional++;
            }
            int actuatorIdStart = positional;
            int rank = 0;
            for (int i = 0; i < contents.Actuators.Count; i++)
            {
                var an = contents.Actuators[i].Name;
                var fixedSlot = Reserved(an);
                if (fixedSlot.HasValue) { byName[an] = fixedSlot.Value; rank++; continue; }
                if (!Ranked(an)) { appended.Add(an.Trim()); continue; }
                byName[an] = actuatorIdStart + rank++;
            }

            // Reserved slots the twin need not declare are ring writers like any other, so a placement
            // must see them. A PROCESS reservation is applied below, being the only kind that may move.
            foreach (var kv in reservations)
                if (!processNames.Contains(kv.Key) && !byName.ContainsKey(kv.Key))
                    byName[kv.Key] = kv.Value;

            // Pin every process slot before the first placement, so a reporter that has to move cannot
            // take a slot a process was promised and start a chain of moves.
            var taken = new HashSet<int>(byName.Values);
            foreach (var pinned in processSlots.Values) taken.Add(pinned);

            // Highest component-range slot free on the ring the cover reports onto. A constant here would
            // drift per model: it collides with the Transfer on a merged ring, or moves when no Clamp exists.
            if (cover != null)
            {
                // Free means free ON THE COVER'S RING. Process slots are excluded whatever their ring,
                // since a process may yet be relocated onto one.
                bool Free(int s) =>
                    !processSlots.Values.Contains(s) &&
                    !byName.Any(kv => kv.Value == s && rings.SameDomain(kv.Key, cover));
                // Component range first, so an existing model keeps the id it already has.
                int slot = Enumerable.Range(0, componentIdCeiling + 1).Reverse().FirstOrDefault(Free, -1);
                if (slot < 0)
                    slot = Enumerable.Range(componentIdCeiling + 1, Capacity - componentIdCeiling - 1)
                        .Reverse().FirstOrDefault(Free, -1);
                if (slot < 0)
                    throw new InvalidOperationException(
                        $"[state_table] No free slot for the top-cover sensor '{cover}': all {Capacity} ids " +
                        "are claimed by reporters on its ring. The cover interlock cannot be placed without " +
                        "reading another component's report.");
                byName[cover] = slot;
                taken.Add(slot);
            }

            foreach (var (process, pinned) in processSlots.OrderBy(p => p.Value))
            {
                var clash = byName.FirstOrDefault(kv =>
                    kv.Value == pinned && rings.SameDomain(kv.Key, process));
                int slot = pinned;
                if (clash.Key != null)
                    slot = Enumerable.Range(0, int.MaxValue).First(s => !taken.Contains(s));
                byName[process] = slot;
                taken.Add(slot);
            }

            // An unpinned process still announces its phase, so it takes the lowest free slot.
            foreach (var process in contents.Processes)
            {
                if (byName.ContainsKey(process)) continue;
                int slot = Enumerable.Range(0, int.MaxValue).First(s => !taken.Contains(s));
                byName[process] = slot;
                taken.Add(slot);
            }

            // Newly declared components last, above everything ranked; the table grows to fit them.
            foreach (var name in appended)
            {
                if (byName.ContainsKey(name)) continue;
                int slot = Enumerable.Range(0, int.MaxValue).First(s => !taken.Contains(s));
                byName[name] = slot;
                taken.Add(slot);
            }

            RejectAmbiguousSlots(byName, rings);
            return byName;
        }

        // Two reporters on one ring writing one slot makes every WAIT on it mean two things. Sharing
        // ACROSS rings is legitimate (each ring keeps its own table), so only same-ring pairs are refused.
        private static void RejectAmbiguousSlots(IReadOnlyDictionary<string, int> byName, ReportGraph rings)
        {
            var bad = byName
                .GroupBy(kv => kv.Value)
                .SelectMany(g => g.SelectMany(a => g
                    .Where(b => string.Compare(a.Key, b.Key, StringComparison.Ordinal) < 0 &&
                                rings.SameDomain(a.Key, b.Key))
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

    }
}
