using System;
using System.Collections.Generic;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Translation
{
    // Everything one generation decided before any artefact was written: the twin it read, the controller
    // allocation it resolved against, whether the report rings fold into one, which slot the top-cover
    // sensor takes, and how every process-to-process handoff travels.
    //
    // Immutable, and derived once from the Control.xml in hand. It exists because the device emitters run
    // AFTER the layout and do not hold the twin -- they wire a resource from what is already on disk, so a
    // decision like "are the rings merged?" has to travel to them rather than be re-derived. It previously
    // travelled as mutable statics on MapperConfig, which meant a second generation in the same process
    // (MapperUI keeps one) started from the first one's answers, with no error when they were wrong.
    //
    // Published once per run by the layout generator, which is the single place the twin is read.
    public sealed class GenerationPlan
    {
        public IReadOnlyList<VueOneComponent> Components { get; }
        public ControllerAllocation Allocation { get; }

        // The per-controller report rings are folded into one, so every announcement is directly readable
        // by every process. See FeedRingMerge for what makes a twin need it.
        public bool RingsMerged { get; }

        // state_table slot the top-cover sensor reports on; see StateTableAllocation for why it is computed
        // rather than positional.
        public int TopCoverSensorSlot { get; }

        internal Process.Recipes.ProcessHandoffPlan Handoffs { get; }

        private GenerationPlan(IReadOnlyList<VueOneComponent> components, ControllerAllocation allocation,
            bool ringsMerged, int topCoverSensorSlot, Process.Recipes.ProcessHandoffPlan handoffs)
        {
            Components = components;
            Allocation = allocation;
            RingsMerged = ringsMerged;
            TopCoverSensorSlot = topCoverSensorSlot;
            Handoffs = handoffs;
        }

        private static GenerationPlan? _current;

        // The plan the run in progress is rendering. Emitters downstream of the layout read it; asking
        // before a plan is published is a wiring mistake, not a defaultable condition, so it throws.
        public static GenerationPlan Current =>
            _current ?? throw new InvalidOperationException(
                "No GenerationPlan has been published. Planning runs once, in the layout generator, before " +
                "any device emitter; reaching one of them without it means the pipeline was entered out of order.");

        internal static GenerationPlan Publish(
            IReadOnlyList<VueOneComponent> components, StationContents contents,
            Process.Recipes.ProcessHandoffPlan handoffs)
        {
            bool ringsMerged = Process.Recipes.FeedRingMerge.Needed(components);
            var plan = new GenerationPlan(components, ControllerAllocation.Current, ringsMerged,
                StateTableAllocation.TopCoverSensorSlot(contents, ringsMerged), handoffs);
            _current = plan;
            return plan;
        }
    }
}
