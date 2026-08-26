using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using static CodeGen.Services.FbtXmlEditor;
using System.IO;
using CodeGen.Translation.Interlocks;

using CodeGen.Mapping;
namespace CodeGen.Services
{
    // Deploy-time patchers for the actuator/sensor CATs. Consumed via `using static`.
    internal static class ActuatorCatTemplatePatcher
    {
        internal static void PatchCatSymlinkQi(FbtEditScope scope, string catName, DeployResult result)
        {
            EditDeployedFbt(scope, catName + ".fbt",
                $"{catName}.fbt QI guard failed", result, (doc, root, ns, fbt) =>
            {

                var targets = root.Descendants(ns + "FB").Where(f =>
                {
                    var t = (string?)f.Attribute("Type") ?? string.Empty;
                    return t.StartsWith("SYMLINKMULTIVARDST", StringComparison.Ordinal)
                        || t.StartsWith("SYMLINKMULTIVARSRC", StringComparison.Ordinal);
                }).ToList();

                if (targets.Count == 0)
                {
                    result.Warnings.Add(
                        $"{catName}.fbt: no SYMLINKMULTIVARDST/SRC FB found; QI guard skipped.");
                    return;
                }

                foreach (var fb in targets)
                {
                    bool hasQi = fb.Elements(ns + "Parameter").Any(p =>
                        (string?)p.Attribute("Name") == "QI");
                    if (hasQi)
                    {
                        foreach (var p in fb.Elements(ns + "Parameter")
                                     .Where(p => (string?)p.Attribute("Name") == "QI"))
                            p.SetAttributeValue("Value", "TRUE");
                    }
                    else
                    {
                        var name1 = fb.Elements(ns + "Parameter")
                            .FirstOrDefault(p => (string?)p.Attribute("Name") == "NAME1");
                        var qi = new System.Xml.Linq.XElement(ns + "Parameter",
                            new System.Xml.Linq.XAttribute("Name", "QI"),
                            new System.Xml.Linq.XAttribute("Value", "TRUE"));
                        if (name1 != null) name1.AddAfterSelf(qi);
                        else fb.Add(qi);
                    }
                    result.PatchesApplied.Add(
                        $"{catName}: ensured {(string?)fb.Attribute("Name")} " +
                        $"({(string?)fb.Attribute("Type")}) QI=TRUE");
                }

                doc.Save(fbt);
                MapperLogger.Info(
                    $"[Deploy] {catName}.fbt: QI=TRUE ensured on " +
                    $"{targets.Count} SYMLINKMULTIVAR FB(s) (DST subscriber + SRC publisher enabled)");
            });
        }

        // Re-sample the actuator's own position sensors so it notices it has ARRIVED. FiveStateActuator's arrival
        // arcs carry NO event term, so they are only evaluated when an event reaches the core, and 'Inputs' is a
        // sample-on-REQ SYMLINKMULTIVARDST: with no Inputs.REQ driver the actuator can drive to a position and
        // never observe that it got there, so its WAIT never satisfies. The motion timers are no fallback — they
        // are gated AND(output, NOT SensorFitted), so a sensor-fitted actuator has none. Deliberately LOCAL: it
        // publishes nothing, so idle ring traffic stays zero. NOT applied to the centre-home swivel (see
        // StripCatHomeSensorPoll) nor to Sensor_Bool_CAT, which re-reads through its RD event.
        internal static void EnsureFiveStateInputPoll(FbtEditScope scope, int pollMs, DeployResult result)
            => EditDeployedFbt(scope, CodeGen.Mapping.TemplateManifest.FbtOf("fiveStateCat"),
                "Five_State_Actuator_CAT input poll inject failed", result,
                (doc, root, ns, fbt) =>
            {
                var net = root.Element(ns + "FBNetwork");
                var ec = net?.Element(ns + "EventConnections");
                if (net == null || ec == null) return;
                if (net.Elements(ns + "FB").Any(f => (string?)f.Attribute("Name") == "Poll")) return;
                if (!net.Elements(ns + "FB").Any(f => (string?)f.Attribute("Name") == "Inputs"))
                {
                    result.Warnings.Add("Five_State_Actuator_CAT: no 'Inputs' FB; input poll skipped.");
                    return;
                }

                var idAttr = root.Elements(ns + "Attribute")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
                int next = net.Elements(ns + "FB")
                    .Select(f => int.TryParse((string?)f.Attribute("ID"), out var v) ? v : 0)
                    .DefaultIfEmpty(0).Max() + 1;

                // The LibraryElements schema fixes FBNetwork child order: every FB must precede the Input/Frame/Output
                // markers and the connection lists, so insert after the last FB rather than appending.
                var poll = new XElement(ns + "FB",
                    new XAttribute("ID", next), new XAttribute("Name", "Poll"),
                    new XAttribute("Type", "E_DELAY"), new XAttribute("x", "800"),
                    new XAttribute("y", "2580"), new XAttribute("Namespace", "IEC61499.Standard"),
                    new XElement(ns + "Parameter",
                        new XAttribute("Name", "DT"), new XAttribute("Value",
                            Configuration.GenerationConfig.Duration(
                                pollMs))));
                var lastFb = net.Elements(ns + "FB").LastOrDefault();
                if (lastFb != null) lastFb.AddAfterSelf(poll);
                else
                {
                    var first = net.Elements().FirstOrDefault();
                    if (first != null) first.AddBeforeSelf(poll); else net.Add(poll);
                }
                idAttr?.SetAttributeValue("Value", (next + 1).ToString());

                foreach (var (s, d) in new[] { ("INIT", "Poll.START"), ("Poll.EO", "Poll.START"), ("Poll.EO", "Inputs.REQ") })
                    if (!ec.Elements(ns + "Connection").Any(c =>
                            (string?)c.Attribute("Source") == s && (string?)c.Attribute("Destination") == d))
                        ec.Add(new XElement(ns + "Connection",
                            new XAttribute("Source", s), new XAttribute("Destination", d)));

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    "Five_State_Actuator_CAT: restored the 200 ms input Poll driving Inputs.REQ, so a sensor-fitted "
                    + "actuator observes its own arrival (local re-read; publishes only on a real state change).");
                MapperLogger.Info("[Deploy] Five_State_Actuator_CAT.fbt: input Poll -> Inputs.REQ restored");
            }, notFoundNote: "Five_State_Actuator_CAT.fbt not found; input poll skipped.");

    }
}
