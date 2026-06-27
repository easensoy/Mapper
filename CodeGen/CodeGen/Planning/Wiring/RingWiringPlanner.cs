using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using static CodeGen.Translation.SystemInjector;

namespace CodeGen.Translation
{
    /// <summary>
    /// Per-station syslay WIRING planner. Owns the three application-canvas wiring chains the
    /// generator lays for each PLC station onto the shared <see cref="SyslayBuilder"/>:
    /// the INIT chain, the stationAdptr (CaS) chain, and the stateRprtCmd report ring — for Feed
    /// (M262), Station 2 (M580: Assembly + Disassembly) and BX1 (Cover PnP). Extracted out of the
    /// (large) <see cref="SystemInjector"/>, which keeps FB EMISSION + type resolution, so the
    /// wiring concern lives in one cohesive module. FB-type resolution
    /// (<see cref="SystemInjector.ResolveActuatorFBType"/>) and the ring/CaS port vocabulary
    /// (<see cref="SystemInjector.StateRprtIn"/>/<c>StateRprtOut</c>,
    /// <see cref="SystemInjector.StationAdptrIn"/>/<c>StationAdptrOut</c>) stay on
    /// <c>SystemInjector</c> and are reached via <c>using static</c>; the cross-PLC discharge
    /// decision is <see cref="HandoffPlanner.DischargeActive"/> (single source of truth).
    /// </summary>
    internal static class RingWiringPlanner
    {
        static bool NameEq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        internal static void BuildFeedStationWiring(SyslayBuilder builder, StationContents contents)
        {
            // Process FB instance name resolved via InstanceNameResolver — must match
            // what GenerateFeedStationSyslayToPath emitted upstream so the wire endpoints
            // line up. Without the config we can't reach the Excel overrides here, so
            // we rely on the resolver's default convention (strip "_process" suffix);
            // for explicit overrides the upstream emit and this wiring agree because
            // both go through the same resolver code path.
            var processInstanceName = InstanceNameResolver.Resolve(contents.Process);
            if (string.IsNullOrWhiteSpace(processInstanceName)) processInstanceName = "Process1";

            // PER-PLC FILTER.
            // Feed_Station lives on M262 only — Station1, Stn1_Term and the
            // Area/Station HMI adapters all belong to the M262 frame. When
            // contents.Sensors / contents.Actuators expanded to whole-system
            // scope (Phase-1 task #9), this function began stitching M580 +
            // BX1 components into a single cross-PLC chain on the syslay:
            //   ... ShaftSensor (M580) → TopCoverSenosr (BX1) → Feeder (M262) ...
            // EAE renders direct event/adapter wires that cross a resource
            // boundary as DASHED ("unresolved") because they cannot be deployed
            // — a wire on M262's resource cannot signal an FB that runs on
            // M580. Result: every M262 actuator (Feeder, Checker, Transfer)
            // showed dashed inputs on >>stationAdptr_in / >>stateRprtCmd_in,
            // EAE blocked deploy, and the Pusher never received its CMD.
            // Each PLC's sysres still gets its own contained chain via
            // ResourceWireEmitter.EmitForResource (M580 + BX1 chain themselves
            // through Station2 / their own components), so dropping non-M262
            // names here only removes the cross-PLC syslay wires — every
            // resource's runtime chain is unaffected.
            static bool IsM262(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) == PlcAssignment.M262;

            // STAGE 5b: the M262 robot-tail (Ejector + Robot) are Disassembly's actuators that
            // merely live on M262 and carry cross-PLC links to M580. They must NOT sit in the
            // critical INIT path to Feed_Station: a stall in the Robot's bring-up would block
            // Feed_Station.INIT and kill the whole Feed station when all three PLCs run (Feed works
            // M262-alone because the cross-PLC links are absent). So init the Feed components ->
            // Feed_Station FIRST, then the tail. Same split is mirrored in ResourceWireEmitter
            // (sysres). This bool is also reused for the report-ring split below. Off -> tail empty
            // -> byte-identical.
            bool robotTail = MapperConfig.EnableRobotTaskTail &&
                contents.Actuators.Any(a => NameEq(a.Name, "Ejector")) &&
                contents.Actuators.Any(a => NameEq(a.Name, "Robot"));
            bool IsRobotTailName(string name) =>
                robotTail && (NameEq(name, "Ejector") || NameEq(name, "Robot"));

            var initChain = new List<string>();
            initChain.Add("Area");
            initChain.Add("Station1");
            foreach (var s in contents.Sensors)
                if (IsM262(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)
                if (IsM262(a.Name) && !IsRobotTailName(a.Name)) initChain.Add(a.Name);
            initChain.Add(processInstanceName);
            foreach (var a in contents.Actuators)
                if (IsM262(a.Name) && IsRobotTailName(a.Name)) initChain.Add(a.Name);

            // No PLC_Start bootstrap edges: Area_CAT contains its own internal plcStart
            // which fires Area.INITO via INIT, propagating through this chain.
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            builder.AddAdapterConnection("Area_HMI.AreaHMIAdptrOUT", "Area.AreaHMIAdptrIN");
            builder.AddAdapterConnection("Station1_HMI.StationHMIAdptrOUT", "Station1.StationHMIAdptrIN");
            builder.AddAdapterConnection("Area.AreaAdptrOUT", "Station1.AreaAdptrIN");
            builder.AddAdapterConnection("Station1.AreaAdptrOUT", "Area_Term.CasAdptrIN");

            // v1-assumption: Sensor_Bool_CAT lacks stationAdptr ports per .fbt verification.
            // CaSBus chain skips sensors and includes only actuators + the Process instance.
            // M262-only filter (same rationale as the init chain above) — pre-filter
            // keeps Station1.StationAdaptrOUT -> Feeder -> Checker -> Transfer -> Feed_Station
            // -> Stn1_Term, with no cross-PLC actuators stitched in.
            // Resolve each actuator's REAL CAT type (not a blanket Five_State assumption) and skip
            // types with NO stationAdptr port. STAGE 5b: the UR3e (Robot_Task_CAT) is an M262
            // actuator with no stationAdptr — without this skip it dangled
            //   Ejector.stationAdptr_out -> Robot.stationAdptr_in  and
            //   Robot.stationAdptr_out -> Feed_Station.stationAdptr_in
            // (ports that don't exist → EAE rejected M262 → "nothing triggers"). Ejector
            // (Five_State, real ports) stays, so the chain re-links Transfer → Ejector →
            // Feed_Station. Same single rule the M580 chain + ResourceWireEmitter use
            // (TemplateMap.NoStationAdapterCatTypes). Off → every M262 actuator is Five_State, so
            // ResolveActuatorFBType returns the identical type and the chain is byte-identical.
            var stationChain = new List<(string Name, string Type)>();
            foreach (var a in contents.Actuators)
            {
                if (!IsM262(a.Name)) continue;
                var fbType = ResolveActuatorFBType(a);
                if (TemplateMap.LacksStationAdapter(fbType)) continue;
                stationChain.Add((a.Name, fbType));
            }
            stationChain.Add((processInstanceName, "Process1_Generic"));

            if (stationChain.Count > 0)
            {
                builder.AddAdapterConnection("Station1.StationAdaptrOUT",
                    $"{stationChain[0].Name}.{StationAdptrIn(stationChain[0].Type)}");
                for (int i = 0; i < stationChain.Count - 1; i++)
                    builder.AddAdapterConnection(
                        $"{stationChain[i].Name}.{StationAdptrOut(stationChain[i].Type)}",
                        $"{stationChain[i + 1].Name}.{StationAdptrIn(stationChain[i + 1].Type)}");
                builder.AddAdapterConnection(
                    $"{stationChain[^1].Name}.{StationAdptrOut(stationChain[^1].Type)}",
                    "Stn1_Term.CasAdptrIN");
            }

            // Report-ring is M262-only too — keeps PartInHopper -> Feeder -> Checker
            // -> Transfer -> Feed_Station -> PartInHopper (closed). The M580 / BX1
            // resources each close their own ring in their own sysres.
            // STAGE 5b: when the robot tail is active the Ejector + Robot are NOT part of the
            // closed Feed ring — they form a separate cross-PLC segment (BuildStation2Wiring
            // splices Disassembly→Ejector and Robot→BearingSensor; the local Ejector→Robot hop is
            // added below). Filtering them here keeps the Feed ring closed around Feed components
            // + Feed_Station ONLY and stops Robot.stateRprtCmd_out being driven twice (Feed ring +
            // cross-hop = the double-drive). Off → ringComponents byte-identical.
            // (robotTail is computed once above, by the init-chain split, and reused here.)
            var ringComponents = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)
                if (IsM262(s.Name)) ringComponents.Add((s.Name, "Sensor_Bool_CAT"));
            foreach (var a in contents.Actuators)
                if (IsM262(a.Name) &&
                    !(robotTail && (NameEq(a.Name, "Ejector") || NameEq(a.Name, "Robot"))))
                    ringComponents.Add((a.Name, "Five_State_Actuator_CAT"));
            ringComponents.Add((processInstanceName, "Process1_Generic"));

            if (ringComponents.Count > 1)
            {
                for (int i = 0; i < ringComponents.Count - 1; i++)
                    builder.AddAdapterConnection(
                        $"{ringComponents[i].Name}.{StateRprtOut(ringComponents[i].Type)}",
                        $"{ringComponents[i + 1].Name}.{StateRprtIn(ringComponents[i + 1].Type)}");
                // Close the M262 Feed ring locally (last -> first). The Feed ring is its own
                // closed loop; there is no cross-PLC ring splice.
                builder.AddAdapterConnection(
                    $"{ringComponents[^1].Name}.{StateRprtOut(ringComponents[^1].Type)}",
                    $"{ringComponents[0].Name}.{StateRprtIn(ringComponents[0].Type)}");
            }

            // STAGE 5b robot tail: the local intra-M262 chain of the Ejector->Robot segment.
            // The segment ENDS (seg[0].in from M580 Disassembly, seg[^1].out to M580 BearingSensor)
            // are the cross-device hops re-added by the HandoffPlanner when the robot tail is built;
            // only the intra-M262 links seg[i]->seg[i+1] are local here. robotTail off -> empty -> no-op.
            var m262Seg = TemplateMap.M262CrossRingSegment(robotTail);
            for (int i = 0; i < m262Seg.Count - 1; i++)
                builder.AddAdapterConnection(
                    $"{m262Seg[i]}.stateRprtCmd_out", $"{m262Seg[i + 1]}.stateRprtCmd_in");
        }

