using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using static CodeGen.Services.FbtXmlEditor;

namespace CodeGen.Services
{
    // Deploy-time patches to the shared updateComponentState ring relay: BCNF always forwards,
    // CNF only on a dest_name match, and REQ clears the reused Component_State_Msg dest_name.
    internal static class RingRelayPatcher
    {
        // Ring relay: REQ (a component reporting its OWN state) must clear component_state_out.dest_name —
        // Component_State_Msg is a reused struct, so a stale dest_name spuriously satisfies a target
        // actuator's BREQ match (dest_name==name) and clobbers its state_cmd.
        internal static void PatchRingReportClearDest(string eaeProjectDir, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "updateComponentState.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "updateComponentState.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var req = root.Descendants(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "REQ");
                var st = req?.Element(ns + "ST");
                if (st == null)
                {
                    result.Warnings.Add("updateComponentState.fbt: no REQ algorithm; report-dest-clear skipped.");
                    return;
                }
                if (st.Value.Contains("dest_name"))
                    return;

                const string newBody =
                    "component_state_out.src_id := id;\r\n" +
                    "component_state_out.source_name := name;\r\n" +
                    "component_state_out.dest_name := '';\r\n" +
                    "component_state_out.state := state_sts;\r\n" +
                    "state_table[id].name := name;\r\n" +
                    "state_table[id].state := state_sts;\r\n";
                st.ReplaceAll(new System.Xml.Linq.XCData(newBody));
                doc.Save(fbt);
                result.PatchesApplied.Add(
                    "updateComponentState.fbt: REQ now clears component_state_out.dest_name -- a state REPORT no longer carries a stale command target, so a sensor report can no longer overwrite an actuator's state_cmd.");
                MapperLogger.Info("[Deploy] updateComponentState.fbt: REQ clears dest_name (ring report-vs-command leftover fix)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"updateComponentState.fbt report-dest-clear patch failed: {ex.Message}");
            }
        }

        // Ring relay: BCNF always forwards, but CNF fires into the actuator core only on dest match — else an
        // unrelated report replays the last retained state_cmd through ActuatorCore.pst_event.
        internal static void PatchRingCommandCnfOnlyOnDestination(string eaeProjectDir, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "updateComponentState.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "updateComponentState.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var ecc = root.Descendants(ns + "ECC").FirstOrDefault();
                if (ecc == null)
                {
                    result.Warnings.Add("updateComponentState.fbt: no ECC; destination-gated CNF patch skipped.");
                    return;
                }

                const string commonCondition = "BREQ AND name <> component_state_in.source_name";
                const string addressedCondition = commonCondition + " AND component_state_in.dest_name = name";
                const string passThroughCondition = commonCondition + " AND component_state_in.dest_name <> name";

                bool changed = false;

                var addressedTransition = ecc.Elements(ns + "ECTransition")
                    .FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "START" &&
                        (string?)t.Attribute("Destination") == "BREQ");
                if (addressedTransition == null)
                {
                    ecc.Add(new System.Xml.Linq.XElement(ns + "ECTransition",
                        new System.Xml.Linq.XAttribute("Source", "START"),
                        new System.Xml.Linq.XAttribute("Destination", "BREQ"),
                        new System.Xml.Linq.XAttribute("Condition", addressedCondition),
                        new System.Xml.Linq.XAttribute("x", "825.226"),
                        new System.Xml.Linq.XAttribute("y", "407.2253")));
                    changed = true;
                }
                else if ((string?)addressedTransition.Attribute("Condition") != addressedCondition)
                {
                    addressedTransition.SetAttributeValue("Condition", addressedCondition);
                    changed = true;
                }

                var passState = ecc.Elements(ns + "ECState")
                    .FirstOrDefault(s => (string?)s.Attribute("Name") == "BREQ_PASS");
                if (passState == null)
                {
                    passState = new System.Xml.Linq.XElement(ns + "ECState",
                        new System.Xml.Linq.XAttribute("Name", "BREQ_PASS"),
                        new System.Xml.Linq.XAttribute("x", "1036"),
                        new System.Xml.Linq.XAttribute("y", "752"),
                        new System.Xml.Linq.XElement(ns + "ECAction",
                            new System.Xml.Linq.XAttribute("Algorithm", "BREQ"),
                            new System.Xml.Linq.XAttribute("Output", "BCNF")));
                    var reqState = ecc.Elements(ns + "ECState")
                        .FirstOrDefault(s => (string?)s.Attribute("Name") == "BREQ");
                    if (reqState != null)
                        reqState.AddAfterSelf(passState);
                    else
                        ecc.AddFirst(passState);
                    changed = true;
                }
                else
                {
                    var actions = passState.Elements(ns + "ECAction").ToList();
                    if (!actions.Any(a =>
                            (string?)a.Attribute("Algorithm") == "BREQ" &&
                            (string?)a.Attribute("Output") == "BCNF") ||
                        actions.Any(a => (string?)a.Attribute("Output") == "CNF"))
                    {
                        passState.Elements(ns + "ECAction").Remove();
                        passState.Add(new System.Xml.Linq.XElement(ns + "ECAction",
                            new System.Xml.Linq.XAttribute("Algorithm", "BREQ"),
                            new System.Xml.Linq.XAttribute("Output", "BCNF")));
                        changed = true;
                    }
                }

                var passTransition = ecc.Elements(ns + "ECTransition")
                    .FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "START" &&
                        (string?)t.Attribute("Destination") == "BREQ_PASS");
                if (passTransition == null)
                {
                    ecc.Add(new System.Xml.Linq.XElement(ns + "ECTransition",
                        new System.Xml.Linq.XAttribute("Source", "START"),
                        new System.Xml.Linq.XAttribute("Destination", "BREQ_PASS"),
                        new System.Xml.Linq.XAttribute("Condition", passThroughCondition),
                        new System.Xml.Linq.XAttribute("x", "721"),
                        new System.Xml.Linq.XAttribute("y", "655")));
                    changed = true;
                }
                else if ((string?)passTransition.Attribute("Condition") != passThroughCondition)
                {
                    passTransition.SetAttributeValue("Condition", passThroughCondition);
                    changed = true;
                }

                var passReturn = ecc.Elements(ns + "ECTransition")
                    .FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "BREQ_PASS" &&
                        (string?)t.Attribute("Destination") == "START");
                if (passReturn == null)
                {
                    ecc.Add(new System.Xml.Linq.XElement(ns + "ECTransition",
                        new System.Xml.Linq.XAttribute("Source", "BREQ_PASS"),
                        new System.Xml.Linq.XAttribute("Destination", "START"),
                        new System.Xml.Linq.XAttribute("Condition", "1"),
                        new System.Xml.Linq.XAttribute("x", "793"),
                        new System.Xml.Linq.XAttribute("y", "760")));
                    changed = true;
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        "updateComponentState.fbt: CNF is now destination-gated; non-target BREQ messages pass with BCNF only, preventing stale actuator command replay.");
                    MapperLogger.Info("[Deploy] updateComponentState.fbt: gated CNF to dest_name match only (stale command replay fix)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"updateComponentState.fbt destination-gated CNF patch failed: {ex.Message}");
            }
        }
    }
}
