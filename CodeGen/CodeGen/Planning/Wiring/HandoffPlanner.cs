using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;

namespace CodeGen.Translation
{
    // Single source of truth for the cross-station handoffs (recipe generator, ring wiring, and HCF
    // binder all read here). Transports: LocalRing = producer + consumer share one PLC's stateRprtCmd
    // ring; CrossDeviceSegment = a short M262 segment spliced onto the M580 ring at the Disassembly
    // seam via two cross-device adapter hops EAE bridges, WITHOUT touching the M580 actuator ring.
    public static class HandoffPlanner
    {
        public enum HandoffTransport { LocalRing, CrossDeviceSegment }

        public sealed record HandoffSpec(
            string Name,
            string Producer,
            string Consumer,
            string SignalComponent,
            int WaitId,
            int WaitState,
            HandoffTransport Transport);

        // Master switch for the M262<->M580 cross-device discharge + part-present handoffs. RIG-VERIFY:
        // the M262<->M580 cross-device adapter transport (only M580<->BX1 is rig-proven). OFF =
        // decoupled local rings (Assembly gates on the local BearingSensor).
        public const bool CrossPlcDischargeActive = true;

        // BX1 cover actuators spliced onto the M580 ring (between Clamp and Assembly_Station); cover
        // place/remove fold into the M580 recipes. TopCoverSenosr stays OFF the ring (its id collides
        // with PartAtAssembly on the M580 state_table).
        public static IReadOnlyList<string> CoverDetour =>
            new[] { "CoverPNP_Hr", "CoverPNP_Vr", "CoverPnp_Gripper" };

        public static bool IsCoverDetourActuator(string name) =>
            CoverDetour.Any(c => string.Equals(c, name, System.StringComparison.OrdinalIgnoreCase));

        // The M262 part-present proximity sensor (DI08); id/pin from MapperConfig.M262SynthSensors
        // (the rig wires it; the twin does not model it).
        public static (string Name, string Pin, int Id) PartAtAssembly =>
            System.Array.Find(MapperConfig.M262SynthSensors,
                s => string.Equals(s.Name, "PartAtAssembly", System.StringComparison.OrdinalIgnoreCase));

        public static IReadOnlyList<HandoffSpec> All()
        {
            var pa = PartAtAssembly;
            var list = new List<HandoffSpec>
            {
                // Assembly->Disassembly is ALWAYS local: Assembly's tail publishes CMD state 7 with
                // src_id = AssemblyProcessId; Disassembly row 0 WAITs on (AssemblyProcessId, 7).
                new("AssemblyToDisassembly", "Assembly_Station", "Disassembly",
                    "Assembly_Station", MapperConfig.AssemblyProcessId, 7, HandoffTransport.LocalRing),
            };
            if (CrossPlcDischargeActive && pa.Name != null)
            {
                list.Insert(0, new("FeedToAssembly", "Feed_Station", "Assembly_Station",
                    pa.Name, pa.Id, 1, HandoffTransport.CrossDeviceSegment));
                list.Add(new("DisassemblyToDischarge", "Disassembly", "Discharge",
                    "Ejector", MapperConfig.DisassemblyProcessId, 0, HandoffTransport.CrossDeviceSegment));
            }
            else
            {
                // Decoupled: Assembly gates on the part arriving at the M580 BearingSensor.
                list.Insert(0, new("FeedToAssembly", "Feed_Station", "Assembly_Station",
                    "BearingSensor", -1, 1, HandoffTransport.LocalRing));
            }
            return list;
        }

        public static bool DischargeActive => CrossPlcDischargeActive;

        // WaitId < 0 means "resolve the id from the component map".
        public static HandoffSpec AssemblyStart =>
            All().First(h => h.Consumer == "Assembly_Station");
    }
}
