using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Services
{
    // Deploy-time patchers for the HMI-facing surface of the CATs (the _HMI faceplate contract + the
    // HMI/OPCUA section frame). Consumed via `using static` so the call sites in TemplateLibraryDeployer
    // stay unqualified.
    internal static class HmiTemplatePatcher
    {
        // Repair the deployed Seven-State centre-home CAT's boundary state output: a stale deploy can leave
        // the OutputVar current_state_to_process orphaned (no WITH clause), which EAE rejects. The _HMI
        // faceplate references it (must NOT be removed), so re-add state_out (WITH current_state_to_process)
        // + the ActuatorCore.pst_out -> state_out event connection. Idempotent.
        internal static void EnsureSevenStateStateOut(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var fbt = Path.Combine(eaeProjectDir, "IEC61499",
                    "Seven_State_Actuator_Centre_Home_CAT", "Seven_State_Actuator_Centre_Home_CAT.fbt");
                if (!File.Exists(fbt)) return;
                var doc = XDocument.Load(fbt, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                XNamespace ns = root.GetDefaultNamespace();
                var iface = root.Element(ns + "InterfaceList");
                var net = root.Element(ns + "FBNetwork");
                if (iface == null || net == null) return;

                // Only repair a stale-bridge CAT that still carries the boundary current_state_to_process.
                var outputVars = iface.Element(ns + "OutputVars");
                bool hasBoundaryVar = outputVars?.Elements(ns + "VarDeclaration")
                    .Any(v => (string?)v.Attribute("Name") == "current_state_to_process") == true;
                if (!hasBoundaryVar) return;   // fresh committed CAT -> nothing to repair

                var eventOutputs = iface.Element(ns + "EventOutputs");
                if (eventOutputs == null) return;
                if (eventOutputs.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "state_out"))
                    return;   // already consistent

                // Re-pair: state_out WITH current_state_to_process, driven by ActuatorCore.pst_out.
                eventOutputs.Add(new XElement(ns + "Event",
                    new XAttribute("Name", "state_out"),
                    new XAttribute("Comment", "Re-paired with current_state_to_process so the boundary var has a WITH clause (HMI reads it)"),
                    new XElement(ns + "With",
                        new XAttribute("Var", "current_state_to_process"))));

                var ec = net.Element(ns + "EventConnections");
                if (ec == null) { ec = new XElement(ns + "EventConnections"); net.Add(ec); }
                if (!ec.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Destination") == "state_out"))
                    ec.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", "ActuatorCore.pst_out"),
                        new XAttribute("Destination", "state_out")));

                doc.Save(fbt, SaveOptions.DisableFormatting);
                result.PatchesApplied.Add("Seven_State_Actuator_Centre_Home_CAT: re-paired state_out WITH current_state_to_process (HMI keeps the var)");
                MapperLogger.Info("[Deploy] Seven_State state_out re-paired (current_state_to_process valid again)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"EnsureSevenStateStateOut failed: {ex.Message}");
            }
        }

        // VISUAL-only: keep the "HMI & OPCUA Connectivity" section frame from spilling into the section
        // below (MoveStyle "AnyContained"->"None", cap height, pull IThis up inside). No wiring change.
        internal static void FixCatHmiOpcuaFrame(string eaeProjectDir, string catName, DeployResult result)
        {
            try
            {
                var fbt = Path.Combine(eaeProjectDir, "IEC61499", catName, catName + ".fbt");
                if (!File.Exists(fbt)) return;
                var doc = XDocument.Load(fbt, LoadOptions.PreserveWhitespace);
                var net = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "FBNetwork");
                if (net == null) return;
                var ns = net.Name.Namespace;
                double Dv(XElement e, string a) =>
                    double.TryParse((string?)e.Attribute(a), out var v) ? v : 0;
                var frames = net.Elements(ns + "Frame").ToList();
                var hmi = frames.FirstOrDefault(fr =>
                    ((string?)fr.Elements(ns + "Parameter")
                        .FirstOrDefault(p => (string?)p.Attribute("Name") == "Text")?.Attribute("Value") ?? "")
                        .IndexOf("OPCUA", StringComparison.OrdinalIgnoreCase) >= 0);
                if (hmi == null) return;   // CAT has no HMI/OPCUA section (Sensor_Bool/Robot_Task) — nothing to do
                double fy = Dv(hmi, "Y"), fh = Dv(hmi, "Height");
                double nextY = frames.Select(fr => Dv(fr, "Y")).Where(y => y > fy + 100)
                    .DefaultIfEmpty(fy + fh + 1500).Min();
                int newH = (int)System.Math.Max(fh, nextY - fy - 30);
                hmi.SetAttributeValue("Height", newH.ToString());
                var ms = hmi.Elements(ns + "Parameter").FirstOrDefault(p => (string?)p.Attribute("Name") == "MoveStyle");
                if (ms != null) ms.SetAttributeValue("Value", "None");
                var ithis = net.Elements(ns + "FB").FirstOrDefault(f => (string?)f.Attribute("Name") == "IThis");
                if (ithis != null) ithis.SetAttributeValue("y", ((int)(fy + 40)).ToString());
                doc.Save(fbt, SaveOptions.DisableFormatting);
                result.PatchesApplied.Add($"{catName}: HMI/OPCUA frame fixed (MoveStyle=None, H={newH}, IThis inside)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"FixCatHmiOpcuaFrame({catName}) failed: {ex.Message}");
            }
        }
    }
}
