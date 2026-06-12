using System;
using System.IO;
using System.Text.Json;

namespace CodeGen.Configuration
{
    public class MapperConfig
    {
        private const string ConfigFileName = "mapper_config.json";

        // Interim flag (2026-05-28): route every Seven_State actuator
        // (Bearing_PnP swivel) to Five_State_Actuator_CAT instead.
        // WHY: Process1_Generic commands actuators only through the stateRprtCmd
        // ring, and Seven_State_Actuator_CAT.fbt declares NO ring port — so the
        // recipe can neither command Bearing_PnP nor read its state, and the
        // Assembly sequence stalls forever at the bearing step (command goes
        // nowhere, the WAIT never satisfies). Five_State sits on the ring, so the
        // recipe drives it (work/home) and the whole clamp->bearing->shaft
        // sequence cycles. Trade-off: no true two-position pick/place swivel
        // until task #69 gives Seven_State its own stateRprtCmd adapter — flip
        // this to false in that same commit. Gated at all four Seven_State
        // detection sites (ResolveActuatorFBType, SevenStateActuators,
        // IsFiveStateCommandable, IsSevenStateCommandable) so the FB type and the
        // recipe command vocabulary stay in lock-step.
        // static readonly (not const) on purpose: the const form makes the
        // compiler treat the real-Seven_State branches gated on this flag as dead
        // code (CS0162). They are not dead — they are the path #69 re-enables when
        // it flips this to false. static readonly keeps both branches live.
        // 2026-05-29 (task #69): FLIPPED to false. Seven_State_Actuator_CAT.fbt
        // now carries a stateRprtCmd ring node (an updateComponentState FB wired
        // exactly like Five_State's StateHandling) so Process1_Generic commands
        // and reads Bearing_PnP through the report ring, and ResourceWireEmitter
        // now rings it (NoRingAdapterTypes is empty; it stays off the station
        // chain via NoStationAdapterTypes). Bearing_PnP therefore resolves to
        // Seven_State_Actuator_CAT, not the Five_State stub.
        // 2026-05-29 (re-flip to TRUE): my Seven_State_Actuator_CAT surgery is
        // unverified and on the rig it leaves Bearing_PnP stuck at INIT (the ring
        // command is never processed -> "nothing triggered"). Reverted to the
        // proven Five_State stub so the recipe drives Bearing_PnP and it is
        // forceable for testing. Flip back to FALSE when Jyotsna's real
        // Seven_State_Actuator_CAT (with interlock) lands.
        // 2026-05-30: flipped to FALSE for the "Seven_State end-to-end in Test Simulator"
        // session. SimulatorPostProcessor.InjectSimSwivelForce now publishes atwork1/
        // atwork2 in sim by mirroring the actuator's own current_state{1,2}_to_plc
        // outputs (sensors close instantly when coil energises). The 3 deferred rig
        // fixes (committed surgical CAT in the .cat.zip + BuildStation2Wiring skips
        // Seven from stationChain + BuildMinimalActuatorParameters parameterises
        // process_state_name = lowercased name) all landed earlier in this session.
        // Re-flip to TRUE only if the sim demo regresses and you can't afford to debug
        // the Seven_State path during that demo window.
        public static readonly bool StubSevenStateActuatorsAsFiveState = false;

        // RIG vs SIM behaviour for the Seven_State swivel HOME-FIRST preamble.
        // Set from cfg.SimulatorFullSystem at the start of GenerateStation1TestSyslay
        // (fresh every generation — no session carry-over). The recipe generator reads
        // it to choose the home-preamble WAIT target:
        //   SIM  (true):  the swivel boots at AtHomeInit (current_state=0) and a home
        //                 command from there is a no-op, so the home WAIT must target 0
        //                 (else it stalls waiting for a move that never happens).
        //   RIG (false): the swivel boots PARKED AT A WORK position (atWork1=TRUE) and
        //                 the process engine inits LAST, so its state_table reads the
        //                 blank default 0 — which a WAIT-for-0 false-matches, so the
        //                 engine thinks "already home" and SKIPS the physical homing
        //                 (observed: swivel goes atWork1 -> atWork2, atHome never TRUE).
        //                 The rig home WAIT must target AtHome=6 (== atHome sensor TRUE),
        //                 a value the blank 0 cannot match, so the engine truly waits for
        //                 the swivel to physically reach home before commanding Pick.
        public static bool SimulatorRecipeMode = false;

        // RECIPE RUN-ONCE (2026-06-03). When true (default), each process recipe
        // runs its sequence ONE time and then PARKS on the END row instead of
        // looping back to step 0. The Process engine's END ECState runs
        // EndSequence (CurrentStep := Recipe[CurrentStep].NextStep), so the END
        // row's NextStep decides what happens after the recipe finishes: pointing
        // it at step 0 RESTARTS the whole cycle (and with the home-first preamble
        // now at step 0, that means Home->Pick->Place->Home->Home->Pick... forever
        // -- the observed swivel "bounce between atWork1 and atWork2"); pointing it
        // at ITSELF parks the engine (no further commands, actuators hold their
        // last/home position). Set false to restore continuous looping for a
        // production line that should keep cycling. Applied in
        // ProcessRecipeArrayGenerator.Generate after the preamble shifts indices.
        public static bool RecipeRunOnce = true;

        // AUTO-RETRACT SCOPE (2026-06-04): the recipe generator's auto-retract
        // safety net (it inserts a return-home for an actuator the twin advances but
        // never retracts) runs ONLY for the processes listed here. It exists for the
        // Feed_Station twin, which advances the Checker but never retracts it (leaving
        // it energized at-work — a documented rig-recovery incident). Every OTHER
        // process's recipe is generated VERBATIM from its Control.xml state-transition
        // chain, with NO inserted steps — so Assembly_Station holds the Clamp exactly
        // as the twin sequences it (engage at Clamping_Part, no release in the chain)
        // instead of having a clamp release injected. Add a process name here only if
        // its twin is known to omit a retract it genuinely needs.
        public static readonly string[] AutoRetractProcesses = new[] { "Feed_Station" };

        // SEVEN_STATE HOME PREAMBLE (2026-06-04): OFF for the rig-facing Assembly
        // recipe. Bearing_PnP is a centre-home swivel that can boot parked at Pick
        // or Place after a manual jog / interrupted cycle. The generated process
        // must therefore command the swivel home before issuing Pick, so the
        // bearing transfer always starts from a known rest position.
        // When true, ProcessRecipeArrayGenerator prepends "CMD swivel Home -> WAIT
        // AtHomeInit=0" before the cycle. It is an operational safe-start step added
        // by the mapper, not a Control.xml process state. The rest of the Assembly
        // recipe is still derived from the Control.xml transition chain.
        // Keep FALSE on the rig: if Bearing_PnP is already at home, CMD Home
        // is a no-op and no fresh state report is produced, leaving the
        // Process engine stuck at step 0 before the first real Pick command.
        public static bool EnableSevenStateHomePreamble = false;

