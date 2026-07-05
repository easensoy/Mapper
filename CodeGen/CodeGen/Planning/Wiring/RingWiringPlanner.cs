using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using static CodeGen.Translation.SystemInjector;

namespace CodeGen.Translation
{
    // Per-station syslay wiring (INIT chain, stationAdptr CaS chain, stateRprtCmd report ring) for Feed/Station2/BX1.
    // Process1_Generic has NO data/event outputs except INITO, so the stateRprtCmd ring is the ONLY command path.
    internal static class RingWiringPlanner
    {
        static bool NameEq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        internal static void BuildFeedStationWiring(SyslayBuilder builder, StationContents contents)
        {
            // Same resolver as GenerateFeedStationSyslayToPath, so the wire endpoints match the emitted instance name.
            var processInstanceName = InstanceNameResolver.Resolve(contents.Process);
            if (string.IsNullOrWhiteSpace(processInstanceName)) processInstanceName = "Process1";

            // Per-PLC filter: EAE renders a resource-boundary-crossing wire as dashed/unresolved and blocks deploy; each PLC's sysres is wired separately.
            // The Feed station runs on the M262 OR the RevPi controller (byte-identical for M262 — nothing guesses RevPi there).
            static bool OnFeedController(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) is PlcAssignment.M262 or PlcAssignment.RevPi;

            // Keep the robot-tail (Ejector+Robot) OUT of the INIT path to Feed_Station (a Robot bring-up stall would block it); init the tail last, mirrored in ResourceWireEmitter.
            bool robotTail = MapperConfig.EnableRobotTaskTail &&
                contents.Actuators.Any(a => NameEq(a.Name, "Ejector")) &&
                contents.Actuators.Any(a => NameEq(a.Name, "Robot"));
            bool IsRobotTailName(string name) =>
                robotTail && (NameEq(name, "Ejector") || NameEq(name, "Robot"));

            var initChain = new List<string>();
            initChain.Add("Area");
            initChain.Add("Station1");
            foreach (var s in contents.Sensors)
                if (OnFeedController(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)
                if (OnFeedController(a.Name) && !IsRobotTailName(a.Name)) initChain.Add(a.Name);
            initChain.Add(processInstanceName);
            foreach (var a in contents.Actuators)
                if (OnFeedController(a.Name) && IsRobotTailName(a.Name)) initChain.Add(a.Name);

            // Area_CAT's internal plcStart fires Area.INITO via INIT, propagating through this chain.
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            builder.AddAdapterConnection("Area_HMI.AreaHMIAdptrOUT", "Area.AreaHMIAdptrIN");
            builder.AddAdapterConnection("Station1_HMI.StationHMIAdptrOUT", "Station1.StationHMIAdptrIN");
            builder.AddAdapterConnection("Area.AreaAdptrOUT", "Station1.AreaAdptrIN");
            builder.AddAdapterConnection("Station1.AreaAdptrOUT", "Area_Term.CasAdptrIN");

            // CaS chain skips any CAT lacking stationAdptr (sensors, Seven_State); a dangling stationAdptr makes EAE reject the resource. sysres+syslay must match.
            var stationChain = new List<(string Name, string Type)>();
            foreach (var a in contents.Actuators)
            {
                if (!OnFeedController(a.Name)) continue;
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

            // Report ring is M262-only, closed locally; the robot tail is kept out (its separate cross-PLC segment would double-drive Robot.stateRprtCmd_out).
            var ringComponents = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)
                if (OnFeedController(s.Name)) ringComponents.Add((s.Name, "Sensor_Bool_CAT"));
            foreach (var a in contents.Actuators)
                if (OnFeedController(a.Name) &&
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
                    // MergeFeedRing: the Feed tail crosses to the M580 head instead of closing locally, joining the one cross-PLC ring.
                    var m580Head = contents.Sensors.FirstOrDefault(
                        s => HcfSymbolIndex.NameBasedPlcGuess(s.Name) == PlcAssignment.M580);
                    if (m580Head != null)
                        builder.AddAdapterConnection(
                            $"{ringComponents[^1].Name}.{StateRprtOut(ringComponents[^1].Type)}",
                            $"{m580Head.Name}.{StateRprtIn("Sensor_Bool_CAT")}");
                }
                else
                {
                    builder.AddAdapterConnection(
                        $"{ringComponents[^1].Name}.{StateRprtOut(ringComponents[^1].Type)}",
                        $"{ringComponents[0].Name}.{StateRprtIn(ringComponents[0].Type)}");
                }
            }

            // Local intra-M262 Ejector->Robot links only; the cross-device hops at its ends come from HandoffPlanner. Empty when robotTail off.
            var m262Seg = TemplateMap.M262CrossRingSegment(robotTail);
            for (int i = 0; i < m262Seg.Count - 1; i++)
                builder.AddAdapterConnection(
                    $"{m262Seg[i]}.stateRprtCmd_out", $"{m262Seg[i + 1]}.stateRprtCmd_in");
        }