        /// <summary>
        /// Sibling of <see cref="BuildFeedStationWiring"/> for the M580 (Station 2).
        /// Emits the INIT chain, station-CaS adapter chain and stateRprtCmd ring for
        /// every M580-bucketed actuator/sensor onto the SHARED syslay so the
        /// application canvas shows the Station-2 wiring next to Feed_Station.
        /// Anchors:
        ///   Station FB  : Station2     (Station_CAT instance)
        ///   HMI FB      : Station2_HMI
        ///   Process FBs : Assembly_Station, Disassembly  (both Process1_Generic
        ///                 instances live on the M580 — sequence-stitch through both)
        ///   Terminator  : Stn2_Term    (CaSAdptrTerminator)
        /// The chain is contained inside one resource bucket (M580), so the wires
        /// render solid on the syslay — no dashed cross-PLC edges. M262 + BX1
        /// components are filtered out via <see cref="HcfSymbolIndex.NameBasedPlcGuess"/>.
        /// </summary>
        internal static void BuildStation2Wiring(SyslayBuilder builder, StationContents contents,
            string disassemblyFbName = null)
        {
            const string StationFb     = "Station2";
            const string StationHmiFb  = "Station2_HMI";
            const string AssemblyProc  = "Assembly_Station";
            const string Stn2Term      = "Stn2_Term";

            static bool IsM580(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) == PlcAssignment.M580;

            // STAGE 5a: thread the Disassembly Process FB into the M580 init chain, CaS
            // station chain and stateRprtCmd ring — RIGHT AFTER Assembly_Station — exactly as
            // ResourceWireEmitter does on the M580 sysres (compN → Assembly → Disassembly →
            // comp0). This keeps the application syslay and the resource sysres in lock-step;
            // without it the syslay's Assembly→BearingSensor close-back skips Disassembly and
            // EAE could re-derive a ring that drops it. Gated on the SAME flag as the sysres
            // side (UnparkDisassembly) AND the FB actually being present, so flag-off / no
            // Disassembly keeps the byte-identical Assembly-only layout.
            bool threadDisassembly =
                MapperConfig.UnparkDisassembly && !string.IsNullOrEmpty(disassemblyFbName);

            // Disassembly is generated as a parked END-only Process, but is not
            // threaded through the M580 wiring while Assembly_Station owns the cycle.
            // INIT chain: Station2 -> M580 sensors -> M580 actuators -> Assembly_Station.
            // Station2 has its own internal plcStart that fires Station2.INITO via INIT
            // (same shape Area_CAT uses on M262), so no FB1-bootstrap edge is needed
            // at syslay scope; the M580 sysres wires get the FB1->Station2.INIT bootstrap
            // separately via Station2WireEmitter / ResourceWireEmitter.
            var initChain = new List<string> { StationFb };
            foreach (var s in contents.Sensors)
                if (IsM580(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)
                if (IsM580(a.Name)) initChain.Add(a.Name);
            initChain.Add(AssemblyProc);
            if (threadDisassembly) initChain.Add(disassemblyFbName);   // Assembly_Station → Disassembly
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            // HMI adapter — Station2 faceplate feeds the Station2 instance.
            builder.AddAdapterConnection($"{StationHmiFb}.StationHMIAdptrOUT",
                                         $"{StationFb}.StationHMIAdptrIN");

            // stationAdptr (CaS) chain: Station2 -> M580 actuators -> Assembly_Station ->
            // Disassembly -> Stn2_Term. Sensors are excluded because Sensor_Bool_CAT
            // has no stationAdptr ports per .fbt verification (same rule
            // BuildFeedStationWiring uses for Feed_Station).
            //
            // Only the legacy Seven_State_Actuator_CAT lacks stationAdptr ports. The
            // active centre-home swivel CAT does have stationAdptr + stateRprtCmd, so
            // Station2 must wire Bearing_PnP through the chains using its resolved
            // template type instead of excluding every Seven-shaped actuator.
            var stationChain = new List<(string Name, string Type)>();
            foreach (var a in contents.Actuators)
            {
                if (!IsM580(a.Name)) continue;
                var fbType = ResolveActuatorFBType(a);
                // Skip types with no stationAdptr port (Seven_State + Robot_Task_CAT) — one shared
                // rule with BuildFeedStationWiring + ResourceWireEmitter.
                if (TemplateMap.LacksStationAdapter(fbType))
                    continue;
                stationChain.Add((a.Name, fbType));
            }
            stationChain.Add((AssemblyProc, "Process1_Generic"));
            if (threadDisassembly)   // Assembly_Station → Disassembly → Stn2_Term
                stationChain.Add((disassemblyFbName, "Process1_Generic"));
            if (stationChain.Count > 0)
            {
                builder.AddAdapterConnection($"{StationFb}.StationAdaptrOUT",
                    $"{stationChain[0].Name}.{StationAdptrIn(stationChain[0].Type)}");
                for (int i = 0; i < stationChain.Count - 1; i++)
                    builder.AddAdapterConnection(
                        $"{stationChain[i].Name}.{StationAdptrOut(stationChain[i].Type)}",
                        $"{stationChain[i + 1].Name}.{StationAdptrIn(stationChain[i + 1].Type)}");
                builder.AddAdapterConnection(
                    $"{stationChain[^1].Name}.{StationAdptrOut(stationChain[^1].Type)}",
                    $"{Stn2Term}.CasAdptrIN");
            }

            // stateRprtCmd ring among the M580 components + Assembly_Station.
            // Build the M580 component chain (sensors then actuators) EXCLUDING the
            // process — the process closes the ring.
            var m580 = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)
                if (IsM580(s.Name)) m580.Add((s.Name, "Sensor_Bool_CAT"));
            foreach (var a in contents.Actuators)
                if (IsM580(a.Name)) m580.Add((a.Name, ResolveActuatorFBType(a)));

            // stateRprtCmd ring: M580 sensors → actuators → Assembly_Station → [Disassembly]. The
            // discharge (HandoffPlanner.DischargeActive) splices the M262 segment (Ejector → Robot →
            // PartAtAssembly) onto the ring AT THE DISASSEMBLY SEAM via TWO cross-device adapter hops
            // EAE bridges (Disassembly.out → seg[0], seg[^1] → m580 head); the intra-M262 chain
            // seg[i]→seg[i+1] is added in BuildFeedStationWiring and the M580 sysres opens its
            // close-back at the boundary (ResourceWireEmitter). The M580 bearing/shaft/clamp ring
            // itself is NOT stretched — only a short segment hangs off the Disassembly node. Off →
            // the ring closes locally Disassembly → first sensor (BX1 covers stay BX1-local either way).
            var ring = new List<(string Name, string Type)>(m580);
            // COVER DETOUR: splice the BX1 cover actuators onto the
            // M580 ring between the last M580 actuator (Clamp) and Assembly_Station — Clamp.out crosses
            // to the first cover (M580→BX1), the covers chain locally, and the last cover crosses to
            // Assembly (BX1→M580). EAE bridges the two cross-device hops (the proven STAGE-4 mechanism).
            // This sits at a DIFFERENT seam than the discharge segment (Disassembly→ejector/robot below),
            // so the two compose with only M580↔BX1 and M580↔M262 boundaries — never M262↔BX1. The M580
            // sysres opens this seam (ResourceWireEmitter) so the boundary plug is never double-driven.
            // Empty when the detour is off → byte-identical.
            foreach (var cover in HandoffPlanner.CoverDetour)
                ring.Add((cover, "Five_State_Actuator_CAT"));
            ring.Add((AssemblyProc, "Process1_Generic"));
            if (threadDisassembly)   // … → Assembly_Station → Disassembly → (close back / segment)
                ring.Add((disassemblyFbName, "Process1_Generic"));
            if (ring.Count > 1)
            {
                for (int i = 0; i < ring.Count - 1; i++)
                    builder.AddAdapterConnection(
                        $"{ring[i].Name}.{StateRprtOut(ring[i].Type)}",
                        $"{ring[i + 1].Name}.{StateRprtIn(ring[i + 1].Type)}");
                var seg = TemplateMap.M262CrossRingSegment(HandoffPlanner.DischargeActive);
                if (seg.Count > 0)
                {
                    builder.AddAdapterConnection(
                        $"{ring[^1].Name}.{StateRprtOut(ring[^1].Type)}",
                        $"{seg[0]}.stateRprtCmd_in");
                    builder.AddAdapterConnection(
                        $"{seg[^1]}.stateRprtCmd_out",
                        $"{ring[0].Name}.{StateRprtIn(ring[0].Type)}");
                }
                else
                {
                    builder.AddAdapterConnection(
                        $"{ring[^1].Name}.{StateRprtOut(ring[^1].Type)}",
                        $"{ring[0].Name}.{StateRprtIn(ring[0].Type)}");
                }
            }
        }

