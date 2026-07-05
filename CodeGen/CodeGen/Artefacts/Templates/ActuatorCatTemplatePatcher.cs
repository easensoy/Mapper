using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation.Interlocks;
using static CodeGen.Services.FbtXmlEditor;

namespace CodeGen.Services
{
    // Deploy-time patchers for the actuator/sensor CATs. Consumed via `using static`.
    internal static class ActuatorCatTemplatePatcher
    {
        // Force QI=TRUE on Sensor_Bool_CAT's internal SYMLINKMULTIVARDST; without it the DST defaults
        // FALSE (disabled subscriber, publishes to '$${PATH}Input' silently dropped). Idempotent.
        internal static void PatchSensorBoolCatDstQi(string eaeProjectDir, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "Sensor_Bool_CAT", "Sensor_Bool_CAT.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "Sensor_Bool_CAT.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var dst = root.Descendants(ns + "FB").FirstOrDefault(f =>
                    ((string?)f.Attribute("Type") ?? string.Empty)
                        .StartsWith("SYMLINKMULTIVARDST", StringComparison.Ordinal));
                if (dst == null)
                {
                    result.Warnings.Add("Sensor_Bool_CAT.fbt: no SYMLINKMULTIVARDST FB found; QI guard skipped.");
                    return;
                }

                bool hasQi = dst.Elements(ns + "Parameter").Any(p =>
                    (string?)p.Attribute("Name") == "QI");
                if (hasQi)
                {
                    foreach (var p in dst.Elements(ns + "Parameter")
                                 .Where(p => (string?)p.Attribute("Name") == "QI"))
                        p.SetAttributeValue("Value", "TRUE");
                }
                else
                {
                    var name1 = dst.Elements(ns + "Parameter")
                        .FirstOrDefault(p => (string?)p.Attribute("Name") == "NAME1");
                    var qi = new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", "QI"),
                        new System.Xml.Linq.XAttribute("Value", "TRUE"));
                    if (name1 != null) name1.AddAfterSelf(qi);
                    else dst.Add(qi);
                }

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    $"Sensor_Bool_CAT: ensured {(string?)dst.Attribute("Name")} " +
                    $"({(string?)dst.Attribute("Type")}) QI=TRUE");
                MapperLogger.Info(
                    "[Deploy] Sensor_Bool_CAT.fbt: SYMLINKMULTIVARDST QI=TRUE ensured " +
                    "(live subscriber enabled — publishes no longer dropped)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Sensor_Bool_CAT.fbt QI guard failed: {ex.Message}");
            }
        }

        // Force QI=TRUE on an actuator CAT's internal SYMLINKMULTIVARDST (Inputs) + SYMLINKMULTIVARSRC
        // (Output); without QI the DST rejects sensor publishes and the SRC never writes the solenoid.
        internal static void PatchCatSymlinkQi(string eaeProjectDir, string catName, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499",
                catName, catName + ".fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        catName + ".fbt", SearchOption.AllDirectories)
                    .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                    ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

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
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catName}.fbt QI guard failed: {ex.Message}");
            }
        }



        // Restores the Five_State CAT's Inputs DST to the physical sensor symlinks ($${PATH}athome/atwork).
        internal static void NormalizeFiveStateSimSensorSource(string eaeProjectDir, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Five_State_Actuator_CAT.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Five_State_Actuator_CAT.fbt not found; Five_State sim-sensor normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "FBNetwork");
                var inputs = net?.Elements(ns + "FB")
                    .FirstOrDefault(f => (string?)f.Attribute("Name") == "Inputs");
                if (inputs == null)
                {
                    result.Warnings.Add("Five_State_Actuator_CAT.fbt: Inputs FB not found; Five_State sim-sensor normalize skipped.");
                    return;
                }

                var want = new[]
                {
                    ("NAME1", "'$${PATH}athome'"),
                    ("NAME2", "'$${PATH}atwork'"),
                };
                bool changed = false;
                foreach (var (pn, val) in want)
                {
                    var p = inputs.Elements(ns + "Parameter")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == pn);
                    if (p == null) continue;
                    if ((string?)p.Attribute("Value") != val) { p.SetAttributeValue("Value", val); changed = true; }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add("Five_State_Actuator_CAT: Inputs athome/atwork -> physical sensor symlinks (hardware)");
                    MapperLogger.Info("[Deploy] Five_State sim-sensor source normalize: physical sensor symlinks restored");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State sim-sensor normalize failed: {ex.Message}");
            }
        }



        // Restores Five_State_Actuator_CAT's two wired fault-enable inputs (VarDecl + INIT With + Input pin + FB17/FB14.IN2).
        internal static void NormalizeFiveStateFaultEnables(
            string eaeProjectDir, DeployResult result)
        {
            var map = new[]
            {
                new { Enable = "enableToWorkFaultTimeout", Dest = "FB17.IN2", X = "1280", Y = "5772" },
                new { Enable = "enableToHomeFaultTimeout", Dest = "FB14.IN2", X = "1260", Y = "5292" },
            };

            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Five_State_Actuator_CAT.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Five_State_Actuator_CAT.fbt not found; fault-enable normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var iface = root.Element(ns + "InterfaceList");
                var net = root.Element(ns + "FBNetwork");
                if (iface == null || net == null)
                {
                    result.Warnings.Add("Five_State_Actuator_CAT.fbt: missing InterfaceList/FBNetwork; fault-enable normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var initEvent = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");

                bool changed = false;

                foreach (var m in map)
                {
                    var conn = dataConns?.Elements(ns + "Connection")
                        .FirstOrDefault(c => (string?)c.Attribute("Destination") == m.Dest);
                    if (conn != null && (string?)conn.Attribute("Source") != m.Enable)
                    {
                        conn.SetAttributeValue("Source", m.Enable);
                        changed = true;
                    }

                    if (inputVars != null &&
                        !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == m.Enable))
                    {
                        inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", m.Enable),
                            new System.Xml.Linq.XAttribute("Type", "BOOL")));
                        changed = true;
                    }
                    if (initEvent != null &&
                        !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == m.Enable))
                    {
                        initEvent.Add(new System.Xml.Linq.XElement(ns + "With",
                            new System.Xml.Linq.XAttribute("Var", m.Enable)));
                        changed = true;
                    }
                    if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == m.Enable))
                    {
                        var pin = new System.Xml.Linq.XElement(ns + "Input",
                            new System.Xml.Linq.XAttribute("Name", m.Enable),
                            new System.Xml.Linq.XAttribute("x", m.X),
                            new System.Xml.Linq.XAttribute("y", m.Y),
                            new System.Xml.Linq.XAttribute("Type", "Data"));
                        var lastInput = net.Elements(ns + "Input").LastOrDefault();
                        if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                        changed = true;
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add("Five_State_Actuator_CAT: fault-enable inputs restored as wired inputs (hardware)");
                    MapperLogger.Info("[Deploy] Five_State_Actuator_CAT fault-enable normalize: wired inputs restored");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State_Actuator_CAT fault-enable normalize failed: {ex.Message}");
            }
        }


        // Force the actuator's "mode" InputVar InitialValue=1 (auto); without it mode=0 at boot fires no
        // mode_event and the ECC is stuck in AtHomeInit forever.
        internal static void PatchActuatorModeInitialValue(string eaeProjectDir, string fbtFileName, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", fbtFileName);
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        fbtFileName, SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var inputVars = root.Descendants(ns + "InputVars").FirstOrDefault();
                var modeVar = inputVars?
                    .Elements(ns + "VarDeclaration")
                    .FirstOrDefault(v => (string?)v.Attribute("Name") == "mode");
                if (modeVar == null)
                {
                    result.Warnings.Add(
                        $"{fbtFileName}: no 'mode' InputVar found; Mode-default guard skipped.");
                    return;
                }

                var iv = (string?)modeVar.Attribute("InitialValue");
                if (iv == "1") return;
                modeVar.SetAttributeValue("InitialValue", "1");
                doc.Save(fbt);

                result.PatchesApplied.Add(
                    $"{fbtFileName}: forced mode InputVar InitialValue=1 (powers up in auto mode)");
                MapperLogger.Info(
                    $"[Deploy] {fbtFileName}: mode InputVar InitialValue=1 " +
                    "(actuator ECC no longer stuck in AtHomeInit at boot)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{fbtFileName} Mode-default guard failed: {ex.Message}");
            }
        }


    }
}
