using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // Model-level validation: the checks that need the semantic model rather than the emitted files.
    //
    // These fail the generation rather than warn. A monitoring panel that silently drops a component
    // is worse than one that refuses to build, because the operator cannot tell the difference between
    // "this machine has no such part" and "the generator lost it".
    internal static class HmiModelValidator
    {
        internal static IReadOnlyList<string> Validate(HmiPlant plant, HmiPlan plan)
        {
            var problems = new List<string>();

            var placedTags = new HashSet<string>(
                plan.Screens.SelectMany(s => s.Items).Select(i => i.TagName), StringComparer.Ordinal);
            var placedNames = new HashSet<string>(
                plan.Screens.SelectMany(s => s.Items).Select(i => i.Name), StringComparer.Ordinal);

            // 1. Every twin-declared component that this deployment emitted must be reachable on some
            //    screen. Infrastructure (couplers, terminators) is exempt: it is not a twin component.
            foreach (var c in plant.Components)
                if (!placedTags.Contains(c.TagName))
                    problems.Add($"'{c.InstanceName}' ({c.CatType}, tag {c.TagName}) is emitted in the syslay but " +
                                 "appears on no generated screen.");

            foreach (var p in plant.Processes)
                if (!placedTags.Contains(p.TagName))
                    problems.Add($"process '{p.InstanceName}' (tag {p.TagName}) appears on no generated screen.");

            // 2. Nothing may be placed that the model does not know about - that is how a stale screen
            //    survives a component being removed from the twin.
            var known = new HashSet<string>(
                plant.Components.Select(c => c.TagName)
                    .Concat(plant.Processes.Select(p => p.TagName))
                    .Concat(plant.Stations.Select(s => s.TagName)),
                StringComparer.Ordinal);
            foreach (var tag in placedTags.Where(t => !known.Contains(t)))
                problems.Add($"a screen places TagName {tag}, which is not in the semantic model (stale placement).");

            // 3. Read-only means no placed symbol may declare a controller output, whatever its
            //    capability says. Belt and braces with the file-level check.
            if (plan.ReadOnly)
                foreach (var item in plan.Screens.SelectMany(s => s.Items).Where(i => i.Symbol.CommandCapable))
                    problems.Add($"READ-ONLY VIOLATION: '{item.Name}' places command-capable symbol " +
                                 $"'{item.Symbol.Name}' ({item.Symbol.Outputs}).");

            // 4. A supported command capability must carry both an output event and the feedback that
            //    proves acceptance. A capability that claims support without them is a generator bug.
            foreach (var (owner, cap) in AllCapabilities(plant))
            {
                if (!cap.Supported) continue;
                if (cap.Purpose is HmiCapabilityPurpose.Monitor or HmiCapabilityPurpose.InterlockDiagnostics) continue;

                if (string.IsNullOrEmpty(cap.OutputEvent))
                    problems.Add($"'{owner}' reports {cap.Purpose} supported with no output event.");
                if (cap.RequiredFeedback.Count == 0)
                    problems.Add($"'{owner}' reports {cap.Purpose} supported with no accepted-state feedback.");
            }

            // 5. Duplicate field names inside one screen would not compile.
            foreach (var s in plan.Screens)
            {
                var names = s.Items.Select(i => i.Name)
                    .Concat(s.Buttons.Select(b => b.Name))
                    .Concat(s.Captions.Select(c => c.Name));
                foreach (var dup in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
                    problems.Add($"{s.Name}: duplicate control name '{dup.Key}'.");
            }

            _ = placedNames;
            return problems;
        }

        private static IEnumerable<(string Owner, HmiCapability Cap)> AllCapabilities(HmiPlant plant) =>
            plant.Stations.SelectMany(s => s.Capabilities.Select(c => (s.InstanceName, c)))
                .Concat(plant.Processes.SelectMany(p => p.Capabilities.Select(c => (p.InstanceName, c))))
                .Concat(plant.Components.SelectMany(x => x.Capabilities.Select(c => (x.InstanceName, c))));
    }
}
