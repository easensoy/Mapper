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

        public static IReadOnlyList<string> Validate(DeploymentProfile profile)
        {
            var problems = new List<string>();
            var covered = RevPiIoBrokerInjector.CoveredComponents;

            // A component moved off the M262 that owns its channels deploys with no IO unless the coupler serves it.
            if (profile.PartialRevPi)
            {
                var uncovered = profile.RevPiComponents
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
        public static void ThrowIfInvalid(DeploymentProfile profile)
        {
            var problems = Validate(profile);
            if (problems.Count > 0)
                throw new InvalidRevPiSelectionException(
                    "Invalid RevPi selection:" + Environment.NewLine + " - " +
                    string.Join(Environment.NewLine + " - ", problems));
        }
    }
}
