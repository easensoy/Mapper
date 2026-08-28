using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Devices.RevPi;
using CodeGen.Configuration;
using CodeGen.Mapping;

namespace CodeGen.Validation.Plan
{
    // Pre-generation guard on the RevPi SELECTION, so an unsupportable choice fails before any artefact is
    // written rather than shipping a project whose actuators cannot actuate.
    //
    // PLC_RW_REVPI carries the Feed IO over one Modbus word pair and serves a strict SUBSET of the Feed
    // station, so the supported mode is the PER-COMPONENT swap; a whole-Feed swap is rejected.
    public static class RevPiSelectionValidator
    {
        // Thrown so generation stops loudly instead of emitting a half-supportable RevPi project.
        public sealed class InvalidRevPiSelectionException : InvalidOperationException
        {
            public InvalidRevPiSelectionException(string message) : base(message) { }
        }

        // ioBearing: the selected names that actually need a physical channel. A process needs none, so
        // hosting one here is a placement decision rather than an IO one and the coupler has no say.
        public static IReadOnlyList<string> Validate(Configuration.CompilerConfiguration cfg,
            DeploymentProfile profile, IReadOnlyCollection<string> ioBearing)
        {
            var problems = new List<string>();
            var covered = RevPiIoBrokerInjector.CoveredComponents(cfg);

            // A component moved off the M262 that owns its channels deploys with no IO unless the coupler serves it.
            if (profile.HasAssignments)
            {
                var uncovered = ioBearing
                    .Where(c => !covered.Contains(c))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
                if (uncovered.Count > 0)
                    problems.Add(
                        $"These components were routed to the RevPi but the Modbus coupler carries no IO for them: " +
                        $"[{string.Join(", ", uncovered)}]. PLC_RW_REVPI serves only " +
                        $"[{string.Join(", ", covered.OrderBy(n => n, StringComparer.Ordinal))}]; anything else would " +
                        "deploy on the RevPi with no physical IO and could never actuate.");
            }

            return problems;
        }

        // Convenience for call sites that want the selection to be fatal (the generation pipeline).
        public static void ThrowIfInvalid(Configuration.CompilerConfiguration cfg,
            DeploymentProfile profile, IReadOnlyCollection<string> ioBearing)
        {
            var problems = Validate(cfg, profile, ioBearing);
            if (problems.Count > 0)
                throw new InvalidRevPiSelectionException(
                    "Invalid RevPi selection:" + Environment.NewLine + " - " +
                    string.Join(Environment.NewLine + " - ", problems));
        }
    }
}