        // CROSS-PLC COVER RING (2026-06-08, task #69): extend the single stateRprtCmd
        // report/command ring across the M580↔BX1 device boundary so the M580
        // Assembly_Station engine can COMMAND the BX1 Cover PnP actuators
        // (CoverPNP_Hr / CoverPNP_Vr / CoverPnp_Gripper) and READ their state reports
        // into its own state_table. Today the M580 ring and the BX1 ring are two
        // SEPARATE closed loops, so cover commands never leave the M580 and cover
        // reports never reach the engine — the covers carry the correct GLOBAL ids
        // (task #25) but "only resolve once the M580↔BX1 bridge is emitted"
        // (SystemLayoutInjector ~L1164). When TRUE this flag makes ONE ring that
        // spans both PLCs:
        //   …Clamp → [cross to BX1] TopCoverSenosr → CoverPNP_Hr → CoverPNP_Vr →
        //    CoverPnp_Gripper → [cross to M580] Assembly_Station → BearingSensor…
        // The two cross-device hops live ONLY on the syslay (the application); EAE
        // bridges cross-resource connections into per-resource transport at deploy
        // (the same mechanism MqttBridgeEmitter relies on for cross-PLC event/data).
        // The per-PLC sysres rings are OPENED at the boundary so the adapter sockets
        // are not double-driven. This ALSO enables the cover CMD/WAIT rows in the
        // Assembly recipe — gated together because commanding a cover whose reports
        // cannot reach the engine would stall the WAIT forever.
        //
        // CORNERSTONE RISK: EAE bridging a cross-device ADAPTER connection (stateRprtCmd
        // carries a CNF event + CNFD/Component_State_Msg) is unproven — only cross-device
        // EVENT/DATA bridging is proven (MqttBridge). If EAE rejects it at Build, OR the
        // M580 ring breaks (bearing/shaft stop cycling), set this back to FALSE and
        // rebuild — that restores the two separate closed rings (today's working rig)
        // exactly, and we pivot to explicit PUBLISH/SUBSCRIBE SIFB transport instead.
        // 2026-06-09 REVERTED TO FALSE: with this TRUE the M580 Assembly_Station ring
        // closed THROUGH the BX1 covers cross-PLC (Assembly_Station.stateRptCmdAdptr_in
        // <- BX1.CoverPnp_Gripper.stateRprtCmd_out). EAE does NOT bridge that cross-
        // device ADAPTER connection at runtime (BX1 isn't even running), so the M580
        // ring was BROKEN at the boundary: the engine's own state reports never
        // circulated back, state_table never updated, and the Assembly recipe stalled
        // -> "M580 runs nothing". This is the cornerstone failure flagged when the ring
        // extension was built. FALSE restores the SEPARATE per-PLC rings (M580 closes
        // Clamp->Assembly_Station->BearingSensor LOCALLY) and drops the cover CMD/WAIT
        // rows, so M580 bearing/shaft runs STANDALONE again.
        // 2026-06-10 STAGE 4 — RE-ENABLED (TRUE). This is now the SIMPLE cross-PLC
        // mechanism the user asked for: NO custom FB. The BX1 cover stateRprtCmd ring is
        // spliced into the M580 Assembly ring via two cross-DEVICE adapter hops drawn in
        // the syslay (Clamp.out→TopCoverSenosr.in, CoverPnp_Gripper.out→Assembly.in); EAE
        // compiles those into its CrossComm transport at Build (the SAME mechanism the SE
        // reference uses on this exact M580↔BX1 pair — proven to bridge cross-device
        // adapters). The reason it failed on 06-08 was simply that BX1 WAS NOT RUNNING;
        // now BX1 runs (covers cycled physically on the rig today), so both PLCs deployed
        // = the ring circulates. Gates: the adapter hops (BuildStation2Wiring /
        // BuildBx1Wiring crossPlcCoverRing), the sysres boundary-open
        // (ResourceWireEmitter.CrossPlcCoverRingActive) AND the Assembly recipe cover
        // block all key off this flag. Pair with DeployBx1CoverEngine=FALSE (Cover_Station
        // scaffold retires + is swept). FALSE reverts to the proven two-engine state in
        // one rebuild. (The RingCrossGate-FB variant — FoldCoversIntoAssembly — is shelved:
        // the user wants no extra FBs.) OPERATING RULE: deploy + login BOTH M580 and BX1.
        // 2026-06-10 RE-ENABLED (TRUE) after a HEADLESS REPRO disproved the syslay-abort
        // theory: running GenerateStation1TestSyslay with these exact Stage-4 flags on the
        // real Demonstrator produces a FULL syslay — 31 FBs, NO Cover_Station, all M262
        // (Feeder/Checker/Transfer/Ejector/Feed_Station) + M580 (Assembly/Bearing/Shaft/Clamp)
        // + BX1 covers present, BOTH cross-hops drawn, Assembly recipe commanding all 9
        // actuators incl coverpnp_vr/hr/gripper. So the earlier empty syslay was NOT this code
        // — it was EAE's "Not Used Instances → Delete" (deletes the app instances) or a partial
        // run. Generation is correct. (Deploy still needs a clean Build in EAE so the
        // HwConfiguration recompiles BMXBUS/EIPSCANNER2 after a wipe.)
        public static bool ExtendStateRingAcrossBx1 = true;

        /// <summary>
        /// Experimental cross-PLC cover fold-in. Disabled for the rig: the working
        /// mechanism is the BX1-local Cover_Station engine and the M580 Assembly ring
        /// must stay local so Bearing_PnP/shaft/clamp continue to run independently.
        /// If this is ever re-enabled, it must be proven without changing the M580 ring.
        /// Former Stage 4 intent was to fold the BX1 covers into the M580 Assembly_Station —
        /// the twin's Assembly process natively defines the 8 cover steps
        /// (Cover_PnP_GoDown_Pick … Cover_PnP_returned, before the clamp opens), so the
        /// covers are commanded by Assembly's recipe like every other actuator. Gates,
        /// as ONE unit: (a) a RingCrossGate node spliced into EACH locally-closed
        /// stateRprtCmd ring (M580 Mode 0, BX1 Mode 1) + the cross-device TX→RX
        /// event/data connections in the syslay, which EAE compiles into CrossComm
        /// (nxtv3 UDP — the SE reference's proven mechanism on this exact PLC pair;
        /// NO unified ring, NO SIFBs); (b) the 8-step cover CMD/WAIT block in the
        /// Assembly recipe. Both rings stay locally closed, so M580 survives BX1
        /// being offline (Assembly parks at the first cover WAIT instead of dying —
        /// the exact failure mode that killed ExtendStateRingAcrossBx1).
        /// Pair with DeployBx1CoverEngine=false (the Cover_Station scaffold retires;
        /// its deployed instance is swept). FALSE reverts to the proven two-engine
        /// state in one rebuild.
        /// 2026-06-10 SHELVED (stays FALSE): the user wants NO extra FBs, so the cover
        /// fold uses ExtendStateRingAcrossBx1's plain cross-device adapter ring instead of
        /// the RingCrossGate FB. This flag + its RingCrossGate code path stay dormant.
        /// </summary>
        public static bool FoldCoversIntoAssembly = false; // shelved (no extra FBs) — see ExtendStateRingAcrossBx1

        /// <summary>
        /// Master gate for the LOCAL BX1 cover command engine (Cover_Station). When TRUE,
        /// a Process1_Generic engine named Cover_Station is instantiated on the BX1 SubApp +
        /// sysres, spliced into the BX1 cover stateRprtCmd ring (the sysres ring auto-splices
        /// any Process FB it finds), and carries the cover pick/place recipe — so BX1 cycles
        /// its own covers with NO M580 dependency and NO cross-PLC ring. Independent of (and
        /// mutually exclusive with) ExtendStateRingAcrossBx1.
        /// 2026-06-10 FALSE — STAGE 4: the Cover_Station scaffold retires (it proved the
        /// BX1 cover chain end-to-end on the rig but is NOT in the twin). The covers are now
        /// commanded by Assembly_Station over the cross-PLC adapter ring
        /// (ExtendStateRingAcrossBx1=true). When FALSE, the already-deployed Cover_Station
        /// instance is SWEPT from the BX1 sysres before wiring (Station2WireEmitter) — a
        /// leftover would be re-discovered by the ring type-scan and fight Assembly's cover
        /// commands. Flip back TRUE (with ExtendStateRingAcrossBx1=false) to restore the
        /// proven BX1-local cover cycle exactly.
        /// 2026-06-10 FALSE — STAGE 4 re-enabled: Cover_Station removed from BX1 (verified by
        /// headless repro: no Cover_Station FB in the generated syslay), Assembly_Station now
        /// commands the covers over the cross-PLC ring. The sweep clears the already-deployed
        /// Cover_Station from the BX1 sysres on the next Test Runtime.
        /// </summary>
        public static bool DeployBx1CoverEngine = false;

