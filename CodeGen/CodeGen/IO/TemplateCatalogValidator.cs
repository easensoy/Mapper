using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Configuration
{
    // The manifest decides what is deployed, what is instantiated, which CAT a twin's state graph
    // selects and which ports a resource is wired through, so a bad row does not misreport - it emits a
    // project EAE rejects, or one wired to a port that does not exist. Every such row is refused here,
    // at load, which is before any plan and therefore before anything is written.
    internal static class TemplateCatalogValidator
    {
        // rig is handed in: this rule spans TWO declaration files, so it can only be checked where
        // both are known - which is the one place that loads them.
        public static void Validate(TemplateCatalog c, RigCatalog rig)
        {
            var errors = new List<string>();

            if (c.Templates.Count == 0)
                errors.Add("declares no templates, so the Mapper owns no FB type at all");

            foreach (var g in c.Templates.GroupBy(t => t.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
                errors.Add($"template '{g.Key}' is declared {g.Count()} times");

            foreach (var t in c.Templates)
            {
                if (string.IsNullOrWhiteSpace(t.Name)) { errors.Add("a template row has no name"); continue; }
                if (t.DeployLast && !t.Deploy)
                    errors.Add($"'{t.Name}' is deployLast but not deployed, so it is never extracted at all");
                if (t.Emitted && !t.MirrorToSysres)
                    errors.Add($"'{t.Name}' is instantiated every run but never mirrored, so its instance " +
                               "would live on the shared canvas and on no resource");
                if (t.NameParameter is { Length: 0 })
                    errors.Add($"'{t.Name}' declares an empty nameParameter");
                foreach (var p in t.Ports.Where(string.IsNullOrWhiteSpace))
                    errors.Add($"'{t.Name}' declares an empty port name");
                foreach (var g in t.Ports.GroupBy(p => p, StringComparer.Ordinal).Where(g => g.Count() > 1))
                    errors.Add($"'{t.Name}' declares port '{g.Key}' {g.Count()} times");

                // A tap names ports on THIS type; a name absent from its declared contract would wire a
                // publisher to a pin the artefact does not have. Only checked where ports are declared,
                // because a type that declares none is not port-validated at all.
                if (t.Telemetry is { } tap && t.Ports.Count > 0)
                    foreach (var (label, source) in new[]
                             {
                                 ("stateEventSource", tap.StateEventSource),
                                 ("stateDataSource", tap.StateDataSource),
                                 ("initSource", tap.InitSource),
                             })
                        if (string.IsNullOrWhiteSpace(source))
                            errors.Add($"'{t.Name}' telemetry declares no {label}");

                if (t.Telemetry is { } t2 && string.IsNullOrWhiteSpace(t2.TopicNameSource))
                    errors.Add($"'{t.Name}' telemetry declares no topicNameSource, so it would publish " +
                               "under an empty topic");
            }

            // A role names ONE instance in layout.yml, so two templates claiming it leaves the choice to
            // declaration order rather than to the declaration.
            foreach (var g in c.Templates.SelectMany(t => t.InfraRoles.Select(r => (Role: r, t.Name)))
                         .GroupBy(x => x.Role, StringComparer.Ordinal).Where(g => g.Count() > 1))
                errors.Add($"infrastructure role '{g.Key}' is served by {g.Count()} templates: " +
                           string.Join(", ", g.Select(x => x.Name)));

            foreach (var role in new[] { TemplateRole.Process, TemplateRole.Sensor })
            {
                var n = c.Templates.Count(t => t.Role == role &&
                                               (role != TemplateRole.Sensor || t.Kind == TemplateArtefactKind.Cat));
                if (n != 1)
                    errors.Add($"{n} templates carry role '{role}'; exactly one must, or nothing downstream " +
                               "can resolve it without spelling a name");
            }

            // An actuator has to be commandable somehow: a stop vocabulary, or a declared sequence its
            // CAT runs instead. Neither means a recipe could name it and nothing could drive it.
            var executed = rig.Execution;
            foreach (var t in c.Templates.Where(t => t.Role == TemplateRole.Actuator &&
                                                     t.Kind == TemplateArtefactKind.Cat))
                if (t.Protocol == null && !executed.Any(e => e.Claims(t.Name, null) ||
                                                             e.Cat.Length == 0 ||
                                                             string.Equals(e.Cat, t.Name, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"actuator CAT '{t.Name}' declares neither a command protocol nor an " +
                               "execution policy, so nothing could drive an instance of it");

            // Two CATs serving one shape makes the selection depend on where a row happens to sit.
            foreach (var g in c.Templates.Where(t => t.Protocol != null)
                         .SelectMany(t => t.Protocol!.StateCounts.Select(n => (Shape: n, t.Name)))
                         .GroupBy(x => x.Shape).Where(g => g.Count() > 1))
                errors.Add($"a {g.Key}-state graph is claimed by {g.Count()} CATs " +
                           $"({string.Join(", ", g.Select(x => x.Name))}) with no declared priority");

            errors.AddRange(ProtocolErrors(c.Templates));

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "templates.yml is invalid:" + Environment.NewLine + "  - " +
                    string.Join(Environment.NewLine + "  - ", errors));
        }

        private static IEnumerable<string> ProtocolErrors(IReadOnlyList<TemplateDeclaration> templates)
        {
            foreach (var t in templates)
            {
                if (t.Protocol is not { } p) continue;
                if (p.Command.Count == 0)
                    yield return $"'{t.Name}' declares no command values, so nothing can drive it";
                if (p.StateCounts.Count == 0 && !p.ServesBranched)
                    yield return $"'{t.Name}' serves no state-graph shape, so it can never be selected";
                foreach (var stop in p.Command.Keys)
                {
                    if (!p.Settled.ContainsKey(stop))
                        yield return $"'{t.Name}' commands '{stop}' but declares no settled value for it";
                    if (!p.Interlock.ContainsKey(stop))
                        yield return $"'{t.Name}' commands '{stop}' but declares no interlock value for it";
                }
                // enforcedTargets names stops in the TARGET vocabulary - what the CAT's own interlock
                // manager compares against - so a verdict aimed outside it could never gate a move.
                foreach (var stop in p.EnforcedTargets.Where(s => p.Target.Count > 0 && !p.Target.ContainsKey(s)))
                    yield return $"'{t.Name}' enforces the interlock for stop '{stop}', which is not one of " +
                                 "the targets its interlock manager compares against";
                if (p.RawStateRange is { } r && r.Min > r.Max)
                    yield return $"'{t.Name}' declares a rawStateRange of {r.Min}..{r.Max}, which is empty";

                // WHICH TWIN NUMBERS NAME WHICH STOP. Without it nothing can read a command, an
                // interlock or a timing leg written against the twin's own numbering.
                foreach (var stop in p.Command.Keys.Where(k => !p.Stops.ContainsKey(k)))
                    yield return $"'{t.Name}' commands '{stop}' but declares no twin State_Number that " +
                                 "names it, so a model written against its own numbering cannot be read";
                foreach (var stop in p.Stops.Keys.Where(k => !p.Command.ContainsKey(k)))
                    yield return $"'{t.Name}' declares stop numbers for '{stop}', which it has no command " +
                                 "for, so nothing could ever drive it there";
                // One number cannot name two places: which one a rule meant would be undecidable.
                foreach (var clash in p.Stops.SelectMany(kv => kv.Value.Select(n => (n, kv.Key)))
                             .GroupBy(x => x.n).Where(g => g.Count() > 1))
                    yield return $"'{t.Name}' gives State_Number {clash.Key} to " +
                                 string.Join(" and ", clash.Select(x => $"'{x.Key}'")) +
                                 ", so which stop a model naming it means is undecidable";
                foreach (var leg in p.Legs.Keys.Where(k => !p.Command.ContainsKey(k)))
                    yield return $"'{t.Name}' declares a motion leg toward '{leg}', which is not a stop " +
                                 "it can be commanded to";
                // A motion leg is not a stop: sharing a number would time the move by the arrival.
                foreach (var leg in p.Legs.Where(kv => p.StopFor(kv.Value) != null))
                    yield return $"'{t.Name}' times the leg toward '{leg.Key}' from State_Number " +
                                 $"{leg.Value}, which it also declares as the stop '{p.StopFor(leg.Value)}'";
            }

            // Two CATs that could serve one graph shape at the same priority leave the choice to
            // whichever row was written first, and the two drive the actuator differently.
            var shapes = templates.Where(t => t.Protocol != null)
                .SelectMany(t => t.Protocol!.StateCounts.Select(n => (Shape: n.ToString(), t)))
                .Concat(templates.Where(t => t.Protocol is { ServesBranched: true })
                    .Select(t => (Shape: "branched", t)));
            foreach (var g in shapes.GroupBy(x => x.Shape))
            {
                var top = g.Select(x => x.t).OrderByDescending(t => t.Protocol!.Priority).ToList();
                if (top.Count > 1 && top[0].Protocol!.Priority == top[1].Protocol!.Priority)
                    yield return $"a {g.Key}-state graph is served by " +
                                 string.Join(" and ", top.Select(t => $"'{t.Name}'")) +
                                 " at the same protocol.priority, so which one drives it would depend on " +
                                 "which row was written first";
            }
        }

    }
}
