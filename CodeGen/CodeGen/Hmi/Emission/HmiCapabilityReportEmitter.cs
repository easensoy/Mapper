using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace CodeGen.Hmi
{
    // The evidence file, at the two levels that are genuinely different.
    //
    //   EffectiveOperatorAction - what a person can actually trigger on the emitted panel. This is
    //                             the headline, and it is the SAME verdict the emitter and the
    //                             validator use, so the report cannot disagree with the source.
    //   ControllerCapability    - what the controller would honour if the panel could send it. Kept
    //                             because it is the useful diagnostic, but clearly labelled as NOT
    //                             what the operator gets: a station that accepts a cycle selection
    //                             while the recipe engine ignores it belongs here, not in the summary.
    //
    // It is DERIVED, never authored: nothing here decides anything.
    internal static class HmiCapabilityReportEmitter
    {
        internal const string FileName = "HmiCapabilities.xml";

        internal static string Emit(string hmiDir, HmiPlant plant, HmiPlan plan)
        {
            var actions = plan.Screens.SelectMany(s => s.Items).SelectMany(i => i.Actions).ToList();

            var rows = plant.AllCapabilities()
                .Where(r => r.Cap.Purpose != HmiCapabilityPurpose.Monitor)
                .OrderBy(r => r.Cap.Purpose.ToString(), StringComparer.Ordinal)
                .ThenBy(r => r.Owner, StringComparer.Ordinal)
                .ToList();

            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("HmiCapabilities",
                    new XAttribute("model", plant.ModelName),
                    new XAttribute("screens", plan.Screens.Count),
                    new XAttribute("placed", plan.Screens.Sum(s => s.Items.Count)),

                    // THE HEADLINE: effective operator actions, after every gate including symbol
                    // and action gating. An action absent from here cannot be triggered at all.
                    new XElement("Summary",
                        new XAttribute("effectiveActions", actions.Count(a => a.Effective)),
                        new XAttribute("of", actions.Count),
                        actions.GroupBy(a => a.ActionId)
                            .OrderBy(g => g.Key, StringComparer.Ordinal)
                            .Select(g => new XElement("Action",
                                new XAttribute("id", g.Key),
                                new XAttribute("label", g.First().Label),
                                new XAttribute("effective", g.Count(x => x.Effective)),
                                new XAttribute("of", g.Count()),
                                new XAttribute("status", g.Any(x => x.Effective) ? "EFFECTIVE" : "WITHHELD"),
                                ActionReason(g)))),

                    new XElement("EffectiveOperatorAction",
                        actions
                            .OrderBy(a => a.ActionId, StringComparer.Ordinal)
                            .ThenBy(a => a.Instance, StringComparer.Ordinal)
                            .Select(a => new XElement("Control",
                                new XAttribute("instance", a.Instance),
                                new XAttribute("symbol", a.Symbol),
                                new XAttribute("action", a.ActionId),
                                new XAttribute("label", a.Label),
                                a.Call == null ? null : new XAttribute("fires", a.Call),
                                a.Writes == null ? null : new XAttribute("writes", a.Writes),
                                new XAttribute("effective", a.Effective),
                                a.Effective ? null : new XAttribute("reasonCode", a.Reason),
                                a.Effective || string.IsNullOrEmpty(a.Detail)
                                    ? null : new XAttribute("reason", a.Detail)))),

                    // Controller-side evidence ONLY. Not what the operator gets.
                    new XElement("ControllerCapability",
                        rows.GroupBy(r => r.Cap.Purpose)
                            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
                            .Select(g => new XElement("Capability",
                                new XAttribute("purpose", g.Key),
                                new XAttribute("honoured", g.Count(x => x.Cap.Supported)),
                                new XAttribute("of", g.Count()),
                                Reason(g)))),

                    // Why a move is refused, in the operator's words. These are the rules the
                    // evaluator will actually run - GenerationContext.Interlocks, joined to twin names.
                    new XElement("Interlocks",
                        plant.Components.Where(c => c.Interlocks.Count > 0)
                            .OrderBy(c => c.InstanceName, StringComparer.Ordinal)
                            .Select(c => new XElement("Component",
                                new XAttribute("name", c.InstanceName),
                                new XAttribute("rules", c.Interlocks.Count),
                                c.Interlocks.Select(r => new XElement("Rule",
                                    new XAttribute("from", r.FromState),
                                    new XAttribute("to", r.ToState),
                                    new XAttribute("sourceSlot", r.SourceSlot),
                                    new XAttribute("blockedState", r.BlockedState),
                                    r.Explain(c.DisplayName)))))),

                    // Where each instance actually lives. A TagName binds to one FB on one resource,
                    // and a slot only means anything inside its own report ring, so the binding is
                    // only checkable with all four stated together.
                    new XElement("Bindings",
                        plant.Components.Select(c => new XElement("Instance",
                                new XAttribute("name", c.InstanceName), new XAttribute("tag", c.TagName),
                                new XAttribute("controller", c.Controller), new XAttribute("resource", c.Resource),
                                new XAttribute("slot", c.Slot), new XAttribute("ring", c.Ring ?? "none")))
                            .Concat(plant.Stations.Select(st => new XElement("Instance",
                                new XAttribute("name", st.InstanceName), new XAttribute("tag", st.TagName),
                                new XAttribute("controller", st.Controller), new XAttribute("resource", st.Resource),
                                new XAttribute("modeChain", st.ReceivesModeBroadcast))))),

                    new XElement("Instances",
                        rows.Select(r => new XElement("Instance",
                            new XAttribute("name", r.Owner),
                            new XAttribute("purpose", r.Cap.Purpose),
                            new XAttribute("supported", r.Cap.Supported),
                            r.Cap.Supported ? null : new XAttribute("reasonCode", r.Cap.Reason),
                            r.Cap.Supported || string.IsNullOrEmpty(r.Cap.Detail)
                                ? null : new XAttribute("reason", r.Cap.Detail),
                            string.IsNullOrEmpty(r.Cap.OutputEvent)
                                ? null : new XAttribute("outputEvent", r.Cap.OutputEvent),
                            r.Cap.OutputData.Count == 0
                                ? null : new XAttribute("outputData", string.Join(" ", r.Cap.OutputData)))))));

            var path = Path.Combine(hmiDir, FileName);
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
                doc.Save(w);
            return FileName;
        }

        private static XAttribute? ActionReason(IEnumerable<HmiActionVerdict> group)
        {
            var reasons = group.Where(a => !a.Effective).Select(a => a.Detail)
                .Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.Ordinal).ToList();
            return reasons.Count == 0 ? null : new XAttribute("reason", string.Join(" | ", reasons));
        }

        // The distinct reasons behind a withheld command. Distinct because the same missing element
        // usually withholds it on every instance, and repeating it once per instance buries the fact.
        private static XAttribute? Reason(IEnumerable<(string Owner, HmiCapability Cap)> group)
        {
            var reasons = group.Where(x => !x.Cap.Supported)
                .Select(x => x.Cap.Detail)
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return reasons.Count == 0 ? null : new XAttribute("reason", string.Join(" | ", reasons));
        }

    }
}
