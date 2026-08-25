using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using static CodeGen.Translation.SystemInjector;
using CodeGen.Mapping;

namespace CodeGen.Translation
{
    // Per-station syslay wiring: INIT chain, stationAdptr CaS chain, stateRprtCmd report ring.
    // Process1_Generic has no outputs but INITO, so the stateRprtCmd ring is the ONLY command path.
    internal static class RingWiringPlanner
    {
        static bool NameEq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Each canvas builds its own chain membership and ORDER; that order is load-bearing.
        static void ThreadStationChain(SyslayBuilder builder,
            List<(string Name, string Type)> chain, (string From, string To)? ends)
        {
            if (chain.Count == 0 || ends == null) return;
            builder.AddAdapterConnection(ends.Value.From,
                $"{chain[0].Name}.{TemplateManifest.StationIn(chain[0].Type)}");
            for (int i = 0; i < chain.Count - 1; i++)
                builder.AddAdapterConnection(
                    $"{chain[i].Name}.{TemplateManifest.StationOut(chain[i].Type)}",
                    $"{chain[i + 1].Name}.{TemplateManifest.StationIn(chain[i + 1].Type)}");
            builder.AddAdapterConnection(
                $"{chain[^1].Name}.{TemplateManifest.StationOut(chain[^1].Type)}", ends.Value.To);
        }

        internal static void BuildFeedStationWiring(SyslayBuilder builder, GenerationContext ctx,
            IReadOnlyList<string> procFbs)
        {
            var contents = ctx.Station;

            // Per-PLC filter: EAE renders a resource-boundary-crossing wire as unresolved and blocks deploy.
            var allocation = ctx.Allocation;
            bool OnFeedController(string name) => allocation.IsFeedSide(name);

            // Keep the discharge tail out of the INIT path; a stall in its cross-device bring-up blocks the process.
            var tail = ctx.Rings.DischargeTail;
            bool IsRobotTailName(string name) => tail.Any(n => NameEq(n, name));

            // The segment is driven by the splice, so its members stay off the locally-closed ring or their
            // report input gains a second source. See Docs/PATCH_RATIONALES P-7.
            var crossSegment = ctx.CrossRingSegment;
            bool OnCrossSegment(string name) =>
                crossSegment.Any(n => NameEq(n, name));

            var feed = ctx.ResourceFor(PlcAssignment.M262);
            var initChain = new List<string>();
            if (feed.AreaFb != null) initChain.Add(feed.AreaFb);
            if (feed.StationFb != null) initChain.Add(feed.StationFb);
            foreach (var s in contents.Sensors)
                if (OnFeedController(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)
                if (OnFeedController(a.Name) && !IsRobotTailName(a.Name)) initChain.Add(a.Name);
            initChain.AddRange(procFbs);
            foreach (var a in contents.Actuators)
                if (OnFeedController(a.Name) && IsRobotTailName(a.Name)) initChain.Add(a.Name);

            // Area_CAT's internal plcStart fires Area.INITO via INIT, propagating through this chain.
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            // The same planned relations the sysres renders, so the two halves cannot drift.
            foreach (var (source, destination) in feed.AdapterRelations)
                builder.AddAdapterConnection(source, destination);

            // Skip any CAT lacking stationAdptr; a dangling one makes EAE reject the resource. sysres must match.
            var stationChain = new List<(string Name, string Type)>();
            foreach (var a in contents.Actuators)
            {
                if (!OnFeedController(a.Name)) continue;
                var fbType = ctx.CatTypes[a.Name.Trim()];
                if (TemplateMap.LacksStationAdapter(fbType)) continue;
                stationChain.Add((a.Name, fbType));
            }
            foreach (var proc in procFbs) stationChain.Add((proc, TemplateManifest.ProcessType.Name));

            ThreadStationChain(builder, stationChain, feed.StationChain);

            var ringComponents = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)
                if (OnFeedController(s.Name) && !OnCrossSegment(s.Name))
                    ringComponents.Add((s.Name, TemplateManifest.SensorType.Name));
            foreach (var a in contents.Actuators)
                if (OnFeedController(a.Name) && !OnCrossSegment(a.Name))
                    ringComponents.Add((a.Name, ctx.CatTypes[a.Name.Trim()]));
            foreach (var proc in procFbs) ringComponents.Add((proc, TemplateManifest.ProcessType.Name));

            if (ringComponents.Count > 1)
            {
                for (int i = 0; i < ringComponents.Count - 1; i++)
                    builder.AddAdapterConnection(
                        $"{ringComponents[i].Name}.{TemplateManifest.RingOut(ringComponents[i].Type)}",
                        $"{ringComponents[i + 1].Name}.{TemplateManifest.RingIn(ringComponents[i + 1].Type)}");
                if (ctx.Rings.RingsMerged)
                {
                    // Merged: the Feed tail crosses to the M580 head instead of closing locally.
                    var m580Head = contents.Sensors.FirstOrDefault(
                        s => allocation.IsOn(s.Name, PlcAssignment.M580));
                    if (m580Head != null)
                        builder.AddAdapterConnection(
                            $"{ringComponents[^1].Name}.{TemplateManifest.RingOut(ringComponents[^1].Type)}",
                            $"{m580Head.Name}.{TemplateManifest.RingIn(ctx.CatTypes.GetValueOrDefault(m580Head.Name.Trim()))}");
                }
                else
                {
                    builder.AddAdapterConnection(
                        $"{ringComponents[^1].Name}.{TemplateManifest.RingOut(ringComponents[^1].Type)}",
                        $"{ringComponents[0].Name}.{TemplateManifest.RingIn(ringComponents[0].Type)}");
                }
            }

            // Local links only; the cross-device hops at the segment ends are spliced by the transport plan.
            var m262Seg = ctx.CrossRingSegment;
            for (int i = 0; i < m262Seg.Count - 1; i++)
                builder.AddAdapterConnection(
                    $"{m262Seg[i]}.stateRprtCmd_out", $"{m262Seg[i + 1]}.stateRprtCmd_in");
        }