        /// <summary>
        /// STAGE 5 (2026-06-10): unpark Disassembly_Station. Default FALSE = today's proven
        /// state (Disassembly is a parked single-END Process, Assembly opens the clamp at its
        /// tail). When TRUE: (a) Disassembly gets a real recipe (ApplyDisassemblyRuntimeRecipe
        /// — reverse of Assembly: covers off → shaft out → bearing out → UNCLAMP at the end,
        /// the twin order, M580+BX1 only — Ejector/Robot are M262 and deferred to Stage 5b);
        /// (b) Assembly's clamp-open tail is REMOVED (the twin keeps the clamp closed through
        /// assembly AND disassembly; it opens only at Disassembly's Unclamping step) and
        /// Assembly instead publishes a handshake sentinel (CMD state=7) so Disassembly can
        /// WAIT on (Assembly process_id, 7); (c) Disassembly is unparked in the M580 wiring
        /// (ResourceWireEmitter.BypassParkedM580Disassembly + the syslay init/station/ring).
        /// The clamp-open MUST move with the unpark — gating both on this one flag keeps it
        /// atomic (the clamp can never be left to never-open). FALSE reverts in one rebuild.
        /// 2026-06-10 TRUE — Stage 5a enabled. Headless-verified on the real Demonstrator:
        /// Disassembly recipe = 46 rows (covers→shaft→bearing→unclamp + WAIT(17,7) handshake),
        /// Assembly tail = assembly_handshake_done sentinel (no clamp-open), and the M580 sysres
        /// threads Disassembly INTO the ring (INIT-wired, ring_in/out, Assembly→Disassembly edge).
        /// </summary>
        public static bool UnparkDisassembly = true;

        /// <summary>
        /// Process-FB <c>process_id</c> slots — the SINGLE SOURCE OF TRUTH shared by
        /// SystemLayoutInjector (which stamps each Process FB's <c>process_id</c> parameter)
        /// and ProcessRecipeArrayGenerator (whose Disassembly handshake WAITs on Assembly's
        /// id). They are fixed constants BY DESIGN, not registry-resolved: every Process FB's
        /// id must sit ABOVE the component id space so it never collides with a sensor/actuator
        /// id in the shared <c>state_table[20]</c> (ProcessRecipeArrayGenerator.ValidateProcessIdInvariant
        /// throws on a collision). The Disassembly↔Assembly hand-off rides the ring as a
        /// sentinel CMD whose message carries <c>src_id = </c><see cref="AssemblyProcessId"/>;
        /// Disassembly's row-0 WAIT(<see cref="AssemblyProcessId"/>, 7) holds on exactly that.
        /// Centralising the literal here is what makes the handshake non-brittle — change the
        /// slot in ONE place and both the stamp and the WAIT move together.
        /// </summary>
        public const int FeedStationProcessId    = 10;
        public const int AssemblyProcessId       = 17;
        public const int DisassemblyProcessId    = 18;
        public const int CoverStationProcessId   = 19;

        /// <summary>
        /// state_table slot the UR3e (<c>Robot_Task_CAT</c>) stamps on its ring reports (STAGE 5b).
        /// The robot lives on M262 but its reports cross to the <b>M580</b> state_table (where the
        /// Disassembly engine waits on it), so its id must be free <i>there</i>. The registry's
        /// positional id (17) collides on M580 with the Assembly station
        /// (<c>Station2_HMI</c> / <see cref="AssemblyProcessId"/>=17). In the 20-slot table, 19 is
        /// the ONLY slot free on BOTH M262 and M580: 0..18 are taken on one PLC or the other, and 19
        /// (<see cref="CoverStationProcessId"/>) is empty because Cover_Station was folded into
        /// Assembly (Stage 4, <see cref="DeployBx1CoverEngine"/>=false). Used in exactly two places —
        /// the CAT's <c>actuator_id</c> param (SystemLayoutInjector) and the Disassembly recipe's
        /// WAIT robot rows (ProcessRecipeArrayGenerator) — so the slot the robot writes == the slot
        /// the WAIT reads. WARNING: do not run <see cref="EnableRobotTaskTail"/> and
        /// <see cref="DeployBx1CoverEngine"/> together — both would claim slot 19.
        /// </summary>
        public const int RobotActuatorId         = 19;

        /// <summary>
        /// FEED -> ASSEMBLY cross-process handshake (default OFF). When true, Feed_Station's
        /// recipe appends a sentinel <c>CMD feed_handshake_done=7</c> just before END (the engine
        /// then publishes a ring message <c>{src_id = FeedStationProcessId(10), state = 7}</c> on
        /// Feed completion), and Assembly_Station's recipe prepends a row-0
        /// <c>WAIT(FeedStationProcessId, 7)</c> -- the exact mirror of the proven
        /// Assembly->Disassembly handshake (<c>assembly_handshake_done=7</c> + Disassembly's row-0
        /// WAIT(<see cref="AssemblyProcessId"/>, 7)).
        ///
        /// THE DECISIVE DIFFERENCE -- this handshake is CROSS-PLC, the proven one is not.
        /// Assembly->Disassembly works because BOTH engines live on the SAME M580 ring, so
        /// Assembly's {17,7} lands in the M580 state_table Disassembly already reads (local hop, no
        /// CrossComm). Feed_Station lives on M262: its {10,7} sits in the M262 state_table, NOT
        /// M580's. For Assembly's WAIT(10,7) to ever clear, that sentinel must cross M262 -> M580
        /// and land in M580 state_table[10]. That transport is UNPROVEN (only the M580<->BX1
        /// cross-device adapter bridge is rig-proven; the M262<->M580 robot-tail hops are not), and
        /// there is no free ring port to tap the Feed_Station sentinel onto the M580 ring additively
        /// -- bridging it needs either a new gate FB (RingCrossGate was deleted) or merging the two
        /// rings (which makes the proven M262-local Feed ring depend on M580). BOTH conflict with
        /// "no new FB / don't disturb the proven rings".
        ///
        /// So this flag is OFF and MUST stay off until the M262 -> M580 transport is proven (the
        /// robot tail, <see cref="EnableRobotTaskTail"/>, rides the same path -- prove it THERE
        /// first: deploy all three PLCs and see the ejector/robot fire). Turning this ON without the
        /// bridge DEADLOCKS Assembly at row 0 -- ProcessRecipeArrayGenerator already records the
        /// identical stall (a Feed precondition WAIT that could not resolve cross-PLC). The recipe
        /// rows are ready.
        ///
        /// 2026-06-12 — WIRED + ENABLED (user request: "inform Assembly through the ring, no new FB").
        /// The bridge is built as a RING MERGE (no new FB): BuildStation2Wiring + BuildFeedStationWiring
        /// splice the M262 Feed ring and the M580 ring into ONE ring via two cross-device hops
        /// (Disassembly.out->PartInHopper.in, Feed_Station.out->BearingSensor.in), and ResourceWire
        /// Emitter opens both sysres close-backs at the boundary so EAE bridges them (the same mechanism
        /// as the proven M580<->BX1 cover ring). The Feed sentinel {10,7} then circulates into the M580
        /// state_table; Assembly's row-0 WAIT(10,7) holds until Feed finishes.
        /// CONSEQUENCE (the trade-off of "through ring, no FB"): the three PLCs now share ONE ring, so
        /// all three MUST be deployed together — if any is down the ring is open and ALL stall. It also
        /// rides the M262<->M580 cross-device adapter bridge, which is rig-UNPROVEN (only M580<->BX1 is
        /// confirmed). If EAE does not bridge it, the M580 ring will not close and Assembly/Disassembly
        /// stall too. Revert instantly: set false (one rebuild) => decoupled local rings (Stage 5a).
        /// </summary>
        // 2026-06-12 — REVERTED to false. The ring-merge made the cross-PLC stateRprtCmd ring
        // span M262+M580+BX1 (the fragile thing the whole redesign set out to avoid), AND it surfaced
        // a real incoherence: the cross-hops + sentinel + WAIT(10,7) emit into the top-level SYSLAY,
        // but the deployed per-device SYSRES did not match (stale recipes, no sentinel, Assembly still
        // starting at clamp=1, and a Robot_Task_CAT left over from a prior EnableRobotTaskTail=true
        // build). EAE then bridges the syslay cross-hops at deploy and splices the M580 ring to M262,
        // while the sysres has no handshake -> nothing triggers. OFF restores ONE coherent source of
        // truth: local, closed rings per PLC (M580/BX1 Assembly+Disassembly+covers; M262 Feed), no
        // cross-PLC live ring splice. Feed->Assembly auto-sequencing remains UNsolved without the one
        // minimal bridge FB (the only EAE-native cross-PLC transport that does NOT stretch the ring).
        public static bool FeedAssemblyHandshake = false;

