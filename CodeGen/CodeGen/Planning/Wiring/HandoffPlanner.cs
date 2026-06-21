using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;

namespace CodeGen.Translation
{
    /// <summary>
    /// Single source of truth for STATION-TO-STATION SEQUENCING (the cross-station handoffs).
    /// Replaces the scattered <c>AssemblyWaitForFeedPart</c> / <c>EnableRobotTaskTail</c> flags so the
    /// recipe generator, the ring wiring, and the HCF binder all read ONE place for "what does the
    /// next station wait on, and how does the signal get there".
    ///
    /// The SMC line is a strict pipeline:
    ///   Feed_Station (M262) ──PartAtAssembly──▶ Assembly_Station (M580)
    ///   Assembly_Station ──sentinel(17,7)──▶ Disassembly (M580)
    ///   Disassembly ──clamp-home then discharge──▶ Ejector (M262) ──▶ Robot/UR3e (M262)
    ///
    /// Three transports, each the SMALLEST proven mechanism for its hop:
    ///   • <see cref="HandoffTransport.LocalRing"/> — producer + consumer share one PLC's
    ///     stateRprtCmd ring, so the sentinel lands in the same state_table the consumer reads
    ///     (Assembly→Disassembly: both on M580).
    ///   • <see cref="HandoffTransport.CrossDeviceSegment"/> — producer + consumer are on DIFFERENT
    ///     PLCs, so a short M262 segment is spliced onto the M580 ring at the Disassembly seam via
    ///     two cross-device adapter hops EAE bridges (the rig-proven M580↔BX1 cover-ring mechanism).
    ///     This carries PartAtAssembly's report INTO M580 and the Ejector/Robot COMMANDS out to M262
    ///     WITHOUT touching the M580 bearing/shaft/clamp actuator ring.
    /// </summary>
    public static class HandoffPlanner
    {
        public enum HandoffTransport { LocalRing, CrossDeviceSegment }

        /// <summary>One station-to-station sequencing edge.</summary>
        public sealed record HandoffSpec(
            string Name,
            string Producer,          // station that completes the upstream work
            string Consumer,          // station that waits for it
            string SignalComponent,   // the FB whose state the consumer waits on
            int WaitId,               // state_table slot the WAIT reads
            int WaitState,            // value the WAIT holds for
            HandoffTransport Transport);

        /// <summary>
        /// Master switch for the M262↔M580 cross-device discharge + part-present handoffs. When ON:
        /// PartAtAssembly (M262) reports across to gate Assembly, and the Ejector + UR3e Robot tail
        /// (M262) are commanded by Disassembly over a cross-device segment. The M262 Feed ring stays
        /// M262-local and the M580 actuator ring is NOT stretched — only a short segment hangs off the
        /// Disassembly node. RIG-VERIFY: the M262↔M580 cross-device adapter transport (only M580↔BX1
        /// is rig-proven; generation is headless-verified). OFF = decoupled local rings (no discharge,
        /// Assembly gates on the local BearingSensor).
        /// </summary>
        public const bool CrossPlcDischargeActive = true;

        /// <summary>
        /// The BX1 cover P&amp;P ACTUATORS spliced onto the M580 ring (ring order), between the last
        /// M580 actuator (Clamp) and Assembly_Station; the cover place (Assembly) + remove (Disassembly)
        /// steps are folded into the M580 recipes. <c>TopCoverSenosr</c> stays OFF the ring (its id
        /// collides with PartAtAssembly on the M580 state_table). SINGLE SOURCE for the ring wiring
        /// (syslay + sysres) and the recipe's cover ids.
        /// </summary>
        public static IReadOnlyList<string> CoverDetour =>
            new[] { "CoverPNP_Hr", "CoverPNP_Vr", "CoverPnp_Gripper" };

        /// <summary>True when <paramref name="name"/> is a cover actuator on the M580 cover detour.</summary>
        public static bool IsCoverDetourActuator(string name) =>
            CoverDetour.Any(c => string.Equals(c, name, System.StringComparison.OrdinalIgnoreCase));

        // ── Component identities the handoffs reference (from the one component map) ──────────────

        /// <summary>The M262 part-present proximity sensor (DI08) the Feed station trips when it
        /// delivers the part to the assembly position. Id/pin come from MapperConfig.M262SynthSensors
        /// (the rig wires it; the twin does not model it).</summary>
        public static (string Name, string Pin, int Id) PartAtAssembly =>
            System.Array.Find(MapperConfig.M262SynthSensors,
                s => string.Equals(s.Name, "PartAtAssembly", System.StringComparison.OrdinalIgnoreCase));

        // ── The handoff table ────────────────────────────────────────────────────────────────────

        public static IReadOnlyList<HandoffSpec> All()
        {
            var pa = PartAtAssembly;
            var list = new List<HandoffSpec>
            {
                // Assembly→Disassembly is ALWAYS local (both engines on the M580 ring): Assembly's
                // tail publishes CMD state 7 carrying src_id = AssemblyProcessId; Disassembly row 0
                // WAITs on (AssemblyProcessId, 7). No cross-PLC link.
                new("AssemblyToDisassembly", "Assembly_Station", "Disassembly",
                    "Assembly_Station", MapperConfig.AssemblyProcessId, 7, HandoffTransport.LocalRing),
            };
            if (CrossPlcDischargeActive && pa.Name != null)
            {
                // Feed→Assembly: PartAtAssembly's report crosses M262→M580; Assembly row 0 WAITs on it.
                list.Insert(0, new("FeedToAssembly", "Feed_Station", "Assembly_Station",
                    pa.Name, pa.Id, 1, HandoffTransport.CrossDeviceSegment));
                // Disassembly→Discharge: after the clamp reaches home, the Ejector then the Robot run
                // on M262, commanded by Disassembly over the cross-device segment. (The recipe-level
                // ordering — clamp-home before ejector, ejector before robot — lives in RecipePlanner;
                // this entry records the cross-PLC seam for the ring wiring + HCF binder.)
                list.Add(new("DisassemblyToDischarge", "Disassembly", "Discharge",
                    "Ejector", MapperConfig.DisassemblyProcessId, 0, HandoffTransport.CrossDeviceSegment));
            }
            else
            {
                // Decoupled: Assembly gates on the part physically arriving at the M580 BearingSensor.
                list.Insert(0, new("FeedToAssembly", "Feed_Station", "Assembly_Station",
                    "BearingSensor", -1, 1, HandoffTransport.LocalRing));
            }
            return list;
        }

        // ── Convenience queries the generators consume ───────────────────────────────────────────

        /// <summary>True when the Ejector + UR3e Robot discharge tail is active (was EnableRobotTaskTail).</summary>
        public static bool DischargeActive => CrossPlcDischargeActive;

        /// <summary>The signal Assembly_Station's recipe row 0 waits on: the part-present component
        /// name + the state value. WaitId &lt; 0 means "resolve the id from the component map".</summary>
        public static HandoffSpec AssemblyStart =>
            All().First(h => h.Consumer == "Assembly_Station");
    }
}