        internal static void BuildStation2Wiring(SyslayBuilder builder, GenerationContext ctx,
            IReadOnlyList<string> procFbs)
        {
            var station2 = ctx.ResourceFor(PlcAssignment.M580);
            var StationFb = station2.StationFb!;

            var allocation = ctx.Allocation;
            var contents = ctx.Station;
            bool IsM580(string name) => allocation.IsOn(name, PlcAssignment.M580);

            // Thread EVERY Station-2 process: the sysres wires every Process FB it finds and no validator
            // inspects connections, so omitting one diverges silently. FB1->Station2.INIT lives on the sysres.
            var initChain = new List<string> { StationFb };
            foreach (var s in contents.Sensors)
                if (IsM580(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)
                if (IsM580(a.Name)) initChain.Add(a.Name);
            initChain.AddRange(procFbs);
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            foreach (var (source, destination) in station2.AdapterRelations)
                builder.AddAdapterConnection(source, destination);

            var stationChain = new List<(string Name, string Type)>();
            foreach (var a in contents.Actuators)
            {
                if (!IsM580(a.Name)) continue;
                var fbType = ctx.CatTypes[a.Name.Trim()];
                if (TemplateMap.LacksStationAdapter(fbType))
                    continue;
                stationChain.Add((a.Name, fbType));
            }
            foreach (var proc in procFbs) stationChain.Add((proc, TemplateManifest.ProcessType.Name));
            ThreadStationChain(builder, stationChain, station2.StationChain);

            var m580 = new List<(string Name, string Type)>();
            foreach (var s in contents.Sensors)
                if (IsM580(s.Name)) m580.Add((s.Name, TemplateManifest.SensorType.Name));
            foreach (var a in contents.Actuators)
                if (IsM580(a.Name)) m580.Add((a.Name, ctx.CatTypes[a.Name.Trim()]));

            // The discharge segment splices at the Disassembly seam via two EAE-bridged cross-device hops.
            var ring = new List<(string Name, string Type)>(m580);
            // Splice the cover-presence sensor just before the cover actuators, mirroring the sysres CaSBusOrder.
            // Resolve its instance name from the twin: the spelling varies, and a rename must not dangle.
            var topCoverName = contents.Sensors
                .Select(s => (s.Name ?? string.Empty).Trim())
                .FirstOrDefault(Configuration.RigCatalog.Current.Roles.IsTopCover);
            if (!string.IsNullOrEmpty(topCoverName))
                ring.Add((topCoverName!, TemplateManifest.SensorType.Name));
            // The cover detour uses a DIFFERENT seam, so the two never compose into an M262<->BX1 boundary.
            foreach (var cover in ctx.DetouredChain)
                ring.Add((cover, ctx.CatTypes[cover.Trim()]));
            foreach (var proc in procFbs) ring.Add((proc, TemplateManifest.ProcessType.Name));
            if (ring.Count > 1)
            {
                for (int i = 0; i < ring.Count - 1; i++)
                    builder.AddAdapterConnection(
                        $"{ring[i].Name}.{TemplateManifest.RingOut(ring[i].Type)}",
                        $"{ring[i + 1].Name}.{TemplateManifest.RingIn(ring[i + 1].Type)}");
                var seg = ctx.CrossRingSegment;
                if (seg.Count > 0)
                {
                    builder.AddAdapterConnection(
                        $"{ring[^1].Name}.{TemplateManifest.RingOut(ring[^1].Type)}",
                        $"{seg[0]}.stateRprtCmd_in");
                    if (ctx.Rings.RingsMerged)
                    {
                        var m262Head = contents.Sensors.FirstOrDefault(s => allocation.IsFeedSide(s.Name));
                        if (m262Head != null)
                            builder.AddAdapterConnection(
                                $"{seg[^1]}.stateRprtCmd_out",
                                $"{m262Head.Name}.{TemplateManifest.RingIn(ctx.CatTypes.GetValueOrDefault(m262Head.Name.Trim()))}");
                    }
                    else
                        builder.AddAdapterConnection(
                            $"{seg[^1]}.stateRprtCmd_out",
                            $"{ring[0].Name}.{TemplateManifest.RingIn(ring[0].Type)}");
                }
                else
                {
                    builder.AddAdapterConnection(
                        $"{ring[^1].Name}.{TemplateManifest.RingOut(ring[^1].Type)}",
                        $"{ring[0].Name}.{TemplateManifest.RingIn(ring[0].Type)}");
                }
            }
        }

        // BX1 has no Process FB of its own; Assembly_Station commands it over the cross-PLC ring.
        internal static void BuildBx1Wiring(SyslayBuilder builder, GenerationContext ctx,
            IReadOnlyList<string> procFbs)
        {
            var allocation = ctx.Allocation;
            var contents = ctx.Station;
            bool IsBx1(string name) => allocation.IsOn(name, PlcAssignment.BX1);

            // The connection's INIT is sourced from FB1 on the sysres, so it looks dangling on this canvas.
            bool mqttEnabled = ctx.Config.MqttPublishEnabled;
            string bx1Conn = Configuration.TelemetrySettings.Current
                .For(PlcAssignment.BX1).NameFor(ctx.Config.UseTelemetryCat);
            if (mqttEnabled)
                builder.AddEventConnection($"{bx1Conn}.INITO", $"{bx1Conn}.CONNECT");

            // Connection first, so the broker is up before any embedded MqttPub fires.
            var initChain = new List<string>();
            if (mqttEnabled) initChain.Add(bx1Conn);
            foreach (var s in contents.Sensors)    if (IsBx1(s.Name)) initChain.Add(s.Name);
            foreach (var a in contents.Actuators)  if (IsBx1(a.Name)) initChain.Add(a.Name);
            // A process on this resource inits after the components it drives, as on every other one.
            initChain.AddRange(procFbs);
            for (int i = 0; i < initChain.Count - 1; i++)
                builder.AddEventConnection($"{initChain[i]}.INITO", $"{initChain[i + 1]}.INIT");

            // The connection has no stateRprtCmd port, so it stays out of the ring.
            var ring = new List<(string Name, string Type)>();
            // The cover sensor rides the M580 ring, so keep it off this one or it is double-driven.
            foreach (var s in contents.Sensors)
                if (IsBx1(s.Name) && !(
                                       Configuration.RigCatalog.Current.Roles.IsTopCover(s.Name)))
                    ring.Add((s.Name, TemplateManifest.SensorType.Name));
            foreach (var a in contents.Actuators)
                if (IsBx1(a.Name) && !ctx.IsDetoured(a.Name))
                    ring.Add((a.Name, ctx.CatTypes[a.Name.Trim()]));

            // and its process, so a resource that runs one closes its ring through it.
            foreach (var proc in procFbs) ring.Add((proc, TemplateManifest.ProcessType.Name));

            if (ring.Count > 1)
            {
                for (int i = 0; i < ring.Count - 1; i++)
                    builder.AddAdapterConnection(
                        $"{ring[i].Name}.{TemplateManifest.RingOut(ring[i].Type)}",
                        $"{ring[i + 1].Name}.{TemplateManifest.RingIn(ring[i + 1].Type)}");
                builder.AddAdapterConnection(
                    $"{ring[^1].Name}.{TemplateManifest.RingOut(ring[^1].Type)}",
                    $"{ring[0].Name}.{TemplateManifest.RingIn(ring[0].Type)}");
            }
        }
    }
}