        /// <summary>
        /// MATERIAL-GATE Feed -> Assembly sequencing (chosen 2026-06-12; default ON). Assembly's
        /// recipe row 0 holds on <c>WAIT(BearingSensor, 1)</c> — the bearing the Feed station
        /// physically delivers trips the sensor, then Assembly runs. BearingSensor is a
        /// <c>Sensor_Bool_CAT</c> on the M580 ring, so this is M580-LOCAL: no cross-PLC link, no new
        /// FB, no stretched stateRprtCmd ring (unlike the removed <see cref="FeedAssemblyHandshake"/>).
        /// It is the natural production sequencing — the next station starts when the part arrives.
        /// State 1 = BearingSensor On (a Sensor_Bool_CAT publishes the Control.xml State_Number
        /// verbatim). CAVEAT: relies on the sensor going off->on when Feed delivers (a change the CAT
        /// publishes into state_table). A prior attempt "deadlocked" only in the SIM, where no
        /// physical part ever arrived so the sensor never changed; on the rig the delivered bearing
        /// trips it. Set false if the rig sensor doesn't trip — then Assembly runs immediately (no
        /// handoff). The wait state (1) is rig-tunable if the sensor reports a different value.
        /// </summary>
        public static bool AssemblyWaitForFeedPart = true;

        /// <summary>
        /// Real M262 rig proximity sensors the digital twin does NOT model, but the physical SMC rig
        /// wires to fixed DI channels (we copy the physical MAPPING from SMC_Rig_Expo_withClamp only,
        /// never its ids/names). ONLY the two the flow needs:
        ///  • <b>PartAtAssembly</b> (DI08) — the Feed→Assembly handoff: Assembly_Station should start
        ///    only after the part is reported at the assembly position.
        ///  • <b>PartAtExit</b> (DI09) — the robot gate: the robot task should start only after the
        ///    ejected part is at the exit.
        /// (PartAtChecker is NOT synthesized — the twin's Checker just goes down/up; nothing in
        /// Control.xml references a part-at-checker sensor.)
        /// <para>
        /// Synthesized as <c>Sensor_Bool_CAT</c> on M262 with project-generated FB ids (FBIdGenerator)
        /// and these explicit state ids. ID NOTE (per the cross-PLC collision review): the 20-slot
        /// state_table is FULL — every id 0..19 is claimed somewhere, and the only ids free on the
        /// M580 table (the would-be consumer) are {0,4,5,6} = the M262 Feed ids. So a globally-unique
        /// id is IMPOSSIBLE at the current table size. While these sensors are EXPOSED-ONLY (off every
        /// ring — current state), the id never indexes any state_table, so it is an inert label; we
        /// park it at 20/21 (ABOVE the 20-slot table) so it cannot be mistaken for, or collide with,
        /// any ring participant on any PLC. WHEN the cross-PLC handoff is wired (Assembly waits on
        /// PartAtAssembly, robot on PartAtExit), re-id to the M580-free in-table slots 5/6 and splice
        /// them onto the M262→M580 cross ring ONLY (never the M262 Feed ring) — then 5/6 are free on
        /// M580 while Checker/Transfer keep 5/6 on M262's separate table. Bound in
        /// <c>HcfPatchService</c>; gated on <see cref="EnableRobotTaskTail"/>.
        /// </para>
        /// </summary>
        // 2026-06-12: EMPTIED. The ids 20/21 are OUT OF BOUNDS for the 20-slot state_table
        // [0..19]. Although these sensors are meant to be exposed-only (off the ring), in
        // practice ResourceWireEmitter wires every Sensor_Bool_CAT on the sysres INTO the Feed
        // report ring, so they landed on the ring with ids 20/21 and the M262 resource faulted
        // (ERR_RT_VALUERANGE → ErrorHalt → all Feed I/O dead). That fault is a SOFTWARE bug,
        // independent of the (separate, hardware) feeder/checker sensor-wiring issue, and it
        // re-faults M262 the moment these are re-enabled. Keep EMPTY until the cross-PLC handoff
        // is wired with in-table ids (5/6) on the M262→M580 cross ring ONLY (see the note above).
        // The robot/ejector tail (EnableRobotTaskTail) does NOT need these — they were a separate,
        // deferred part-handoff feature; the tail runs fine without them.
        public static readonly (string Name, string Pin, int Id)[] M262SynthSensors =
            System.Array.Empty<(string Name, string Pin, int Id)>();

        /// <summary>
        /// STAGE 5b — the UR3e robot + M262 ejector tail for Disassembly. ENABLED 2026-06-11 (user
        /// request) for the rig test. When TRUE the FULL bundle activates: (1) ONLY the real UR3e
        /// (<see cref="CodeGen.Mapping.TemplateMap.IsRobotTaskArm"/> — narrow; every gripper stays
        /// Five_State) resolves to <c>Robot_Task_CAT</c>; (2) <c>Robot_Task_CAT</c> +
        /// <c>Robot_Task_Core</c> templates are deployed; (3) exactly one Robot FB is emitted on M262;
        /// (4) the stateRprtCmd ring extends to the M262 ejector+robot (boundary-open: they leave the
        /// Feed ring; a local <c>Ejector.stateRprtCmd_out→Robot.stateRprtCmd_in</c> segment + the two
        /// M580↔M262 cross-hops carry the tail); (5) HCF <c>DO04=RobotCommands_StartTask</c>,
        /// <c>DI10=RobotStatus_Task_Complete</c>; (6) the Disassembly recipe appends
        /// unclamp(clamp=3)→ejector→robot→END. FLAG-OFF is byte-identical Stage 5a.
        /// IMPORTANT: with this ON the M580 Disassembly ring opens toward M262, so ALL THREE PLCs
        /// (M580+M262+BX1) MUST be deployed together — M580+BX1 alone leaves the ring open and stalls.
        /// NOT runtime-safe until EAE Clean/Build/Deploy confirms the CAT compiles + live M580↔M262
        /// adapter bridging (rig-only unknowns). Revert: set false (one rebuild = Stage 5a).
        ///
        /// 2026-06-12 — SET FALSE (architecture decision). The robot-tail design stretched the live
        /// stateRprtCmd ring THROUGH M262 (Disassembly.out→Ejector→Robot→m580 head), so the M580
        /// ring could not close unless M262 was up — M580+BX1 alone stalled. Investigation confirmed
        /// EAE's ONLY cross-PLC transport is the cross-device adapter (the reference's
        /// ReliableCrossComm; no PUBLISH/SUBSCRIBE or cross-PLC symlinks), the CaS/station adapter
        /// carries mode/cycle/fault (not a process sentinel), and the Process engines have NO spare
        /// ports — so a cross-PLC (process_id,state) handoff for the tail needs one minimal bridge FB.
        /// The user chose the no-new-FB path: keep M580/BX1 closed + local (this flag OFF = Stage 5a),
        /// M262 Feed closed + local, and DEFER the M262 ejector/robot tail + auto-sequencing until a
        /// bridge FB is approved. Re-enabling this flag re-introduces the M262-coupling and must NOT
        /// be done without that bridge. (FeedAssemblyHandshake stays OFF for the same reason.)
        /// </summary>
        public static bool EnableRobotTaskTail = false;

        /// <summary>
        /// When TRUE, Cover_Station runs a MINIMAL proof-of-life recipe (CoverPNP_Vr
        /// work→home only — one actuator end-to-end, no cross-component waits that could
        /// stall on a missing sensor). Flip FALSE for the full 8-step cover pick/place
        /// sequence (vr down → grip → up → hr advance → vr down → release → up → hr return).
        /// 2026-06-10: FALSE — the minimal Vr cycle ran end-to-end on the rig (valve moved,
        /// physical atwork closed the WAIT, recipe completed), so the full sequence is live.
        /// CoverPnp_Gripper runs timer-acknowledged (no home sensor bit exists on the TM3BC
        /// input assembly — see the BuildActuatorParameters override).
        /// </summary>
        public static bool Bx1CoverMinimalCycle = false;

