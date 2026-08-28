using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation
{
    // The two orders a resource's chains are rendered in. Both come from the ONE declaration per
    // component - layout.yml gives each row an idRank and a casBusRank - and layout.yml says why they
    // must not be normalised into one: idRank assigns every state_table index, casBusRank chains the
    // station adapter. So this is a projection of one graph, never a second graph.
    internal enum ChainOrder
    {
        // The shared application canvas: sensors then actuators, by idRank, every ring drawn whole.
        Application,
        // A resource's own network: sensors and actuators interleaved, by casBusRank, cross-resource
        // hops left open for EAE to bridge from the canvas.
        Resource,
    }

    // One resource's wiring, decided before anything is drawn. Every link is a RESOLVED endpoint pair,
    // so a renderer writes XML and decides nothing: membership, ordering, seams and chain endpoints are
    // all answered here. Both renderers read this - the shared canvas and the resource's own network -
    // which is what stops the two halves of one deploy disagreeing.
    //
    // Nothing here is decided by a station, process or controller NAME. A component joins a chain
    // because of where the roster put it and what its target's capabilities say; a ring ends where the
    // carriers the model selected say it does.
    internal sealed record ResourceWiringPlan(
        PlcAssignment Plc,
        ChainOrder Order,
        // Brought up by this resource's own chain rather than by a declared role elsewhere; it heads the
        // chain, so the broker is open before any publisher on it fires.
        string? SelfStartedConnection,
        // The order things are initialised in. Rendered as INITO -> INIT between consecutive members.
        IReadOnlyList<string> InitChain,
        // A connection and the role that opens it. The canvas states this where the connection is drawn;
        // a resource states it again because EAE runs the resource's event graph, not the canvas.
        IReadOnlyList<(string Source, string Destination)> ConnectionLinks,
        IReadOnlyList<(string Source, string Destination)> AdapterRelations,
        IReadOnlyList<(string Source, string Destination)> StationLinks,
        IReadOnlyList<(string Source, string Destination)> RingLinks,
        // A carried segment's own links, emitted by whichever resource hosts its members.
        IReadOnlyList<(string Source, string Destination)> SegmentLinks,
        // Where this resource's chains are deliberately left open, and why. Diagnostic: a seam is a
        // decision, so the reason it was taken belongs beside the run rather than in a reader's head.
        IReadOnlyList<string> OpenSeams,
        // Members, for the parity check: two documents rendering one graph must agree about who is on it.
        IReadOnlyList<RingMember> StationChain,
        IReadOnlyList<RingMember> RingChain,
        IReadOnlyList<RingMember> Processes);

    // A ring or chain member and the CAT that decides how its ports are spelled.
    internal sealed record RingMember(string Name, string Type);

    internal static class ResourceWiringPlanner
    {
        // Everything the plant emits as a chain member, in the order this document draws it, and the
        // CAT that decides how each one's ports are spelled. ONE derivation: the resource being planned
        // and the ring being asked about read the same roll, so they cannot disagree about membership.
        private sealed record MemberRoll(
            List<string> Members, HashSet<string> Sensors, IReadOnlyDictionary<string, string> CatTypes,
            Mapping.TemplateIndex Manifest)
        {
            public bool IsSensor(string n) => Sensors.Contains(n);

            public RingMember Member(string n) => new(n,
                CatTypes.TryGetValue(n, out var t) ? t
                : IsSensor(n) ? Manifest.SensorType.Name
                : Manifest.ProcessType.Name);
        }

        private static MemberRoll Roll(GenerationContext ctx, ChainOrder order)
        {
            var sensors = ctx.Station.Sensors.Select(Name).Where(n => n.Length > 0).ToList();
            // Reporters this deployment injects that the twin does not declare. They are real emitted
            // FBs with a residency and a ring role, so they are members; what differs is only how each
            // document brings them up, which the init chain states.
            sensors.AddRange(ctx.InjectedReporters);
            var actuators = ctx.Station.Actuators.Select(Name).Where(n => n.Length > 0).ToList();

            return new MemberRoll(
                order == ChainOrder.Resource
                    ? ByCaSBus(ctx, sensors, actuators)
                    : sensors.Concat(actuators).ToList(),
                new HashSet<string>(sensors, StringComparer.OrdinalIgnoreCase),
                ctx.CatTypes, ctx.Manifest);
        }

        public static ResourceWiringPlan For(
            GenerationContext ctx, PlcAssignment plc, ChainOrder order = ChainOrder.Application)
        {
            var resource = ctx.ResourceFor(plc);
            var contents = ctx.Station;
            var allocation = ctx.Allocation;
            var caps = resource.Capabilities;

            var segment = ctx.CrossRingSegment;
            var facts = ctx.Profile.Facts;
            bool resourceOrder = order == ChainOrder.Resource;

            // Where a component's reports circulate: the target that hosts it, unless a relocation
            // moved it off the target that owns its station's ring - then the ring is still the
            // station's. Answered by the one owner of that question.
            // WHICH RESOURCE a member belongs to depends on which document is rendered. On the shared
            // canvas a ring is drawn whole, so membership is the ring's HOST. On a resource's own
            // network only the FBs mirrored there exist, so membership is where the component RUNS. The
            // two coincide until a partial swap relocates a component off the target hosting its ring,
            // which is exactly when a resource must wire what it holds rather than what it drives.
            bool Hosted(string name) =>
                resourceOrder ? allocation.Of(name) == plc : RingHostOf(ctx, name) == plc;

            // --- ordered station members ----------------------------------------------------------
            var roll = Roll(ctx, order);
            var injected = ctx.InjectedReporters;
            var members = roll.Members;
            bool IsSensor(string n) => roll.IsSensor(n);
            RingMember Member(string n) => roll.Member(n);

            // --- bring-up -------------------------------------------------------------------------
            var connection = ConnectionOn(ctx, plc);
            // On the shared canvas a connection nothing else starts heads the chain. On the resource the
            // boot FB heads it and the connection is opened from its declared starter either way.
            string? selfStarted = !resourceOrder && connection is { Started: false } c ? c.Name : null;
            var connectionLinks = new List<(string, string)>();
            if (resourceOrder && connection is { } r)
            {
                var starter = resource.AreaFb ?? resource.StationFb ?? ctx.Targets.InitRole;
                connectionLinks.Add(($"{starter}.INITO", $"{r.Name}.INIT"));
                connectionLinks.Add(($"{r.Name}.INITO", $"{r.Name}.CONNECT"));
            }

            // A member another target COMMANDS inits last, so a stall in its cross-device bring-up
            // cannot hold up the process on this one.
            var tail = ctx.Rings.DischargeTail;
            bool IsTail(string name) => Named(tail, name);

            var init = new List<string>();
            if (resourceOrder) init.Add(ctx.Targets.InitRole);
            if (selfStarted != null) init.Add(selfStarted);
            if (resource.AreaFb != null) init.Add(resource.AreaFb);
            if (resource.StationFb != null) init.Add(resource.StationFb);
            // The canvas brings an injected reporter up as a fan-out beside the emitter that creates it,
            // so threading it into the chain there would drive one INIT twice; its own resource has no
            // such emitter and threads it like any other member.
            bool InChain(string n) =>
                Hosted(n) && (resourceOrder || !Named(injected, n));
            init.AddRange(members.Where(n => InChain(n) && !IsTail(n)));
            init.AddRange(resource.Processes);
            init.AddRange(members.Where(n => InChain(n) && IsTail(n)));

            // --- CaS chain ------------------------------------------------------------------------
            // A CAT with no station adapter dangles on this chain and EAE rejects the resource.
            var processes = resource.Processes
                .Select(p => new RingMember(p, ctx.Manifest.ProcessType.Name)).ToList();
            var station = members.Where(n => !IsSensor(n) && Hosted(n)).Select(Member)
                .Where(m => !ctx.Manifest.LacksStationAdapter(m.Type))
                .ToList();
            station.AddRange(processes);

            var stationLinks = new List<(string, string)>();
            if (station.Count > 0 && resource.StationChain is { } ends &&
                (!resourceOrder || processes.Count > 0))
            {
                stationLinks.Add((ends.From, $"{station[0].Name}.{ctx.Manifest.StationIn(station[0].Type)}"));
                for (int i = 0; i < station.Count - 1; i++)
                    stationLinks.Add(($"{station[i].Name}.{ctx.Manifest.StationOut(station[i].Type)}",
                                      $"{station[i + 1].Name}.{ctx.Manifest.StationIn(station[i + 1].Type)}"));
                stationLinks.Add(($"{station[^1].Name}.{ctx.Manifest.StationOut(station[^1].Type)}", ends.To));
            }

            // --- report ring ----------------------------------------------------------------------
            // On the canvas a chain another target commands is spliced into the ring that commands it,
            // so it is excluded here and added where the seam is opened. On its own resource that chain
            // IS the ring, so only the cross-controller segment - wired separately - is held out.
            var ring = RingOf(ctx, plc, order);

            var seams = new List<string>();
            var ringLinks = resourceOrder
                ? ResourceRingLinks(ring, processes, ctx, caps, segment.Count > 0, seams)
                : ApplicationRingLinks(ring, processes, ctx, plc, segment, seams);

            // --- the carried cross-controller segment ---------------------------------------------
            var segmentLinks = new List<(string, string)>();
            if (segment.Count > 0 && allocation.Of(segment[0]) == plc)
            {
                for (int i = 0; i < segment.Count - 1; i++)
                    segmentLinks.Add(($"{segment[i]}.stateRprtCmd_out", $"{segment[i + 1]}.stateRprtCmd_in"));
                // A merged topology closes the segment into this resource's own ring head rather than
                // handing it across a seam, so on the resource that tail link is local.
                if (resourceOrder && ring.Count > 0 && ctx.Rings.RingsMerged && caps.AnchorsMergedRing)
                {
                    segmentLinks.Add(($"{segment[^1]}.stateRprtCmd_out",
                                      $"{ring[0].Name}.{ctx.Manifest.RingIn(ring[0].Type)}"));
                    seams.Add($"merged-ring seam: {segment[^1]}.stateRprtCmd_out -> {ring[0].Name} " +
                              "(ring head, local); the segment head and the process tail stay open");
                }
                else if (resourceOrder)
                    seams.Add($"cross-controller segment {string.Join("->", segment)}: both ends OPEN, " +
                              "spliced into the commanding ring on the shared canvas");
            }

            return new ResourceWiringPlan(plc, order, selfStarted, init, connectionLinks,
                resource.AdapterRelations, stationLinks, ringLinks, segmentLinks, seams,
                station, ring, processes);
        }

        // The canvas draws every hop, including the cross-device ones: the ring is one ring there.
        private static List<(string, string)> ApplicationRingLinks(
            List<RingMember> ring, List<RingMember> processes, GenerationContext ctx,
            PlcAssignment plc, IReadOnlyList<string> segment, List<string> seams)
        {
            var links = new List<(string, string)>();
            var chain = ring.Concat(processes).ToList();
            for (int i = 0; i < chain.Count - 1; i++)
                links.Add(Link(ctx.Manifest, chain[i], chain[i + 1]));
            if (chain.Count <= 1) return links;

            bool splicesSegment = segment.Count > 0 && ctx.Rings.CarrierOf(segment[0]) == plc;
            var acrossTo = HeadOfNextRing(ctx, plc);
            var from = $"{chain[^1].Name}.{ctx.Manifest.RingOut(chain[^1].Type)}";
            var ownHead = $"{chain[0].Name}.{ctx.Manifest.RingIn(chain[0].Type)}";
            if (splicesSegment)
            {
                links.Add((from, $"{segment[0]}.stateRprtCmd_in"));
                links.Add(($"{segment[^1]}.stateRprtCmd_out", acrossTo ?? ownHead));
                seams.Add($"ring exits into the carried segment at {segment[0]} and returns from {segment[^1]}");
            }
            else links.Add((from, acrossTo ?? ownHead));
            return links;
        }

        // A resource wires only what it holds; a hop to another resource is left open and EAE bridges it
        // from the canvas. Which hops those are is a declared capability, never a controller name.
        private static List<(string, string)> ResourceRingLinks(
            List<RingMember> ring, List<RingMember> processes, GenerationContext ctx,
            ResourceCapabilities caps, bool hasSegment, List<string> seams)
        {
            var links = new List<(string, string)>();
            if (ring.Count == 0) return links;
            for (int i = 0; i < ring.Count - 1; i++) links.Add(Link(ctx.Manifest, ring[i], ring[i + 1]));

            if (processes.Count > 0)
            {
                if (caps.CommandsACarriedChain)
                    seams.Add($"cover detour: {ring[^1].Name} reports across to the carried chain and " +
                              $"{processes[0].Name} is fed back from it — EAE bridges via the canvas");
                else links.Add(Link(ctx.Manifest, ring[^1], processes[0]));

                for (int i = 0; i < processes.Count - 1; i++) links.Add(Link(ctx.Manifest, processes[i], processes[i + 1]));

                // The ring leaves this controller either because the carried chain took it across, or
                // because a merged topology hands it to the next ring host.
                bool openBoundary = (hasSegment && caps.CommandsACarriedChain) ||
                                    (ctx.Rings.RingsMerged && caps.AnchorsMergedRing);
                if (openBoundary)
                    seams.Add($"cross-controller ring: {processes[^1].Name} reports across the seam and " +
                              $"{ring[0].Name} is fed from it — EAE bridges via the canvas");
                else links.Add(Link(ctx.Manifest, processes[^1], ring[0]));
            }
            else if (ring.Count > 1)
            {
                if (caps.CarriesACommandedChain)
                    seams.Add($"carried chain {ring[0].Name}…{ring[^1].Name} is OPEN at both ends: " +
                              "another controller commands it");
                else links.Add(Link(ctx.Manifest, ring[^1], ring[0]));
            }
            return links;
        }

        // WHICH TARGET HOSTS a component's ring. Not where the component RUNS: a partial swap can move
        // it off the target that owns its station's ring, and the ring is still the station's. Written
        // twice in two methods until now, which is how the two could have come to disagree.
        private static PlcAssignment RingHostOf(GenerationContext ctx, string name)
        {
            var on = ctx.Allocation.Of(name);
            return ctx.Targets.IsRegistered(on) ? ctx.Targets.RingHostOf(ctx.Targets.Of(on)) : on;
        }

        private static (string, string) Link(Mapping.TemplateIndex m, RingMember from, RingMember to) =>
            ($"{from.Name}.{m.RingOut(from.Type)}",
             $"{to.Name}.{m.RingIn(to.Type)}");

        // casBusRank order for the members that declare one, then the rest in roster order. A component
        // with no casBusRank is not ON the station bus; it still boots, so it is appended rather than
        // dropped, which is what puts a cross-controller segment at the end of the chain.
        private static List<string> ByCaSBus(
            GenerationContext ctx, IReadOnlyList<string> sensors, IReadOnlyList<string> actuators)
        {
            var all = sensors.Concat(actuators).ToList();
            var ranked = ctx.Roster.CaSBusOrder
                .Where(n => all.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            var seen = new HashSet<string>(ranked, StringComparer.OrdinalIgnoreCase);
            ranked.AddRange(all.Where(n => !seen.Contains(n)));
            return ranked;
        }

        private static string Name(VueOneComponent c) => (c.Name ?? string.Empty).Trim();

        private static bool Named(IReadOnlyList<string> names, string name) =>
            names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        // Who is on ONE target's report ring, in that document's order. The single owner of ring
        // membership: the resource being planned asks for its own, and a merged topology asks for the
        // next host's, so no two callers can disagree about where a ring begins or who is on it.
        internal static List<RingMember> RingOf(
            GenerationContext ctx, PlcAssignment plc, ChainOrder order)
        {
            bool resourceOrder = order == ChainOrder.Resource;
            var contents = ctx.Station;
            var facts = ctx.Profile.Facts;
            var segment = ctx.CrossRingSegment;

            var roll = Roll(ctx, order);
            var members = roll.Members;
            RingMember Member(string n) => roll.Member(n);

            bool Carried(string n) =>
                facts.TakesRingScopedSlot(n) || ctx.IsDetoured(n) || Named(segment, n);
            bool Hosted(string n) => resourceOrder ? ctx.Allocation.Of(n) == plc : RingHostOf(ctx, n) == plc;

            var ring = resourceOrder
                ? members.Where(n => Hosted(n) && !Named(segment, n)).Select(Member).ToList()
                : members.Where(n => Hosted(n) && !Carried(n)).Select(Member).ToList();

            // Asked of the PLAN, not of the declaration: a target that commands a carried chain only
            // splices one onto its ring when this run actually has a chain to carry. Reading the raw
            // relationship here pulled a ring-scoped reporter across on a twin that detours nothing.
            if (!resourceOrder && ctx.Targets.IsRegistered(plc) &&
                ctx.CapabilitiesOf(plc).CommandsACarriedChain)
            {
                var carriedReporter = contents.Sensors.Select(Name)
                    .FirstOrDefault(n => n.Length > 0 && facts.TakesRingScopedSlot(n));
                if (!string.IsNullOrEmpty(carriedReporter)) ring.Add(Member(carriedReporter!));
                ring.AddRange(ctx.DetouredChain.Select(Member));
            }
            return ring;
        }

        // The ring hosts of one report domain, cyclically ordered. A host is a target that actually
        // carries ring members on this run - asked of the ring itself rather than of a capability flag,
        // so a target that hosts none is not a host however it is declared.
        //
        // The order is device.yml's declaration order, which is the one order every target already has.
        internal static List<PlcAssignment> RingHostsSharing(
            GenerationContext ctx, PlcAssignment plc)
        {
            var domain = ctx.Rings.DomainOf(plc);
            return ctx.Targets.All
                .Select(t => t.Plc)
                .Where(t => ctx.Emits(t)
                            && ctx.Rings.DomainOf(t) == domain
                            && RingOf(ctx, t, ChainOrder.Application).Count > 0)
                .ToList();
        }

        // Where this host's tail reports. Hosts that share one domain form ONE cycle, so the tail goes
        // to the NEXT host's head; a domain with a single host closes on itself and this is null. No
        // count is assumed: one host, two, or twenty all fall out of the same cyclic step.
        private static string? HeadOfNextRing(GenerationContext ctx, PlcAssignment plc)
        {
            var next = NextInCycle(RingHostsSharing(ctx, plc), plc);
            if (next == null) return null;

            var head = RingOf(ctx, next.Value, ChainOrder.Application).FirstOrDefault();
            return head == null ? null : $"{head.Name}.{ctx.Manifest.RingIn(head.Type)}";
        }

        // The member after this one, cyclically. Null where there is no OTHER member - a lone host has
        // nothing to hand its tail to and closes on itself. Deliberately arithmetic rather than a case
        // per count: one, two or twenty hosts all form exactly one cycle through this.
        internal static T? NextInCycle<T>(IReadOnlyList<T> cycle, T member) where T : struct
        {
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < cycle.Count; i++)
                if (comparer.Equals(cycle[i], member))
                    return cycle.Count < 2 ? null : cycle[(i + 1) % cycle.Count];
            return null;
        }

        // The MQTT connection this resource hosts, and whether anything else brings it up.
        private static (string Name, bool Started)? ConnectionOn(GenerationContext ctx, PlcAssignment plc)
        {
            if (!ctx.Cfg.Telemetry.PublishEnabled) return null;
            var declared = ctx.Cfg.Telemetry.Connections
                .FirstOrDefault(c => c.Plc == plc);
            return declared == null
                ? null
                : (declared.NameFor(ctx.Cfg.Telemetry.UseTelemetryCat),
                   declared.BroughtUpBy != Configuration.ConnectionStarter.None);
        }
    }
}
