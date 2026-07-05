using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using static CodeGen.Translation.SystemInjector;

namespace CodeGen.Translation
{
    // Per-station syslay WIRING planner: the INIT chain, stationAdptr (CaS) chain and stateRprtCmd
    // report ring for Feed (M262), Station 2 (M580) and BX1. FB-type resolution and the ring/CaS
    // port vocabulary stay on SystemInjector (reached via using static); the cross-PLC discharge
    // decision is HandoffPlanner.DischargeActive.
    internal static class RingWiringPlanner
    {
        static bool NameEq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        internal static void BuildFeedStationWiring(SyslayBuilder builder, StationContents contents)
        {
            // Must match the instance name GenerateFeedStationSyslayToPath emitted; both go through
            // the same resolver so the wire endpoints line up.
            var processInstanceName = InstanceNameResolver.Resolve(contents.Process);
            if (string.IsNullOrWhiteSpace(processInstanceName)) processInstanceName = "Process1";

            // Per-PLC filter: EAE renders direct wires crossing a resource boundary as dashed
            // ("unresolved") and blocks deploy. Each PLC's sysres gets its own contained chain via
            // ResourceWireEmitter, so dropping non-M262 names here only removes cross-PLC syslay wires.
            static bool IsM262(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) == PlcAssignment.M262;

            // Keep the M262 robot-tail (Ejector + Robot) OUT of the critical INIT path to
            // Feed_Station — a stall in the Robot's bring-up would block Feed_Station.INIT. Init the
            // Feed components -> Feed_Station first, then the tail (mirrored in ResourceWireEmitter).
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

            // Area_CAT's internal plcStart fires Area.INITO via INIT, propagating through this chain.
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            builder.AddAdapterConnection("Area_HMI.AreaHMIAdptrOUT", "Area.AreaHMIAdptrIN");
            builder.AddAdapterConnection("Station1_HMI.StationHMIAdptrOUT", "Station1.StationHMIAdptrIN");
            builder.AddAdapterConnection("Area.AreaAdptrOUT", "Station1.AreaAdptrIN");
            builder.AddAdapterConnection("Station1.AreaAdptrOUT", "Area_Term.CasAdptrIN");

            // CaS chain skips sensors (Sensor_Bool_CAT has no stationAdptr port) and any actuator
            // whose resolved CAT lacks stationAdptr — dangling those ports makes EAE reject the resource.
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

            // Report ring is M262-only and closed locally. When the robot tail is active, keep
            // Ejector + Robot OUT of it — they form a separate cross-PLC segment, and including them
            // would double-drive Robot.stateRprtCmd_out (Feed ring + cross-hop).
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
                if (MapperConfig.MergeFeedRing)
                {
                    // Connect-at-seam: the Feed tail crosses to the M580 head instead of closing
                    // locally, joining the one cross-PLC ring (Feed head is fed by the discharge segment).
                    var m580Head = contents.Sensors.FirstOrDefault(
                        s => HcfSymbolIndex.NameBasedPlcGuess(s.Name) == PlcAssignment.M580);
                    if (m580Head != null)
                        builder.AddAdapterConnection(
                            $"{ringComponents[^1].Name}.{StateRprtOut(ringComponents[^1].Type)}",
                            $"{m580Head.Name}.{StateRprtIn("Sensor_Bool_CAT")}");
                }
                else
                {
                    // Close the M262 Feed ring locally (last -> first).
                    builder.AddAdapterConnection(
                        $"{ringComponents[^1].Name}.{StateRprtOut(ringComponents[^1].Type)}",
                        $"{ringComponents[0].Name}.{StateRprtIn(ringComponents[0].Type)}");
                }
            }

            // Local intra-M262 links of the Ejector->Robot segment only; the cross-device hops at
            // its ends are added by the HandoffPlanner. robotTail off -> empty -> no-op.
            var m262Seg = TemplateMap.M262CrossRingSegment(robotTail);
            for (int i = 0; i < m262Seg.Count - 1; i++)
                builder.AddAdapterConnection(
                    $"{m262Seg[i]}.stateRprtCmd_out", $"{m262Seg[i + 1]}.stateRprtCmd_in");
        }