        // TEST ISOLATION (2026-05-29, TEMPORARY): restrict ONE process's recipe to a
        // subset of actuators so a single mechanism can be exercised on the rig
        // without the others moving. RecipeTestProcessName = the process to restrict
        // (empty string = apply to every process); RecipeTestActuatorAllowlist = the
        // actuator names (lower-case, matching the recipe CmdTargetName) that may
        // still be commanded. Every OTHER actuator's CMD/WAIT step in that process is
        // dropped, so the actuator is PARKED — never commanded, stays where it is.
        // EMPTY allowlist = no restriction (normal full recipe).
        //
        // 2026-05-29 update: cleared to restore the FULL Assembly_Station cycle for the
        // end-to-end simulator demo. The bench rig is unsafe (clamp damaged + swivel
        // collision risk) so testing moves to the "Test Simulator" button (Cfg
        // .SimulatorFullSystem=true): all 3 PLCs collapse into one SIM resource, every
        // Five_State_Actuator_CAT is forced no-sensor so the internal No_Sensor_Handler
        // timer self-advances the ECC (toWorkTime → atwork, toHomeTime → athome), and
        // the single ring resolves the cross-PLC/cross-process Wait1Id refs Assembly
        // makes to BX1 cover components and Feed_Station handoffs. Bearing_PnP stays
        // on the Five_State stub (StubSevenStateActuatorsAsFiveState above) — Iss
        // SevenStateCommandable returns false under the stub, so the recipe commands
        // bearing with work/home like any other Five_State actuator, and it self-
        // advances on the timer instead of waiting for a 3-position swivel sensor the
        // simulator has no model for. To return to the bearing-only bench test, repopu
        // late the allowlist with { "bearing_pnp", "bearing_gripper" }.
        public static readonly string RecipeTestProcessName = "Assembly_Station";
        // 2026-06-02 (TEMPORARY, bench): bearing-only isolation test on M580. Restricts
        // the Assembly_Station recipe to Bearing_PnP + Bearing_Gripper so they actually
        // RUN on an M580-only deploy — every other actuator's CMD/WAIT (Shaft_*, Clamp,
        // CoverPNP_*) AND the cross-PLC Transfer wait are dropped, so the recipe no longer
        // stalls on the Feed/Transfer (M262) prerequisites that never complete when only
        // M580 runs. Clear back to `new string[0]` to restore the full Assembly cycle.
        // 2026-06-03: shaft actuators added back (IO restored from backup). M580-only
        // set: bearing + shaft, NO BX1 covers (those would stall waiting on the
        // unconnected BX1). Clear to `new string[0]` for the full cycle incl. covers.
        // 2026-06-04: clamp added. Assembly_Station's Clamping_Part state waits on
        // Clamp/Clamped (Control.xml C-f021417c… line 2748), so with clamp in the
        // allowlist the data-driven recipe keeps the clamp CMD/WAIT in its native
        // chain position (after the bearing+shaft assembly) instead of dropping it.
        // Clamp is M580 with real sensors (DI06=ClampAtWork, DI07=ClampAtHome).
        public static readonly string[] RecipeTestActuatorAllowlist = Array.Empty<string>();

        // TEST ISOLATION (2026-06-04, TEMPORARY, bench): drop the centre-home swivel's
        // (Bearing_PnP) cross-component interlock rules so its turn-to-Place (AtWork1 2
        // -> AtWork2 4) is never blocked. Alex traced the swivel sticking at atWork1 to
        // the shaft-sensor interlock rule (RuleSourceID=shaft_hr, RuleBlockedState=AtWork):
        // the swivel refuses to turn-to-Place while shaft_hr reads AtWork. In the isolated
        // bearing+shaft+clamp bench test the shaft is home during the swivel's Place, but
        // the rule still fires, so the swivel never reaches atWork2 and never releases the
        // bearing. RuleCount=0 removes the block for the test. Set false to restore the
        // real Control.xml interlock when the full collision-aware system runs (it also
        // re-arms the SystemLayoutInjector safety guard that refuses an inert RuleCount=0
        // when Control.xml defines in-scope swivel interlocks).
        public static bool DropSwivelInterlockForTest = true;

        public string SystemXmlPath { get; set; } = string.Empty;
        public string MappingRulesPath { get; set; } = string.Empty;
        public string TemplateLibraryPath { get; set; } = string.Empty;
        public string ActuatorTemplatePath { get; set; } = string.Empty;
        public string SensorTemplatePath { get; set; } = string.Empty;
        public string ProcessCATTemplatePath { get; set; } = string.Empty;
        public string RobotTemplatePath { get; set; } = string.Empty;
        public string RobotBasicTemplatePath { get; set; } = string.Empty;
        public string SyslayPath { get; set; } = string.Empty;
        public string SysresPath { get; set; } = string.Empty;
        public string SyslayPath2 { get; set; } = string.Empty;
        public string SysresPath2 { get; set; } = string.Empty;

        /// <summary>
        /// Simulator-target syslay/sysres paths, mirroring the Demonstrator
        /// folder tree under C:\DemonstratorSim. Used only by the
        /// "Test Station 1 Pusher-Simulator" button — the full unchanged
        /// generation pipeline runs against these instead of SyslayPath2/
        /// SysresPath2, then the sim .sysres Resource Type is flipped
        /// EMB_RES_ECO → SIM_RES for EAE's software simulation runtime.
        /// </summary>
        public string SyslayPathSim { get; set; } = string.Empty;
        public string SysresPathSim { get; set; } = string.Empty;

        public string IoBindingsPath { get; set; } = "Input/SMC_Rig_IO_Bindings.xlsx";

        /// <summary>
        /// Path to a folder containing a validated M262 hardware-configuration baseline
        /// (an EAE project's <c>HwConfiguration/</c> folder plus the <c>.hcf</c> file
        /// under <c>IEC61499/System/{sys-guid}/{sysdev-guid}/{sysdev-guid}.hcf</c>).
        /// The TM3 module slot/topology layout — BMTM3 → TM262L01MDESE8T → TM3DI16_G →
        /// TM3DQ16T_G — is fixed by the physical SMC rig wiring and therefore cannot
        /// be synthesised from Control.xml; the deployer copies it verbatim from this
        /// path and only overwrites the channel ParameterValue strings (DI00, DI01,
        /// DO00) using IoBindings.
        /// </summary>
        public string M262HardwareConfigBaselinePath { get; set; } = string.Empty;

        /// <summary>
        /// IPV4 address of the M262 controller on the rig network. Written into the
        /// M262 sysdev as <c>&lt;Parameter Name="IPV4Address" Value="..."/&gt;</c>
        /// so EAE's Physical Devices canvas shows the controller pre-populated. Operator
        /// can still override in EAE before deploy if the rig moves networks.
        /// </summary>
        public string M262TargetIp { get; set; } = "192.168.1.10";

        /// <summary>
        /// Subnet/network parameters used by the M262 topology emitter so the
        /// Physical Devices canvas shows the M262 wired to a logical network with
        /// a configured IP. Driven from the rig wiring — defaults are the SMC rig
        /// 192.168.1.0/24.
        /// </summary>
        public string M262SubnetAddress { get; set; } = "192.168.1.0";
        public string M262SubnetMask { get; set; } = "255.255.255.0";
        public string M262Gateway { get; set; } = "192.168.1.254";
        public string M262LogicalNetworkName { get; set; } = "DeviceNetwork_1";

        /// <summary>
        /// IPV4 address of the M580 controller on the rig network. Written into
        /// the M580 Equipment JSON the Topology emitter produces, on the
        /// seGmac0 endpoint. Without a real IP (i.e. the prior hard-coded
        /// "0.0.0.0" placeholder) EAE's Deploy &amp; Diagnostic tab refuses to
        /// list the device — the M262 in the same project IS listed despite
        /// having the same "00000000-0000-0000-0000-000000000000" domain UUID
        /// because its IP is concrete, so the IP is the discriminator.
        /// Default matches the reference SMC_Rig_Expo_withClamp rig wiring.
        /// </summary>
        public string M580TargetIp { get; set; } = "192.168.1.20";

