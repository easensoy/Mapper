using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.RevPi;
using CodeGen.Mapping;

namespace CodeGen.Validation.Plan
{
    // Pre-generation guard on the RevPi SELECTION itself, so an unsupportable choice fails before any
    // artefact is written rather than shipping a project whose actuators cannot actuate.
    //
    // The Revolution Pi carries its Feed IO over one Modbus word pair through PLC_RW_REVPI, whose
    // interface exposes exactly ExtendPusher/ExtendChecker (coils) and PusherAtWork/PusherAtHome/
    // checkerUp/chekcerDown/Hopper (sensors) — i.e. Feeder, Checker and PartInHopper. That set is a
    // strict SUBSET of the Feed station: Transfer, Ejector, Robot and PartAtAssembly hold real M262
    // channels today and the coupler has no signals for them.
    //
    // So the supported RevPi mode is the PER-COMPONENT swap (Feeder and/or Checker move, M262 keeps the
    // rest — a four-controller project). The whole-Feed swap is rejected: it would relocate components
    // the coupler cannot serve, and they would deploy with no physical IO at all.
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

            // Every relocated component must be one the coupler actually serves: PLC_RW_REVPI exposes IO
            // for a fixed set, and a component moved off the M262 that owns its channels deploys with none.
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