        // M580 (Station 2) sibling of BuildFeedStationWiring. Anchors: Station2 (Station_CAT),
        // Station2_HMI, Assembly_Station + Disassembly (both Process1_Generic), Stn2_Term. Contained
        // inside the M580 bucket; M262 + BX1 components are filtered out.
        internal static void BuildStation2Wiring(SyslayBuilder builder, StationContents contents,
            string disassemblyFbName = null)
        {
            const string StationFb     = "Station2";
            const string StationHmiFb  = "Station2_HMI";
            const string AssemblyProc  = "Assembly_Station";
            const string Stn2Term      = "Stn2_Term";

            static bool IsM580(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) == PlcAssignment.M580;

            // Thread Disassembly right after Assembly_Station to keep the syslay and sysres rings in
            // lock-step; without it EAE could re-derive a ring that drops Disassembly. Gated on
            // UnparkDisassembly AND the FB being present, so flag-off is byte-identical.
            bool threadDisassembly =
                MapperConfig.UnparkDisassembly && !string.IsNullOrEmpty(disassemblyFbName);

            // Station2's internal plcStart fires Station2.INITO via INIT; the FB1->Station2.INIT
            // bootstrap is added on the M580 sysres by ResourceWireEmitter.
            var initChain = new List<string> { StationFb };
            foreach (var s in contents.Sensors)
                if (IsM580(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)
                if (IsM580(a.Name)) initChain.Add(a.Name);
            initChain.Add(AssemblyProc);
            if (threadDisassembly) initChain.Add(disassemblyFbName);   // Assembly_Station → Disassembly
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            builder.AddAdapterConnection($"{StationHmiFb}.StationHMIAdptrOUT",
                                         $"{StationFb}.StationHMIAdptrIN");

            // CaS chain skips sensors and any actuator whose resolved CAT lacks stationAdptr. The
            // centre-home swivel CAT does have the port, so Bearing_PnP is wired by its resolved type.
            var stationChain = new List<(string Name, string Type)>();
            foreach (var a in contents.Actuators)
            {
                if (!IsM580(a.Name)) continue;
                var fbType = ResolveActuatorFBType(a);
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

            // M580 component chain (sensors then actuators) EXCLUDING the process — the process
            // closes the ring.
            var m580 = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)
                if (IsM580(s.Name)) m580.Add((s.Name, "Sensor_Bool_CAT"));
            foreach (var a in contents.Actuators)
                if (IsM580(a.Name)) m580.Add((a.Name, ResolveActuatorFBType(a)));

            // The discharge (HandoffPlanner.DischargeActive) splices the M262 segment onto the ring
            // at the Disassembly seam via two cross-device adapter hops EAE bridges, WITHOUT
            // stretching the M580 bearing/shaft/clamp ring. Off -> ring closes locally.
            var ring = new List<(string Name, string Type)>(m580);
            // Cover detour: splice the BX1 covers between Clamp and Assembly_Station via two
            // cross-device hops EAE bridges — a DIFFERENT seam than the discharge segment, so the two
            // compose with only M580<->BX1 and M580<->M262 boundaries, never M262<->BX1. Empty when off.
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
                    if (MapperConfig.MergeFeedRing)
                    {
                        // Connect-at-seam: the discharge-segment tail feeds the M262 Feed head so the
                        // segment + Feed chain form one continuous loop.
                        var m262Head = contents.Sensors.FirstOrDefault(
                            s => HcfSymbolIndex.NameBasedPlcGuess(s.Name) == PlcAssignment.M262);
                        if (m262Head != null)
                            builder.AddAdapterConnection(
                                $"{seg[^1]}.stateRprtCmd_out",
                                $"{m262Head.Name}.{StateRprtIn("Sensor_Bool_CAT")}");
                    }
                    else
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

        // BX1 Soft-dPAC (Cover PnP) sibling. BX1 has no Station_CAT / Process FB / HMI on its own
        // resource — Assembly_Station commands BX1 actuators over the cross-PLC broadcast ring. Three
        // chains: MqttConn bring-up, INIT chain, stateRprtCmd ring. Skipped when BX1 has no components.
        internal static void BuildBx1Wiring(SyslayBuilder builder, StationContents contents,
            MapperConfig? config)
        {
            static bool IsBx1(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) == PlcAssignment.BX1;

            // MqttConn.INITO -> CONNECT self-loop opens the broker connection on init. MqttConn.INIT
            // is sourced from FB1 on the BX1 sysres, so it shows dangling here; runtime resolves it.
            bool mqttEnabled = config != null && config.MqttPublishEnabled;
            // UseTelemetryCat (default) names the connection Telemetry_BX1; the raw-FB revert keeps "MqttConn".
            string bx1Conn = (config != null && config.UseTelemetryCat) ? "Telemetry_BX1" : "MqttConn";
            if (mqttEnabled)
                builder.AddEventConnection($"{bx1Conn}.INITO", $"{bx1Conn}.CONNECT");

            // INIT chain: connection first (broker up before any embedded MqttPub fires), then sensors,
            // then actuators.
            var initChain = new List<string>();
            if (mqttEnabled) initChain.Add(bx1Conn);
            foreach (var s in contents.Sensors)    if (IsBx1(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)  if (IsBx1(a.Name)) initChain.Add(a.Name);
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            // MqttConn has no stateRprtCmd port, so it stays out of the ring.
            var ring = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)    if (IsBx1(s.Name)) ring.Add((s.Name, "Sensor_Bool_CAT"));
            // Cover-detour actuators are on the M580 ring, so keep them out of the BX1 ring to avoid
            // double-wiring. TopCoverSenosr stays a BX1 sensor (INIT-only, off-ring).
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