        /// <summary>
        /// BroadcastDomain UUID the M580 seGmac0 IP-Address endpoint binds to.
        /// Mapper used to emit the all-zeros NOCONF UUID here so EAE left the
        /// device on "no broadcast domain" — but that hides the Logical Network
        /// / Subnet / Gateway columns in EAE's hardware property editor, and the
        /// user expects the M580 panel to read "Default Network / 192.168.0.0 /
        /// 255.255.255.0 / 192.168.0.254" matching the Workstation NIC and the
        /// BroadcastDomain_Default Network.json file that EAE 24.1 ships with
        /// every fresh Demonstrator. Pin the M580 endpoint to the live
        /// "Default Network" broadcast domain UUID
        /// 2131fbdd-0a41-4e41-abfb-a14a5ca9218d (matches the value in
        /// Topology/BroadcastDomain_Default Network.json on the rig). M262 is
        /// intentionally left on NOCONF per user — don't touch the M262 file.
        /// </summary>
        public string M580BroadcastDomainUuid { get; set; }
            = "2131fbdd-0a41-4e41-abfb-a14a5ca9218d";

        /// <summary>
        /// Subnet base address the "Default Network" BroadcastDomain JSON
        /// declares. Pinned to the reference SMC_Rig_Expo_withClamp value
        /// (192.168.0.0/24) so EAE sees a byte-identical topology when the
        /// user opens that solution to Take Ownership of the M580. The rig's
        /// device-side IP (192.168.1.20) sits OUTSIDE this /24 — EAE tolerates
        /// the mismatch (the reference ships this way and works), the connect
        /// dialog just highlights the subnet/gateway rows in yellow. Default
        /// follows reference; override if you commission a rig on a strictly
        /// matching subnet later.
        /// </summary>
        public string DefaultNetworkSubnetAddress { get; set; } = "192.168.0.0";

        /// <summary>
        /// Subnet mask for the "Default Network" BroadcastDomain JSON.
        /// </summary>
        public string DefaultNetworkSubnetMask { get; set; } = "255.255.255.0";

        /// <summary>
        /// Gateway address for the "Default Network" BroadcastDomain JSON.
        /// Pinned to the reference SMC_Rig_Expo_withClamp value (192.168.0.254)
        /// so the Demonstrator topology mirrors the SMC_Rig_Expo solution
        /// exactly. The physical M580 reports 0.0.0.0 for its own gateway —
        /// EAE flags this row yellow in the connect dialog but tolerates it
        /// (the reference shipped this way and works). Default follows
        /// reference; override only if you commission a rig with an actual
        /// gateway set on the device.
        /// </summary>
        public string DefaultNetworkGateway { get; set; } = "192.168.0.254";

        /// <summary>
        /// UUID of the "Default Network" BroadcastDomain. Matches the live
        /// Topology/BroadcastDomain_Default Network.json on the rig
        /// (2131fbdd-...). Kept as a single constant on both the M580 endpoint
        /// binding and the BroadcastDomain JSON so they always cross-reference.
        /// </summary>
        public string DefaultNetworkUuid { get; set; }
            = "2131fbdd-0a41-4e41-abfb-a14a5ca9218d";

        /// <summary>
        /// IPV4 address of the BX1 soft-dPAC workstation on the rig network.
        /// Same Deploy &amp; Diagnostic visibility constraint as the M580 above.
        /// Default matches the reference SMC_Rig_Expo_withClamp rig wiring,
        /// where the BX1 softdpac runtime listens on 192.168.1.151 (the
        /// HMIB1X_1 panel hosts it; 192.168.1.209 is the panel itself).
        /// </summary>
        public string BX1TargetIp { get; set; } = "192.168.1.151";

        /// <summary>
        /// IPV4 address of the HMIB1X industrial panel that HOSTS the BX1 softdpac
        /// container. On the reference SMC_Rig_Expo_withClamp the panel (HMIB1X_1)
        /// is 192.168.1.209 and the softdpac runtime it hosts is BX1TargetIp
        /// (192.168.1.151). EAE deploys/logs in to the runtime IP (.151); the host
        /// IP (.209) is the panel's management interface. This is the field that
        /// makes BX1 a REMOTE HMIB1X panel rather than a local Workstation (whose
        /// runtime EAE resolves to 127.0.0.1 — the "cannot connect to BX1" error).
        /// </summary>
        public string BX1HostIp { get; set; } = "192.168.1.209";

        /// <summary>
        /// ISOLATION (2026-06-08): emit the BX1 EtherNet/IP remote-I/O coupler
        /// (Equipment_EtherNetIPDevice_1.json + its FDT Content) only when TRUE.
        /// A DtmDeviceDEO forces EAE's FDT framework to LOAD an FdtProject.prj on
        /// topology import; an FDT project copied verbatim from another solution can
        /// make EAE's topology server throw an immediate 500 ("Unable to import
        /// topology / Internal Server Error"). The BX1 HMIB1X login does NOT need
        /// this device (it is the covers' physical I/O, a separate concern), so it
        /// is held OUT by default until the HMIB1X import + login is confirmed
        /// working. When FALSE the emitter also SWEEPS any previously-deployed copy
        /// (equipment JSON + Content files + topologyproj registrations) so the
        /// topology imports clean. Flip to TRUE once a DTM-import path is proven.
        /// </summary>
        // 2026-06-09: re-enabled. The topology-import 500 was an ORPHANED WIRE
        // (Wire_Wire 145.json → the dead Workstation NIC uuid …053), now auto-swept
        // by TopologyNetworkEmitter.SweepOrphanWires — NOT this device. With that
        // fixed + the SE.FieldDevice/Standard.IoEtherNetIP libraries referenced, the
        // EtherNet/IP cover-I/O coupler imports cleanly, so it is emitted again (the
        // BX1 softdpac's EtherNet/IP scanner in the .hcf references it; without the
        // topology device the physical-devices section is incomplete).
        public bool EmitBx1EtherNetIpDevice { get; set; } = true;

        /// <summary>
        /// Master gate for the BX1 EtherNet/IP cover-I/O broker (BX1_IO). When TRUE:
        ///   (Stage 1) deploys PLC_RW_BX1 + changeEventM262_2 and instantiates the
        ///     BX1_IO broker (id F6C04A4BA6FA8593) on the BX1 sysres + SubApp, wiring
        ///     its INIT — so the .hcf EtherNet/IP symlinks (RES0.BX1_IO.EIP_Input_Word_1
        ///     / _Output_Word_1) resolve instead of showing red/unresolved;
        ///   (Stage 2) bridges the broker's word I/O to OUR ring-model covers'
        ///     symlinks (RES0.&lt;cover&gt;.athome/atwork in; .OutputToWork/OutputToHome out)
        ///     — no cover CAT changes, our Five_State_Actuator_CAT already exposes them;
        ///   (Stage 3) a local BX1 cover pick/place cycle drives the covers.
        /// FALSE = today's working compile (no broker). Independent of
        /// ExtendStateRingAcrossBx1 (which stays FALSE — it broke the M580 run): the
        /// reference BX1 has NO cover stateRprtCmd ring, so the broker is wholly
        /// separate from any cross-PLC ring extension.
        /// </summary>
        public bool DeployBx1IoBroker { get; set; } = true;

        /// <summary>
        /// BX1 cover-I/O bridge placement. TRUE (default) = INTERNALIZED: the per-cover
        /// sensor/coil symlink bridge + scan cycle live INSIDE the PLC_RW_BX1 composite
        /// (Bx1IoBrokerInjector.EmbedCoverBridgeInComposite generates them from the cover↔bit
        /// map at deploy time), so the generated BX1 sysres/syslay carries ONLY the single
        /// <c>BX1_IO</c> instance — no BX1IO_Sense_*/BX1IO_Coil_*/BX1_IO_Cycle FBs.
        /// FALSE = the proven EXTERNAL bridge: Bx1IoBrokerInjector injects the 6 symlink FBs
        /// + E_DELAY into the resource (the path verified live at EIP_Output_Word=16#0004).
        /// BX1-only — M262/M580 unaffected. The one EAE-runtime unknown the internalized path
        /// rests on is whether a SYMLINKMULTIVAR with an ABSOLUTE cross-instance NAME
        /// (BX1_RES.CoverPNP_Vr.OutputToWork) resolves from INSIDE a composite type; flip to
        /// FALSE + clean-rebuild to restore the external path if it doesn't.
        /// </summary>
        public bool Bx1BridgeInsideComposite { get; set; } = true;

