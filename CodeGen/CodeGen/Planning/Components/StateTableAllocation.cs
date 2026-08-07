using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation
{
    // Which state_table slot each component reports on.
    //
    // Slots are positional: sensors first, then actuators, in ComponentRegistry's declared id order. Two
    // components need a slot that is NOT positional, and both are computed here so the recipe and the
    // layout can never disagree about them:
    //
    //  * the part-present sensor, pinned to the slot the synth injection reserves so a twin that declares
    //    it and one that leaves it to the rig produce identical ids;
    //  * the top-cover sensor, whose report crosses onto the Assembly ring and therefore needs a slot free
    //    on THAT ring rather than the one its position would give it.
    //
    // Pure: everything is derived from the components handed in. Nothing is cached and nothing is stored,
    // so a second generation in the same process cannot inherit the first one's answer -- which is exactly
    // what a stale slot did, silently, with no error: the cover-place gate simply waited forever on a
    // component that never reported.
    public static class StateTableAllocation
    {
        // The first actuator slot. The part-present sensor holds a RESERVED slot rather than a positional
        // one, so it must not push the actuator range up: every actuator id, and with it every recipe
        // Wait1Id, interlock SourceID and HCF binding, would shift by one.
        public static int ActuatorIdStart(IReadOnlyList<VueOneComponent> sensors) =>
            sensors.Count - sensors.Count(s => HandoffPlanner.IsPartAtAssembly(s.Name));

        // The top-cover sensor's slot: the highest component-range id no ASSEMBLY-ring member occupies.
        //
        // Occupied means every M580/BX1 component, every member of the cross-controller segment, every
        // allocated process slot, the robot's reserved slot, the synth sensors -- and, when the topology
        // merges the rings, the Feed components too, because then they report onto the same ring.
        //
        // Highest-free rather than a configured constant: with a clamp the Feed ids sit on a separate ring
        // so {0,4,5,6} are free and this yields 6, the rig-proven value; without one the merged ring fills
        // [0..15] and this yields 16. A fixed 6 collides with the Transfer on the merged ring and deadlocks
        // the cover-place gate, and a fixed 14/15/16 drifts the other way when no Clamp occupies a slot.
        public static int TopCoverSensorSlot(StationContents contents, bool ringsMerged)
        {
            var catalog = RigCatalog.Current;
            var allocation = ControllerAllocation.Current;
            var cross = catalog.CrossRingSegment;

            var occupied = new HashSet<int>(catalog.ProcessSlots.Values) { catalog.RobotActuatorId };
            foreach (var synth in catalog.SynthSensors) occupied.Add(synth.Id);

            void MarkOccupied(string name, int id)
            {
                if (TemplateMap.IsTopCoverSensor(name)) return;          // this is the slot being placed
                var plc = allocation.Of(name);
                if (plc is PlcAssignment.M580 or PlcAssignment.BX1 || cross.Contains(name) || ringsMerged)
                    occupied.Add(id);
            }

            for (int i = 0; i < contents.Sensors.Count; i++)
                MarkOccupied(contents.Sensors[i].Name,
                    HandoffPlanner.IsPartAtAssembly(contents.Sensors[i].Name)
                        ? HandoffPlanner.PartAtAssembly.Id
                        : i);
            int actuatorIdStart = ActuatorIdStart(contents.Sensors);
            for (int i = 0; i < contents.Actuators.Count; i++)
            {
                // The task arm is APPENDED to the actuator order, so its positional id here is not the one
                // it deploys with -- it takes the robot's reserved slot, already occupied above. Marking the
                // positional one would falsely reserve a cover slot that is genuinely free.
                if (TemplateMap.IsRobotTaskArm(contents.Actuators[i])) continue;
                MarkOccupied(contents.Actuators[i].Name, actuatorIdStart + i);
            }

            for (int slot = ComponentIdCeiling; slot >= 0; slot--)
                if (!occupied.Contains(slot)) return slot;

            throw new InvalidOperationException(
                "No free state_table slot for the top-cover sensor: every id in " +
                $"[0..{ComponentIdCeiling}] is claimed by an Assembly-ring member (occupied = " +
                $"{string.Join(",", occupied.Where(i => i <= ComponentIdCeiling).OrderBy(i => i))}). " +
                "The cover interlock cannot be placed without colliding with another component's report. " +
                "Widening state_table past its declared size touches every CAT's updateComponentState.");
        }

        // Highest id a component may take. Above it are the process/robot slots, below it the positional
        // component range; state_table is declared one larger than this in every CAT.
        private const int ComponentIdCeiling = 16;
    }
}
