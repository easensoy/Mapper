using System;
using System.Linq;
using System.IO;
using System.Xml.Linq;

namespace CodeGen.Services
{
    // Deploy-time patchers for the HMI-facing surface of the CATs (the _HMI faceplate contract + the
    // HMI/OPCUA section frame). Consumed via `using static` so the call sites in TemplateLibraryDeployer
    // stay unqualified.
    internal static class HmiTemplatePatcher
    {
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
                throw new InvalidOperationException(
                    $"FixCatHmiOpcuaFrame({catName}) failed: {ex.Message} — a deploy-time patch could not be applied, so the deployed type does not have the shape the planner's parameters name. Usually EAE holding the .fbt open during Generate: CLOSE EAE and Generate again. Generation ABORTED rather than shipping a tree EAE will not run.", ex);
            }
        }
    }
}