        /// <summary>
        /// M262 resource name written into the .sysres root and the .sysdev's
        /// &lt;Resource&gt; entry. Default "M262_RES" so the EAE Deploy &amp;
        /// Diagnostic tree reads "M262 &gt; M262_RES" rather than the generic
        /// Schneider default "RES0", making the device-target binding
        /// self-evident in multi-runtime projects (the M580 + BX1 sysres are
        /// equivalently named M580_RES / BX1_RES — see
        /// Station2DeviceEmitter.M580ResourceName / BX1ResourceName).
        ///
        /// <para>An earlier attempt forced this to "RES0" on the hypothesis
        /// that EAE 24.1's catalog templates rendered a phantom RES0 alongside
        /// any custom-named sysres, surfacing "Device &lt;name&gt; contains 2
        /// instances of Runtime.Management.EMB_RES_ECO" at compile. The real
        /// root cause turned out to be a duplicate-Layer-ID .syslay stub
        /// (handled by CompileCachePurger's sweep), not the resource name.
        /// Per-PLC names are now safe and resumed.</para>
        /// </summary>
        public string ResourceName { get; set; } = "M262_RES";

        /// <summary>
        /// Folder holding per-PLC HCF (Hardware Configuration File) templates exported
        /// from EAE. Each .hcf carries the TM3/X80 slot layout plus DI/DO channel
        /// ParameterValue bindings to symbolic names (e.g. 'RES0.M262IO.PusherAtHome').
        /// Mapper copies these templates verbatim into the deployed project and
        /// rewrites only the symbol bindings from IoBindings.xlsx. Bus topology is
        /// fixed by physical rig wiring and never synthesised from Control.xml.
        /// </summary>
        public string IoFolderPath { get; set; } = string.Empty;

        /// <summary>
        /// HCF template for the M262 PLC (Station 1 — BMTM3 bus + TM262L01MDESE8T CPU
        /// + TM3DI16_G + TM3DQ16T_G modules). Holds the Feed_Station IO bindings:
        /// Pusher/Checker/Transfer atHome/atWork sensors and Extend* output coils.
        /// </summary>
        public string M262HcfTemplatePath { get; set; } = string.Empty;

        /// <summary>
        /// HCF template for the M580 PLC (Station 2 — BMXBUS + BMEXBP0400 rack +
        /// BMED581020 CPU + BMXDDM16025 modules). Holds the Assembly_Station IO
        /// bindings: SwivelArm AtPick/AtPlace/AtHome sensors, Bearing_Gripper,
        /// Shaft_Vr/Shaft_Hr atHome/atWork, Shaft_Gripper, Clamp.
        /// </summary>
        public string M580HcfTemplatePath { get; set; } = string.Empty;

        /// <summary>
        /// HCF template for the BX1 PLC (secondary IO island — Cover PnP + Ejector +
        /// CoverGripper + TopCoverSensor bindings). Currently exists only as a wiring
        /// reference (BX1 IO.png in the IO folder) and no .hcf file has been exported
        /// yet; the path is reserved so Mapper can pick it up once available.
        /// </summary>
        public string BX1HcfTemplatePath { get; set; } = string.Empty;

        /// <summary>
        /// When TRUE, the simulator pipeline collapses the entire SMC rig
        /// (Feed_Station + Assembly_Station + Disassembly + Robot orchestrator)
        /// into a SINGLE resource (one SIM device, one sysres, one syslay).
        /// All 4 Processes, all 13 actuators and all 4 sensors live on one
        /// flat FBNetwork with a single CaSBus init chain and a single
        /// stateRptCmd ring. No cross-device SIFB channels, no M580/BX1
        /// sysdev/hcf, no commdesc.xml. Cross-process handshakes that the
        /// hardware path drops (because Feed_Station/HandShake waits on
        /// Disassembly/handshake which lives on a different PLC) are
        /// preserved here by wiring Process[i].state_update directly into
        /// Process[j].state_change on the shared canvas.
        ///
        /// <para>The hardware path (Button 2 / btnTestStation1) ignores this
        /// flag — the Feed Station slice must regenerate byte-identical to
        /// today's working output. Only the "Test Simulator" button flips
        /// this on before running the pipeline. Default FALSE so a fresh
        /// MapperConfig keeps the hardware path stable.</para>
        ///
        /// <para>Bearing_PnP (a 13-state branched actuator) is stubbed with
        /// Five_State_Actuator_CAT when this flag is on, with an activity
        /// warning that the assembly/disassembly branch selection is
        /// approximated — the rest of the system still generates and runs.</para>
        /// </summary>
        public bool SimulatorFullSystem { get; set; } = false;

        /// <summary>
        /// Emit the Process recipe as one <c>Recipe : ARRAY OF RecipeStep</c>
        /// struct input instead of the six parallel arrays (StepType,
        /// CmdTargetName, CmdStateArr, Wait1Id, Wait1State, NextStep) — on the
        /// HARDWARE / Test Runtime path, not just the simulator. The exact same
        /// machinery the simulator already uses (DeployRecipeStepDatatype +
        /// NormalizeProcess1RecipeArrays + NormalizeProcessRuntimeRecipeArrays +
        /// BuildProcessFbParameters useRecipeStruct) is driven by
        /// <c>(SimulatorFullSystem || UseRecipeStruct)</c>, so the FB interface,
        /// the engine ST and the instance parameter stay in lock-step. The
        /// RecipeStep struct's CmdTargetName is STRING[150] (not the simulator's
        /// old STRING[15]) so long names like 'coverpnp_gripper' don't overflow.
        /// Default TRUE: runtime and simulator both use the single RecipeStep
        /// array input. Recipe content/ordering is generated upstream; the
        /// carrier mechanism stays stable.
        /// </summary>
        public bool UseRecipeStruct { get; set; } = true;

        // ============================================================
        // MQTT event-driven state publishing (no-loss fix)
        // ------------------------------------------------------------
        // The OPC UA / WebSocket HMI paths sit downstream of the runtime
        // sampler (200 ms M262/BX1, 100 ms M580), so brief states (ToWork=1,
        // AtWork=2, ToHome=3, AtHomeEnd=4) shorter than the sample interval
        // are aliased out and never reach the client. MQTT_PUBLISH is a
        // function block inside the scan: it fires on the actuator's pst_out
        // the same scan the state changes, before any sampler, so nothing is
        // lost. These fields drive the single shared MQTT_CONNECTION the
        // Mapper injects; every embedded MQTT_PUBLISH binds to it by matching
        // ConnectionID value (no wire between them).
        // ============================================================

        /// <summary>
        /// Master opt-in. When FALSE (default) the Mapper emits exactly what
        /// it does today — no MQTT_CONNECTION injected, no ConnectionID
        /// stamped — so the hardware/sim paths stay byte-stable and backward
        /// safe. Flip TRUE only after the two-part jitter gate passes on the
        /// rig (dead broker + slow broker, ActuatorCore scan stays flat).
        /// </summary>
        public bool MqttPublishEnabled { get; set; } = false;

        /// <summary>Broker endpoint for MQTT_CONNECTION.URL. EAE 24.1's
        /// <c>CMQTTClientStateMgr.validateEndpoint</c> has two gates:
        /// <list type="number">
        ///   <item>Scheme must be one of <c>mqtt://</c> / <c>mqtts://</c> / <c>ws://</c>
        ///     / <c>wss://</c> — <c>tcp://</c> trips "The URI scheme is not MQTT".</item>
        ///   <item>The runtime defaults to <b>secure-by-default</b>: plain
        ///     <c>mqtt://</c> trips "Insecure configuration prohibited; TLSconfig"
        ///     unless a TLS cert is wired. The proven workaround (matches the
        ///     reference TrainingIIoT MQTT_CONNECTION) is to use scheme
        ///     <c>mqtts://</c> against a plain broker on port 1883 — EAE
        ///     accepts the scheme name and uses plain transport on the port,
        ///     no actual TLS negotiation occurs.</item>
        /// </list></summary>
        public string MqttBrokerUrl { get; set; } = "mqtt://192.168.1.50:1883";