        /// <summary>
        /// Sibling of <see cref="BuildStation2Wiring"/> for the BX1 Soft-dPAC
        /// sub-station (Cover PnP). BX1 has NO Station_CAT, NO Process FB and NO
        /// HMI faceplate on its own resource — Assembly_Station on the M580
        /// commands every BX1 actuator over the cross-PLC state_table broadcast
        /// ring (see Docs/ARCHITECTURE.md §4a). So BX1's syslay wiring is three
        /// chains over its own component set:
        /// <list type="bullet">
        ///   <item>MqttConn bring-up — <c>MqttConn.INITO → MqttConn.CONNECT</c>
        ///     self-loop so the broker connection opens as soon as the FB is
        ///     initialised (broker URL/ConnectionID/ClientIdentifier already
        ///     parameterised at injection time). Emitted only when MQTT is
        ///     enabled. MqttConn.INIT itself is sourced from the BX1 resource
        ///     boot anchor (FB1) on the sysres — that wire is invisible on the
        ///     syslay because FB1 lives on the sysres only.</item>
        ///   <item>INIT chain — MqttConn → sensor(s) → actuator(s). MqttConn
        ///     sits at the head so the broker is up BEFORE the embedded
        ///     MqttPub inside each CAT (patched in by PatchCatMqttPublish)
        ///     starts publishing. Without MqttConn at the head, the first
        ///     MqttPub.PUBLISH1 fires before MQTT_CONNECTION has opened and
        ///     EAE drops it (no QueueDepth on the publish side).</item>
        ///   <item>stateRprtCmd ring — closed loop across all BX1 sensors +
        ///     actuators. Closes back to the first sensor so every component
        ///     sees every other's state broadcast (the ring is how state
        ///     reaches the M580's Assembly_Station via the cross-PLC bridge).</item>
        /// </list>
        /// Skipped cleanly when BX1 has no components in the current fixture.
        /// </summary>
        internal static void BuildBx1Wiring(SyslayBuilder builder, StationContents contents,
            MapperConfig? config)
        {
            static bool IsBx1(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) == PlcAssignment.BX1;

            // MqttConn bring-up: self-loop INITO → CONNECT so the broker
            // connection opens as soon as the FB is initialised. Emitted only
            // when MQTT is enabled (otherwise no MqttConn FB exists). The
            // matching INIT-input wire (FB1.INITO → MqttConn.INIT) is emitted
            // on the BX1 sysres by ResourceWireEmitter — FB1 is the resource
            // boot anchor and lives only on the sysres, so the syslay shows
            // MqttConn.INIT dangling at the head; runtime resolves it through
            // the sysres bridge.
            bool mqttEnabled = config != null && config.MqttPublishEnabled;
            // Telemetry_CAT (config.UseTelemetryCat, default) names BX1's connection Telemetry_BX1;
            // the raw-FB revert keeps "MqttConn". The INITO->CONNECT self-loop passes through the wrapper.
            string bx1Conn = (config != null && config.UseTelemetryCat) ? "Telemetry_BX1" : "MqttConn";
            if (mqttEnabled)
                builder.AddEventConnection($"{bx1Conn}.INITO", $"{bx1Conn}.CONNECT");

            // INIT chain — the connection first (broker up before any embedded
            // MqttPub fires), then BX1 sensors, then BX1 actuators.
            var initChain = new List<string>();
            if (mqttEnabled) initChain.Add(bx1Conn);
            foreach (var s in contents.Sensors)    if (IsBx1(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)  if (IsBx1(a.Name)) initChain.Add(a.Name);
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            // stateRprtCmd ring — every BX1 sensor + actuator carries the
            // stateRprtCmd_in/out adapter ports. MqttConn is NOT in the ring
            // (no stateRprtCmd port).
            var ring = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)    if (IsBx1(s.Name)) ring.Add((s.Name, "Sensor_Bool_CAT"));
            // COVER DETOUR: the covers are on the M580 ring (wired by BuildStation2Wiring) — keep them
            // OUT of the BX1 ring so they are not double-wired. TopCoverSenosr stays a BX1 sensor
            // (INIT-only, off-ring; its id would clash on the M580 state_table).
            foreach (var a in contents.Actuators)
                if (IsBx1(a.Name) && !HandoffPlanner.IsCoverDetourActuator(a.Name))
                    ring.Add((a.Name, "Five_State_Actuator_CAT"));

            // BX1-local stateRprtCmd ring: self-closed broadcast loop (last -> first).
            if (ring.Count > 1)
            {
                for (int i = 0; i < ring.Count - 1; i++)
                    builder.AddAdapterConnection(
                        $"{ring[i].Name}.{StateRprtOut(ring[i].Type)}",
                        $"{ring[i + 1].Name}.{StateRprtIn(ring[i + 1].Type)}");
                builder.AddAdapterConnection(
                    $"{ring[^1].Name}.{StateRprtOut(ring[^1].Type)}",
                    $"{ring[0].Name}.{StateRprtIn(ring[0].Type)}");
            }
        }
    }
}
