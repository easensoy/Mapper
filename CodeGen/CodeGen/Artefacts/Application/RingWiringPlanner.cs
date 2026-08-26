namespace CodeGen.Translation
{
    // Renders one resource's planned wiring onto the shared canvas: bring-up chain, stationAdptr CaS
    // chain, stateRprtCmd report ring. Process1_Generic has no outputs but INITO, so the report ring is
    // the ONLY command path (INVARIANTS I-14).
    //
    // Every membership, ordering and seam question was answered by ResourceWiringPlanner, which hands
    // over resolved endpoint pairs. This writes them down, so a resource is wired the same way whatever
    // target it is and the canvas cannot disagree with the resource about the topology.
    internal static class RingWiringPlanner
    {
        internal static void Render(SyslayBuilder builder, ResourceWiringPlan plan)
        {
            // Its INIT is sourced on the resource, so it looks dangling on this canvas; the connection
            // still heads the chain so the broker is open before any publisher on it fires.
            if (plan.SelfStartedConnection is { } conn)
                builder.AddEventConnection($"{conn}.INITO", $"{conn}.CONNECT");

            for (int i = 0; i < plan.InitChain.Count - 1; i++)
                builder.AddEventConnection($"{plan.InitChain[i]}.INITO", $"{plan.InitChain[i + 1]}.INIT");

            // The same planned relations the resource renders, so the two halves cannot drift.
            foreach (var (source, destination) in plan.AdapterRelations)
                builder.AddAdapterConnection(source, destination);
            foreach (var (source, destination) in plan.StationLinks)
                builder.AddAdapterConnection(source, destination);
            foreach (var (source, destination) in plan.RingLinks)
                builder.AddAdapterConnection(source, destination);
            foreach (var (source, destination) in plan.SegmentLinks)
                builder.AddAdapterConnection(source, destination);
        }
    }
}