        // M580 (Station 2) sibling of BuildFeedStationWiring; contained to the M580 bucket, M262+BX1 filtered out.
        internal static void BuildStation2Wiring(SyslayBuilder builder, StationContents contents,
            string? disassemblyFbName = null)
        {
            const string StationFb     = "Station2";
            const string StationHmiFb  = "Station2_HMI";
            const string AssemblyProc  = "Assembly_Station";
            const string Stn2Term      = "Stn2_Term";

            static bool IsM580(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) == PlcAssignment.M580;

            // Thread Disassembly after Assembly_Station to keep the syslay+sysres rings in lock-step (else EAE re-derives a ring dropping it); flag-off is byte-identical.
            bool threadDisassembly =
                MapperConfig.UnparkDisassembly && !string.IsNullOrEmpty(disassemblyFbName);

            // Station2's internal plcStart fires Station2.INITO; the FB1->Station2.INIT bootstrap is on the M580 sysres (ResourceWireEmitter).
            var initChain = new List<string> { StationFb };
            foreach (var s in contents.Sensors)
                if (IsM580(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)
                if (IsM580(a.Name)) initChain.Add(a.Name);
            initChain.Add(AssemblyProc);
            if (threadDisassembly) initChain.Add(disassemblyFbName!);   // Assembly_Station → Disassembly
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
            if (threadDisassembly)
                stationChain.Add((disassemblyFbName!, "Process1_Generic"));
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

            // M580 sensors then actuators, EXCLUDING the process (the process closes the ring).
            var m580 = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)
                if (IsM580(s.Name)) m580.Add((s.Name, "Sensor_Bool_CAT"));
            foreach (var a in contents.Actuators)
                if (IsM580(a.Name)) m580.Add((a.Name, ResolveActuatorFBType(a)));

            // Discharge (DischargeActive) splices the M262 segment at the Disassembly seam via two EAE-bridged cross-device hops, without stretching the M580 ring. Off -> ring closes locally.
            var ring = new List<(string Name, string Type)>(m580);
            // Cover detour splices the BX1 covers between Clamp and Assembly_Station at a DIFFERENT seam, so the two compose with only M580<->BX1 and M580<->M262 boundaries, never M262<->BX1. Empty when off.
            foreach (var cover in HandoffPlanner.CoverDetour)
                ring.Add((cover, "Five_State_Actuator_CAT"));
            ring.Add((AssemblyProc, "Process1_Generic"));
            if (threadDisassembly)
                ring.Add((disassemblyFbName!, "Process1_Generic"));
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
                        // MergeFeedRing: the discharge-segment tail feeds the Feed head, so segment + Feed form one loop.
                        var m262Head = contents.Sensors.FirstOrDefault(
                            s => HcfSymbolIndex.NameBasedPlcGuess(s.Name) is PlcAssignment.M262 or PlcAssignment.RevPi);
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

        // BX1 (Cover PnP) sibling: no Process FB of its own — Assembly_Station commands it over the cross-PLC ring. Three chains: MqttConn bring-up, INIT, stateRprtCmd ring.
        internal static void BuildBx1Wiring(SyslayBuilder builder, StationContents contents,
            MapperConfig? config)
        {
            static bool IsBx1(string name) =>
                HcfSymbolIndex.NameBasedPlcGuess(name) == PlcAssignment.BX1;

            // INITO->CONNECT self-loop opens the broker on init; MqttConn.INIT is sourced from FB1 on the sysres, so it shows dangling here (runtime resolves it).
            bool mqttEnabled = config != null && config.MqttPublishEnabled;
            // UseTelemetryCat (default) names the connection Telemetry_BX1; the raw-FB revert keeps "MqttConn".
            string bx1Conn = (config != null && config.UseTelemetryCat) ? "Telemetry_BX1" : "MqttConn";
            if (mqttEnabled)
                builder.AddEventConnection($"{bx1Conn}.INITO", $"{bx1Conn}.CONNECT");

            // INIT chain: connection first (broker up before any embedded MqttPub fires), then sensors, then actuators.
            var initChain = new List<string>();
            if (mqttEnabled) initChain.Add(bx1Conn);
            foreach (var s in contents.Sensors)    if (IsBx1(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)  if (IsBx1(a.Name)) initChain.Add(a.Name);
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            // MqttConn has no stateRprtCmd port, so it stays out of the ring.
            var ring = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)    if (IsBx1(s.Name)) ring.Add((s.Name, "Sensor_Bool_CAT"));
            // Cover-detour actuators are on the M580 ring, so keep them off the BX1 ring (TopCoverSenosr stays a BX1 sensor, off-ring).
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
