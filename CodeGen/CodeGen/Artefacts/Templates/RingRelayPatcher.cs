using System;
using System.Collections.Generic;
using System.Linq;
using static CodeGen.Services.FbtXmlEditor;
using System.IO;
using System.Xml.Linq;

namespace CodeGen.Services
{
    // Deploy-time patches to the shared updateComponentState ring relay: BCNF always forwards,
    // CNF only on a dest_name match, and REQ clears the reused Component_State_Msg dest_name.
    internal static class RingRelayPatcher
    {
        // `state_cmd` LATCHES the last command addressed to this component and nothing resets it, while the stock
        // INIT algorithm is empty. It drives the core's `state_val`, and the centre-home swivel accepts a work
        // command as a pure LEVEL, so a latch surviving a redeploy moves the arm with no recipe behind it.
        // Clearing it at INIT is safe: 0 is not a command in any core, and state_sts/state_table are untouched.
        internal static void PatchRingClearCommandLatchOnInit(string eaeProjectDir, DeployResult result)
            => EditDeployedFbt(eaeProjectDir, "updateComponentState.fbt",
                "updateComponentState.fbt command-latch INIT clear failed", result,
                (doc, root, ns, fbt) =>
            {
                const string body = "state_cmd := 0;";
                var basic = root.Descendants(ns + "BasicFB").FirstOrDefault();
                if (basic == null)
                {
                    result.Warnings.Add("updateComponentState.fbt: no BasicFB; command-latch clear skipped.");
                    return;
                }

                var init = FindAlgorithm(basic, ns, "INIT");
                if (init == null)
                {
                    init = new XElement(ns + "Algorithm", new XAttribute("Name", "INIT"));
                    basic.Add(init);
                }

                var st = init.Element(ns + "ST");
                if (st != null && st.Value.Replace(" ", string.Empty).Contains("state_cmd:=0")) return;

                init.RemoveNodes();
                init.Add(new XElement(ns + "ST", new XCData(body)));
                doc.Save(fbt);
                result.PatchesApplied.Add(
                    "updateComponentState.fbt: INIT clears the latched command (state_cmd := 0) so a command "
                    + "retained across Clean/redeploy cannot drive an actuator before the recipe asks");
            });

        // REQ (a component reporting its OWN state) must clear component_state_out.dest_name: Component_State_Msg
        // is a reused struct, so a stale dest_name satisfies another actuator's BREQ match and clobbers its state_cmd.
        internal static void PatchRingReportClearDest(string eaeProjectDir, DeployResult result)
            => EditDeployedFbt(eaeProjectDir, "updateComponentState.fbt",
                "updateComponentState.fbt report-dest-clear patch failed", result,
                (doc, root, ns, fbt) =>
            {
                var st = FindAlgorithm(root, ns, "REQ")?.Element(ns + "ST");
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
                st.ReplaceAll(new XCData(newBody));
                doc.Save(fbt);
                result.PatchesApplied.Add(
                    "updateComponentState.fbt: REQ now clears component_state_out.dest_name -- a state REPORT no longer carries a stale command target, so a sensor report can no longer overwrite an actuator's state_cmd.");
                MapperLogger.Info("[Deploy] updateComponentState.fbt: REQ clears dest_name (ring report-vs-command leftover fix)");
            });

        // CNF fires into the actuator core only on a dest match, else an unrelated report replays the retained state_cmd.
        internal static void PatchRingCommandCnfOnlyOnDestination(string eaeProjectDir, DeployResult result)
            => EditDeployedFbt(eaeProjectDir, "updateComponentState.fbt",
                "updateComponentState.fbt destination-gated CNF patch failed", result,
                (doc, root, ns, fbt) =>
            {
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

                var addressedTransition = FindTransition(ecc, ns, "START", "BREQ");
                if (addressedTransition == null)
                {
                    ecc.Add(new XElement(ns + "ECTransition",
                        new XAttribute("Source", "START"),
                        new XAttribute("Destination", "BREQ"),
                        new XAttribute("Condition", addressedCondition),
                        new XAttribute("x", "825.226"),
                        new XAttribute("y", "407.2253")));
                    changed = true;
                }
                else if ((string?)addressedTransition.Attribute("Condition") != addressedCondition)
                {
                    addressedTransition.SetAttributeValue("Condition", addressedCondition);
                    changed = true;
                }

                var passState = ecc.ByAttribute(ns, "ECState", "Name", "BREQ_PASS");
                if (passState == null)
                {
                    passState = new XElement(ns + "ECState",
                        new XAttribute("Name", "BREQ_PASS"),
                        new XAttribute("x", "1036"),
                        new XAttribute("y", "752"),
                        new XElement(ns + "ECAction",
                            new XAttribute("Algorithm", "BREQ"),
                            new XAttribute("Output", "BCNF")));
                    var reqState = ecc.ByAttribute(ns, "ECState", "Name", "BREQ");
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
                        passState.Add(new XElement(ns + "ECAction",
                            new XAttribute("Algorithm", "BREQ"),
                            new XAttribute("Output", "BCNF")));
                        changed = true;
                    }
                }

                var passTransition = FindTransition(ecc, ns, "START", "BREQ_PASS");
                if (passTransition == null)
                {
                    ecc.Add(new XElement(ns + "ECTransition",
                        new XAttribute("Source", "START"),
                        new XAttribute("Destination", "BREQ_PASS"),
                        new XAttribute("Condition", passThroughCondition),
                        new XAttribute("x", "721"),
                        new XAttribute("y", "655")));
                    changed = true;
                }
                else if ((string?)passTransition.Attribute("Condition") != passThroughCondition)
                {
                    passTransition.SetAttributeValue("Condition", passThroughCondition);
                    changed = true;
                }

                var passReturn = FindTransition(ecc, ns, "BREQ_PASS", "START");
                if (passReturn == null)
                {
                    ecc.Add(new XElement(ns + "ECTransition",
                        new XAttribute("Source", "BREQ_PASS"),
                        new XAttribute("Destination", "START"),
                        new XAttribute("Condition", "1"),
                        new XAttribute("x", "793"),
                        new XAttribute("y", "760")));
                    changed = true;
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        "updateComponentState.fbt: CNF is now destination-gated; non-target BREQ messages pass with BCNF only, preventing stale actuator command replay.");
                    MapperLogger.Info("[Deploy] updateComponentState.fbt: gated CNF to dest_name match only (stale command replay fix)");
                }
            });
    }
}