        /// <summary>MQTT_CONNECTION.ClientIdentifier — one per runtime/resource.</summary>
        public string MqttClientId { get; set; } = "SMC_M262";

        /// <summary>
        /// MQTT_CONNECTION.ConnectionID — the registry key. The single
        /// injected connection sets this, and every embedded MQTT_PUBLISH
        /// carries the same value so they bind. Default 1.
        /// </summary>
        public int MqttConnectionId { get; set; } = 1;

        /// <summary>
        /// MQTT_CONNECTION.QueueDepth — offline buffer depth. When the broker
        /// is unreachable the connection queues up to this many messages in
        /// PLC memory and redelivers on reconnect (the PLC half of the
        /// end-to-end no-loss buffer). Default 100.
        /// </summary>
        public int MqttQueueDepth { get; set; } = 100;

        /// <summary>MQTT_PUBLISH.QoS1 — 1 = at-least-once (broker acks). Default 1.</summary>
        public int MqttQoS { get; set; } = 1;

        /// <summary>
        /// MQTT_CONNECTION.CleanSession — FALSE keeps the broker session
        /// across drops so QoS1 messages are not lost on a reconnect. Default false.
        /// </summary>
        public bool MqttCleanSession { get; set; } = false;

        /// <summary>
        /// MQTT_PUBLISH.Retain1 — FALSE for an event stream (every transition
        /// is a discrete message the logger captures). Default false.
        /// </summary>
        public bool MqttRetain { get; set; } = false;

        /// <summary>
        /// MQTT_CONNECTION.KeepAlive in milliseconds. The MQTT keepalive ping
        /// interval — the client tells the broker "I'm still here" this often,
        /// and the broker disconnects clients that miss two periods. Default
        /// 60000 ms = 60 s (standard MQTT 3.1.1 recommendation).
        /// <para>Was left empty by Mapper for months because passing an INT
        /// constant to the TIME port raised ERR_CAST_CONSTANT at compile
        /// time. The fix is to format it as a TIME literal (<c>T#60000ms</c>)
        /// via <c>SyslayBuilder.FormatTimeMs</c>, not as a bare int. Without
        /// any value at all the M262 firmware applied <c>T#0s</c> as default,
        /// which made the connection give up before the first SYN-ACK and
        /// produced the rig symptom "broker never sees a connection attempt
        /// from 192.168.1.10".</para>
        /// </summary>
        public int MqttKeepAliveMs { get; set; } = 60000;

        /// <summary>
        /// MQTT_CONNECTION.ConnectionTimeout in milliseconds. How long the FB
        /// waits for the TCP 3-way handshake + MQTT CONNACK before deciding
        /// the connect attempt failed. Default 5000 ms = 5 s. EAE's implicit
        /// default for an unset TIME port is T#0s which aborts the connect
        /// before the first SYN-ACK round-trip completes.
        /// </summary>
        public int MqttConnectionTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// MQTT_CONNECTION.ConnectionRetryCount — how many times the FB
        /// retries after a failed connect before giving up. Default 999
        /// (effectively infinite so the FB keeps trying through transient
        /// network blips). An unset value defaults to 0 = give up after the
        /// first failure, which is the wrong choice for a rig that may boot
        /// before its broker is reachable.
        /// </summary>
        public int MqttConnectionRetryCount { get; set; } = 999;

        /// <summary>
        /// MQTT_CONNECTION.ConnectionRetryTime in milliseconds. Wait between
        /// retry attempts. Default 2000 ms = 2 s. Without an explicit value
        /// the firmware applies T#0s which either disables retry entirely or
        /// busy-loops them (depends on firmware revision).
        /// </summary>
        public int MqttConnectionRetryTimeMs { get; set; } = 2000;

        /// <summary>
        /// Topic root. The per-instance topic is built as
        /// <c>{MqttTopicRoot}/{instance}/state</c>. When the CAT's
        /// RootPath='$${PATH}' macro resolves inside the MQTT parameter this
        /// is informational; if it does NOT resolve, the Mapper stamps the
        /// resolved RootPath per instance using this prefix. Default "smc".
        /// </summary>
        public string MqttTopicRoot { get; set; } = "smc";

        public string ActiveSyslayPath =>
            !string.IsNullOrEmpty(SyslayPath2) ? SyslayPath2 : SyslayPath;

        public string ActiveSysresPath =>
            !string.IsNullOrEmpty(SysresPath2) ? SysresPath2 : SysresPath;

        public string TemplateIec61499Dir =>
            Path.GetDirectoryName(Path.GetDirectoryName(ActuatorTemplatePath)) ?? string.Empty;

        public string TemplateHmiDir =>
            Path.Combine(Path.GetDirectoryName(TemplateIec61499Dir) ?? string.Empty, "HMI");

        public static MapperConfig Load()
        {
            var configPath = Path.Combine(Environment.CurrentDirectory, ConfigFileName);

            if (!File.Exists(configPath))
            {
                var def = CreateDefault();
                Save(configPath, def);
                return def;
            }

            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<MapperConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new Exception($"Failed to deserialise config from '{configPath}'");
        }

        private static MapperConfig CreateDefault() => new()
        {
            SystemXmlPath = @"C:\VueOne\system\Control.xml",
            MappingRulesPath = @"Input\VueOne_IEC61499_Mapping.xlsx",
            TemplateLibraryPath = @"C:\VueOneMapper\Template Library",
            ActuatorTemplatePath = @"C:\Station1\IEC61499\Five_State_Actuator_CAT\Five_State_Actuator_CAT.fbt",
            SensorTemplatePath = @"C:\Station1\IEC61499\Sensor_Bool_CAT\Sensor_Bool_CAT.fbt",
            // Process1_Generic.fbt is the new outer composite template (Phase 1+2);
            // the legacy Process1_CAT.fbt is no longer deployed by Mapper.
            ProcessCATTemplatePath = @"C:\Station1\IEC61499\Process1_Generic\Process1_Generic.fbt",
            RobotTemplatePath = @"C:\SMC_Rig\IEC61499\Robot_Task_CAT\Robot_Task_CAT.fbt",
            RobotBasicTemplatePath = @"C:\SMC_Rig\IEC61499\Robot_Task_Core.fbt",
            SyslayPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath = @"C:\Station1\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            SyslayPath2 = @"C:\Demonstrator\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPath2 = @"C:\Demonstrator\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            SyslayPathSim = @"C:\DemonstratorSim\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000001\00000000-0000-0000-0000-000000000000.syslay",
            SysresPathSim = @"C:\DemonstratorSim\Demonstrator\IEC61499\System\00000000-0000-0000-0000-000000000000\00000000-0000-0000-0000-000000000002\00000000-0000-0000-0000-000000000000.sysres",
            IoBindingsPath = @"Input\SMC_Rig_IO_Bindings.xlsx",
            M262HardwareConfigBaselinePath = string.Empty,
            // IO authoring folder + per-PLC .hcf exports. Defaulted so a fresh
            // config (e.g. launched from a working dir without a saved
            // mapper_config.json) still finds the hardware-config files instead
            // of silently skipping the .hcf copy and leaving M580/BX1 empty.
            IoFolderPath = @"C:\VueOneMapper\IO",
            M262HcfTemplatePath = @"C:\VueOneMapper\IO\M262IO.hcf",
            M580HcfTemplatePath = @"C:\VueOneMapper\IO\M580IO.hcf",
            BX1HcfTemplatePath = @"C:\VueOneMapper\IO\BX1IO.ethernetip.hcf",
            M262TargetIp = "192.168.1.10",
            M262SubnetAddress = "192.168.1.0",
            M262SubnetMask = "255.255.255.0",
            M262Gateway = "192.168.1.254",
            M262LogicalNetworkName = "DeviceNetwork_1",
            ResourceName = "M262_RES",
        };

        private static void Save(string path, MapperConfig config)
        {
            var json = JsonSerializer.Serialize(config,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
