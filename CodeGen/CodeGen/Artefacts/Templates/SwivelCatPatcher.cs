using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using static CodeGen.Services.FbtXmlEditor;

namespace CodeGen.Services
{
    // Deploy-time patches for the centre-home swivel CAT (Seven_State_Actuator_Centre_Home): the
    // physical-sensor restore, the home-poll strip, and the AtHome coil/brake/recovery behaviours.
    internal static class SwivelCatPatcher
    {
        // Keeps the centre-home swivel's Inputs block on the real sensor symlinks and strips sim-position
        // wiring; hard-fails if SimCentreHomeSensor_7SCH survives (the rig can't use sim wiring).
        internal static void NormalizeSwivelSimSensorSource(string eaeProjectDir, DeployResult result)
        {
            var fbt = FindDeployedFbt(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT.fbt");
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Seven_State_Actuator_Centre_Home_CAT.fbt not found; swivel sim-sensor normalize skipped.");
                return;
            }
            try
            {
                var doc = LoadXmlWithRetry(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "FBNetwork");
                if (net == null)
                {
                    result.Warnings.Add("Seven_State_Actuator_Centre_Home_CAT.fbt: FBNetwork not found; swivel sim-sensor normalize skipped.");
                    return;
                }
                var inputs = net.Elements(ns + "FB")
                    .FirstOrDefault(f => (string?)f.Attribute("Name") == "Inputs");
                if (inputs == null)
                {
                    result.Warnings.Add("Seven_State_Actuator_Centre_Home_CAT.fbt: Inputs FB not found; swivel sim-sensor normalize skipped.");
                    return;
                }

                const string simFbName = "SimPosition";
                bool changed = false;

                XElement EnsureSection(string localName)
                {
                    var section = net.Element(ns + localName);
                    if (section != null) return section;
                    section = new XElement(ns + localName);
                    net.Add(section);
                    changed = true;
                    return section;
                }

                var eventConns = EnsureSection("EventConnections");
                var dataConns = EnsureSection("DataConnections");

                void SetParam(System.Xml.Linq.XElement fb, string name, string value)
                {
                    var p = fb.Elements(ns + "Parameter")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == name);
                    if (p == null)
                    {
                        fb.Add(new XElement(ns + "Parameter",
                            new XAttribute("Name", name),
                            new XAttribute("Value", value)));
                        changed = true;
                        return;
                    }
                    if ((string?)p.Attribute("Value") != value)
                    {
                        p.SetAttributeValue("Value", value);
                        changed = true;
                    }
                }

                void RemoveEvent(string source, string destination)
                {
                    foreach (var c in eventConns.Elements(ns + "Connection")
                                 .Where(c => (string?)c.Attribute("Source") == source &&
                                             (string?)c.Attribute("Destination") == destination)
                                 .ToList())
                    {
                        c.Remove();
                        changed = true;
                    }
                }

                void AddEvent(string source, string destination)
                {
                    if (eventConns.Elements(ns + "Connection").Any(c =>
                            (string?)c.Attribute("Source") == source &&
                            (string?)c.Attribute("Destination") == destination))
                        return;
                    eventConns.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", source),
                        new XAttribute("Destination", destination)));
                    changed = true;
                }

                void RemoveDataTo(params string[] destinations)
                {
                    var destinationSet = destinations.ToHashSet(StringComparer.Ordinal);
                    foreach (var c in dataConns.Elements(ns + "Connection")
                                 .Where(c => destinationSet.Contains((string?)c.Attribute("Destination") ?? string.Empty))
                                 .ToList())
                    {
                        c.Remove();
                        changed = true;
                    }
                }

                void AddData(string source, string destination)
                {
                    if (dataConns.Elements(ns + "Connection").Any(c =>
                            (string?)c.Attribute("Source") == source &&
                            (string?)c.Attribute("Destination") == destination))
                        return;
                    dataConns.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", source),
                        new XAttribute("Destination", destination)));
                    changed = true;
                }

                void RemoveSimPosition()
                {
                    foreach (var c in eventConns.Elements(ns + "Connection")
                                 .Where(c =>
                                 {
                                     var s = (string?)c.Attribute("Source") ?? string.Empty;
                                     var d = (string?)c.Attribute("Destination") ?? string.Empty;
                                     return s.StartsWith(simFbName + ".", StringComparison.Ordinal) ||
                                            d.StartsWith(simFbName + ".", StringComparison.Ordinal);
                                 })
                                 .ToList())
                    {
                        c.Remove();
                        changed = true;
                    }

                    foreach (var c in dataConns.Elements(ns + "Connection")
                                 .Where(c =>
                                 {
                                     var s = (string?)c.Attribute("Source") ?? string.Empty;
                                     var d = (string?)c.Attribute("Destination") ?? string.Empty;
                                     return s.StartsWith(simFbName + ".", StringComparison.Ordinal) ||
                                            d.StartsWith(simFbName + ".", StringComparison.Ordinal);
                                 })
                                 .ToList())
                    {
                        c.Remove();
                        changed = true;
                    }

                    foreach (var fb in net.Elements(ns + "FB")
                                 .Where(f => (string?)f.Attribute("Name") == simFbName)
                                 .ToList())
                    {
                        fb.Remove();
                        changed = true;
                    }
                }

                // Inputs stays subscribed to the real physical sensor names.
                SetParam(inputs, "NAME1", "'$${PATH}athome'");
                SetParam(inputs, "NAME2", "'$${PATH}atwork1'");
                SetParam(inputs, "NAME3", "'$${PATH}atWork2'");

                RemoveEvent("Inputs.INITO", "SimPosition.INIT");
                RemoveEvent("SimPosition.INITO", "ActuatorCore.INIT");
                RemoveEvent("ActuatorCore.pst_out", "SimPosition.REQ");
                RemoveEvent("SimPosition.CNF", "FB1.EI");

                RemoveDataTo(
                    "ActuatorCore.atHome", "ActuatorCore.atWork1", "ActuatorCore.atWork2",
                    "IThis.atHome", "IThis.atWork1", "IThis.atWork2",
                    "FaultHandling.atHome", "FaultHandling.atWork1", "FaultHandling.atWork2",
                    "SimPosition.CurrentState");

                RemoveSimPosition();

                AddEvent("Inputs.INITO", "ActuatorCore.INIT");
                // Rig homes on the real atHome sensor, not the ReturnToHomeHandler work->home timer.
                AddData("Inputs.VALUE1", "ActuatorCore.atHome");
                AddData("Inputs.VALUE2", "ActuatorCore.atWork1");
                AddData("Inputs.VALUE3", "ActuatorCore.atWork2");
                AddData("Inputs.VALUE1", "IThis.atHome");
                AddData("Inputs.VALUE2", "IThis.atWork1");
                AddData("Inputs.VALUE3", "IThis.atWork2");
                AddData("Inputs.VALUE1", "FaultHandling.atHome");
                AddData("Inputs.VALUE2", "FaultHandling.atWork1");
                AddData("Inputs.VALUE3", "FaultHandling.atWork2");

                bool hasSimPosition =
                    net.Elements(ns + "FB").Any(f =>
                        string.Equals((string?)f.Attribute("Name"), simFbName, StringComparison.Ordinal) ||
                        string.Equals((string?)f.Attribute("Type"), "SimCentreHomeSensor_7SCH", StringComparison.Ordinal)) ||
                    eventConns.Elements(ns + "Connection").Any(ReferencesSimPosition) ||
                    dataConns.Elements(ns + "Connection").Any(ReferencesSimPosition);

                bool ReferencesSimPosition(XElement connection)
                {
                    var source = (string?)connection.Attribute("Source") ?? string.Empty;
                    var destination = (string?)connection.Attribute("Destination") ?? string.Empty;
                    return source.StartsWith(simFbName + ".", StringComparison.Ordinal) ||
                           destination.StartsWith(simFbName + ".", StringComparison.Ordinal);
                }

                if (hasSimPosition)
                {
                    throw new InvalidOperationException(
                        "Hardware/Test Runtime cannot use simulator centre-home wiring: " +
                        "Seven_State_Actuator_Centre_Home_CAT still contains SimPosition/SimCentreHomeSensor_7SCH.");
                }

                if (changed)
                {
                    SaveXmlWithRetry(doc, fbt);
                    result.PatchesApplied.Add("Seven_State_Actuator_Centre_Home_CAT: simulator position model removed; physical sensor wiring restored");
                    MapperLogger.Info("[Deploy] Centre-Home swivel sim-sensor source normalize: physical sensor wiring restored");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Hardware/Test Runtime cannot continue because the centre-home swivel CAT could not be restored to physical sensor wiring. " +
                    "Close any open Seven_State_Actuator_Centre_Home_CAT editor tab in EAE and regenerate.",
                    ex);
            }
        }

        // Bearing_PnP home is recipe-only: strips any injected poll machinery (HomePoll/PollGate1/PollGate2/
        // PollWindow + connections), adds nothing. The CAT's 'Inputs' SYMLINKMULTIVARDST is sample-on-REQ —
        // if the core stops re-observing positions, fix the CAT/interface, don't re-add a polling FB.
        internal static void StripCatHomeSensorPoll(string eaeProjectDir, string catName, DeployResult result)
        {
            var fbt = FindDeployedFbt(eaeProjectDir, catName + ".fbt");
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add($"{catName}.fbt not found; home-poll strip skipped.");
                return;
            }
            try
            {
                var doc = LoadXmlWithRetry(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "FBNetwork");
                if (net == null) return;

                var pollFbNames = new[] { "HomePoll", "PollWindow", "PollGate1", "PollGate2" };
                bool Has(string n) => net.Elements(ns + "FB").Any(f => (string?)f.Attribute("Name") == n);
                if (!pollFbNames.Any(Has)) return;

                bool RefsPoll(string? ep)
                {
                    var e = ep ?? string.Empty;
                    foreach (var n in pollFbNames)
                        if (e.StartsWith(n + ".", StringComparison.Ordinal)) return true;
                    return false;
                }

                foreach (var f in net.Elements(ns + "FB")
                             .Where(f => pollFbNames.Contains((string?)f.Attribute("Name")))
                             .ToList())
                    f.Remove();
                foreach (var secName in new[] { "EventConnections", "DataConnections" })
                {
                    var cc = net.Element(ns + secName);
                    if (cc == null) continue;
                    foreach (var c in cc.Elements(ns + "Connection")
                                 .Where(c => RefsPoll((string?)c.Attribute("Source"))
                                          || RefsPoll((string?)c.Attribute("Destination")))
                                 .ToList())
                        c.Remove();
                }

                SaveXmlWithRetry(doc, fbt);
                result.PatchesApplied.Add(
                    $"{catName}: removed HomePoll/PollGate1/PollGate2/PollWindow poll machinery + connections (Bearing_PnP home is recipe-only now).");
                MapperLogger.Info($"[Deploy] {catName}.fbt: stripped HomePoll/PollGate/PollWindow (poll removed; home recipe-only)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catName} home-poll strip failed: {ex.Message}");
            }
        }

        // Swivel work-arrival latch: relax=true (rig) fires ToWorkN->AtWorkN on atWorkN=TRUE alone;
        // strict=false (sim) also requires atWorkOther=FALSE.
        internal static void PatchSwivelRelaxWorkLatch(string eaeProjectDir, bool relax, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var latches = new[]
                {
                    ("ToWork1", "AtWork1", "atWork1 = TRUE", "atWork1 = TRUE AND atWork2 = FALSE"),
                    ("ToWork2", "AtWork2", "atWork2 = TRUE", "atWork2 = TRUE AND atWork1 = FALSE"),
                };
                int changed = 0;
                foreach (var (src, dst, relaxed, strict) in latches)
                {
                    var tr = root.Descendants(ns + "ECTransition").FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == src &&
                        (string?)t.Attribute("Destination") == dst);
                    if (tr == null) continue;
                    var want = relax ? relaxed : strict;
                    if ((string?)tr.Attribute("Condition") != want)
                    {
                        tr.SetAttributeValue("Condition", want);
                        changed++;
                    }
                }
                if (changed > 0)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        $"SevenStateCentreHomeActuator.fbt: work-arrival latch {(relax ? "RELAXED (atWorkN=TRUE only)" : "restored (mutually exclusive)")} on {changed} transition(s)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"SevenStateCentreHomeActuator.fbt work-latch patch failed: {ex.Message}");
            }
        }

        internal static void PatchSwivelInterlockEventCarriesStateVal(string eaeProjectDir, bool add, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var ilckEvent = root.Descendants(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "ilck_event");
                if (ilckEvent == null)
                {
                    result.Warnings.Add("SevenStateCentreHomeActuator.fbt: ilck_event not found; state_val sampling skipped.");
                    return;
                }

                bool hasStateVal = ilckEvent.Elements(ns + "With")
                    .Any(w => (string?)w.Attribute("Var") == "state_val");
                bool changed = false;

                if (add && !hasStateVal)
                {
                    ilckEvent.AddFirst(new System.Xml.Linq.XElement(ns + "With",
                        new System.Xml.Linq.XAttribute("Var", "state_val")));
                    changed = true;
                }
                else if (!add && hasStateVal)
                {
                    foreach (var w in ilckEvent.Elements(ns + "With")
                                 .Where(w => (string?)w.Attribute("Var") == "state_val")
                                 .ToList())
                    {
                        w.Remove();
                        changed = true;
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        $"SevenStateCentreHomeActuator.fbt: ilck_event {(add ? "samples" : "no longer samples")} state_val");
                    MapperLogger.Info(
                        $"[Deploy] SevenStateCentreHomeActuator.fbt: ilck_event state_val sampling add={add}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"SevenStateCentreHomeActuator.fbt ilck_event state_val patch failed: {ex.Message}");
            }
        }



        // Gated SwivelBrakeHome: a timed reverse-coil brake at centre so the swivel homes directly from
        // AtWork1 (Disassembly) without coasting into the ejector. Directional — at AtHome the algorithm
        // reverses the coil only when homing from AtWork1; from AtWork2 (Assembly) it de-energises unchanged.
        // No-op when disabled; the ECC/CAT are force-refreshed so a flag flip reverts.
        internal static void PatchSwivelBrakeHome(string eaeProjectDir, bool enabled, int brakeMs, DeployResult result)
        {
            if (!enabled) return;
            if (brakeMs <= 0) brakeMs = 500;

            // Core ECC: SevenStateCentreHomeActuator.fbt
            var ecc = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(ecc))
            {
                ecc = Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(ecc)) { result.Warnings.Add("Swivel brake: core ECC not found; skipped."); return; }
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(ecc, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root; if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                // 'atHome' -> directional brake (reverse the coil only when homing from AtWork1).
                var atHomeAlgo = root.Descendants(ns + "Algorithm").FirstOrDefault(a => (string?)a.Attribute("Name") == "atHome");
                if (atHomeAlgo == null) { result.Warnings.Add("Swivel brake: 'atHome' algorithm not found; skipped."); return; }
                atHomeAlgo.Element(ns + "ST")?.ReplaceNodes(new XCData(
                    "current_state_to_process := 6;\r\nIF outputToWork2 = TRUE THEN\r\n\toutputToWork1:= TRUE;\r\n\toutputToWork2:= FALSE;\r\nELSE\r\n\toutputToWork1:= FALSE;\r\n\toutputToWork2:= FALSE;\r\nEND_IF;\r\n"));

                root.Descendants(ns + "Algorithm").FirstOrDefault(a => (string?)a.Attribute("Name") == "AtHomeInit")
                    ?.Element(ns + "ST")?.ReplaceNodes(new XCData(
                        "current_state_to_process := 0;\r\noutputToWork1:= FALSE;\r\noutputToWork2:= FALSE;\r\n"));

                var eos = root.Descendants(ns + "EventOutputs").FirstOrDefault();
                if (eos != null && !eos.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "brake_start"))
                    eos.Add(new XElement(ns + "Event", new XAttribute("Name", "brake_start"),
                        new XAttribute("Comment", "centre-home brake pulse start")));
                var eis = root.Descendants(ns + "EventInputs").FirstOrDefault();
                if (eis != null && !eis.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "brake_done"))
                    eis.Add(new XElement(ns + "Event", new XAttribute("Name", "brake_done"),
                        new XAttribute("Comment", "centre-home brake pulse elapsed")));

                var atHome = root.Descendants(ns + "ECState").FirstOrDefault(s => (string?)s.Attribute("Name") == "AtHome");
                if (atHome != null && !atHome.Elements(ns + "ECAction").Any(a => (string?)a.Attribute("Output") == "brake_start"))
                    atHome.Add(new XElement(ns + "ECAction", new XAttribute("Output", "brake_start")));

                // AtHome -> AtHomeInit non-sensor arc = brake_done (a safety cap only; the sensor arc below is primary).
                root.Descendants(ns + "ECTransition").FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "AtHome" && (string?)t.Attribute("Destination") == "AtHomeInit"
                        && (string?)t.Attribute("Condition") != "atHome = FALSE")
                    ?.SetAttributeValue("Condition", "brake_done");

                // CRITICAL: AtHomeInit must emit output_event (drives the Output SYMLINKMULTIVARSRC to write
                // both coils FALSE) — stock emits only pst_out, so the reverse coil stays energised and overshoots to AtWork1.
                var atHomeInit = root.Descendants(ns + "ECState").FirstOrDefault(s => (string?)s.Attribute("Name") == "AtHomeInit");
                if (atHomeInit != null && !atHomeInit.Elements(ns + "ECAction").Any(a => (string?)a.Attribute("Output") == "output_event"))
                    atHomeInit.Add(new XElement(ns + "ECAction", new XAttribute("Output", "output_event")));

                // SENSOR-STOPPED de-energise (the real fix): AtHome -> AtHomeInit on atHome=FALSE cuts the
                // coil at the DI02 centre-window edge, not after the fixed brake_done timer (which over-drove to AtWork1).
                var brakeDoneArc = root.Descendants(ns + "ECTransition").FirstOrDefault(t =>
                    (string?)t.Attribute("Source") == "AtHome" && (string?)t.Attribute("Destination") == "AtHomeInit" &&
                    (string?)t.Attribute("Condition") == "brake_done");
                bool hasSensorArc = root.Descendants(ns + "ECTransition").Any(t =>
                    (string?)t.Attribute("Source") == "AtHome" && (string?)t.Attribute("Destination") == "AtHomeInit" &&
                    (string?)t.Attribute("Condition") == "atHome = FALSE");
                if (brakeDoneArc != null && !hasSensorArc)
                    brakeDoneArc.AddBeforeSelf(new XElement(ns + "ECTransition",
                        new XAttribute("Source", "AtHome"), new XAttribute("Destination", "AtHomeInit"),
                        new XAttribute("Condition", "atHome = FALSE"),
                        new XAttribute("x", "1445.13"), new XAttribute("y", "2470.42")));

                doc.Save(ecc);
                result.PatchesApplied.Add("SevenStateCentreHomeActuator.fbt: SENSOR-STOPPED centre-home brake (atHome reverses the coil; AtHome->AtHomeInit on atHome=FALSE cuts at the centre-window edge; AtHomeInit now PUBLISHES output_event so the coil actually releases; brake_done = safety cap)");
            }
            catch (Exception ex) { result.Warnings.Add($"Swivel brake core ECC patch failed: {ex.Message}"); return; }

            // Composite: brakeTimer E_DELAY + wiring
            var cat = Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"),
                "Seven_State_Actuator_Centre_Home_CAT.fbt", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrEmpty(cat) || !File.Exists(cat)) { result.Warnings.Add("Swivel brake: composite not found; skipped."); return; }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(cat, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root; if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var net = root.Descendants(ns + "FBNetwork").FirstOrDefault();
                var actuator = net?.Elements(ns + "FB").FirstOrDefault(f => (string?)f.Attribute("Name") == "ActuatorCore");
                if (net == null || actuator == null) { result.Warnings.Add("Swivel brake: composite ActuatorCore missing; skipped."); return; }

                var existing = net.Elements(ns + "FB").FirstOrDefault(f => (string?)f.Attribute("Name") == "brakeTimer");
                if (existing == null)
                {
                    int maxId = net.Elements(ns + "FB")
                        .Select(f => int.TryParse((string?)f.Attribute("ID"), out var v) ? v : 0).DefaultIfEmpty(0).Max();
                    int id = maxId + 1;
                    actuator.AddAfterSelf(new XElement(ns + "FB",
                        new XAttribute("ID", id), new XAttribute("Name", "brakeTimer"),
                        new XAttribute("Type", "E_DELAY"), new XAttribute("x", "3100"), new XAttribute("y", "4880"),
                        new XAttribute("Namespace", "IEC61499.Standard"),
                        new XElement(ns + "Parameter", new XAttribute("Name", "DT"), new XAttribute("Value", $"T#{brakeMs}ms"))));
                    var idc = root.Descendants(ns + "Attribute").FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
                    if (idc != null && int.TryParse((string?)idc.Attribute("Value"), out var c) && c <= id)
                        idc.SetAttributeValue("Value", id + 1);
                }
                else
                {
                    existing.Elements(ns + "Parameter").FirstOrDefault(p => (string?)p.Attribute("Name") == "DT")
                        ?.SetAttributeValue("Value", $"T#{brakeMs}ms");
                }

                var evc = net.Elements(ns + "EventConnections").FirstOrDefault();
                if (evc != null)
                {
                    void AddConn(string src, string dst)
                    {
                        if (!evc.Elements(ns + "Connection").Any(c =>
                                (string?)c.Attribute("Source") == src && (string?)c.Attribute("Destination") == dst))
                            evc.Add(new XElement(ns + "Connection",
                                new XAttribute("Source", src), new XAttribute("Destination", dst)));
                    }
                    AddConn("ActuatorCore.brake_start", "brakeTimer.START");
                    AddConn("brakeTimer.EO", "ActuatorCore.brake_done");
                }

                doc.Save(cat);
                result.PatchesApplied.Add($"Seven_State_Actuator_Centre_Home_CAT.fbt: brakeTimer E_DELAY (T#{brakeMs}ms) wired brake_start->START, EO->brake_done");
                MapperLogger.Info($"[Deploy] centre-home BRAKE ON: reverse-coil pulse {brakeMs}ms at centre (errs toward AtWork1/away from ejector)");
            }
            catch (Exception ex) { result.Warnings.Add($"Swivel brake composite patch failed: {ex.Message}"); }
        }

        internal static void PatchSwivelAtHomeInitRecovery(string eaeProjectDir, bool addArc, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
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
                    result.Warnings.Add(
                        "SevenStateCentreHomeActuator.fbt: no <ECC>; AtHomeInit sensor-recovery skipped.");
                    return;
                }

                // SELF-HOME ON POWER-UP: the swivel has no spring-centre, so the only way HOME is its
                // initial state is to DRIVE it there — rig (addArc) redirects INIT work-position arcs to
                // ToHome; sim (!addArc) restores INIT->work and strips the self-home arcs.
                // SAFETY: the arm physically swings toward centre at power-up — the swing path must be clear before a cold download.
                var initArcs = ecc.Elements(ns + "ECTransition")
                    .Where(t => (string?)t.Attribute("Source") == "INIT").ToList();
                bool IsSelfHomeArc(System.Xml.Linq.XElement t) =>
                    (string?)t.Attribute("Source") == "AtHomeInit" &&
                    (string?)t.Attribute("Destination") == "ToHome";
                bool IsStaleWorkArc(System.Xml.Linq.XElement t) =>
                    (string?)t.Attribute("Source") == "AtHomeInit" &&
                    ((string?)t.Attribute("Destination") == "AtWork1" ||
                     (string?)t.Attribute("Destination") == "AtWork2");

                if (!addArc)
                {
                    bool ch = false;
                    foreach (var t in initArcs)
                    {
                        if ((string?)t.Attribute("Destination") != "ToHome") continue;
                        var cond = (string?)t.Attribute("Condition") ?? string.Empty;
                        t.SetAttributeValue("Destination",
                            cond.Contains("atWork1 = TRUE") ? "AtWork1" : "ToWork2");
                        ch = true;
                    }
                    foreach (var t in ecc.Elements(ns + "ECTransition")
                                 .Where(x => IsSelfHomeArc(x) || IsStaleWorkArc(x)).ToList())
                    { t.Remove(); ch = true; }
                    if (ch)
                    {
                        doc.Save(fbt);
                        result.PatchesApplied.Add(
                            "SevenStateCentreHomeActuator.fbt: INIT boot arcs restored to work states; self-home arcs stripped (sim path)");
                    }
                    return;
                }

                // RIG: drive home via INIT only. AtHomeInit must have no self-driving exit (a self-home arc
                // re-fires on noisy DIs and cycles the swivel) — redirect INIT to ToHome and strip every
                // AtHomeInit -> {ToHome,AtWork1,AtWork2} arc; the stock AtHomeInit -> ToWork1/ToWork2 (Pick/Place) arcs stay.
                bool changed = false;

                foreach (var t in initArcs)
                {
                    var dest = (string?)t.Attribute("Destination");
                    if (dest == "AtWork1" || dest == "ToWork2")
                    { t.SetAttributeValue("Destination", "ToHome"); changed = true; }
                }

                foreach (var t in ecc.Elements(ns + "ECTransition")
                             .Where(x => IsSelfHomeArc(x) || IsStaleWorkArc(x)).ToList())
                { t.Remove(); changed = true; }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        "SevenStateCentreHomeActuator.fbt: HOME-ON-INIT (boot only) -- INIT work-position arcs redirected to ToHome; " +
                        "ALL AtHomeInit->{ToHome,AtWork1,AtWork2} self-home/recovery arcs stripped (they re-fired on noisy sensors and cycled the swivel)");
                    MapperLogger.Info(
                        "[Deploy] SevenStateCentreHomeActuator.fbt: self-homes at power-up via INIT->ToHome; AtHomeInit is now a stable rest state (no self-driving exit)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"SevenStateCentreHomeActuator.fbt AtHomeInit sensor-recovery patch failed: {ex.Message}");
            }
        }

        // Wires AtHome to the coil-clearing 'atHome' algorithm + output_event so the Output
        // SYMLINKMULTIVARSRC writes both work coils FALSE (swaps which existing algorithm AtHome runs).
        internal static void PatchSwivelAtHomeCoilClear(string eaeProjectDir, bool clearCoils, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var atHomeState = root.Descendants(ns + "ECState")
                    .FirstOrDefault(s => (string?)s.Attribute("Name") == "AtHome");
                if (atHomeState == null)
                {
                    result.Warnings.Add(
                        "SevenStateCentreHomeActuator.fbt: no AtHome ECState; coil-clear patch skipped.");
                    return;
                }
                var ecAction = atHomeState.Elements(ns + "ECAction").FirstOrDefault();
                if (ecAction == null)
                {
                    result.Warnings.Add(
                        "SevenStateCentreHomeActuator.fbt: AtHome has no ECAction; coil-clear patch skipped.");
                    return;
                }

                var algoNames = root.Descendants(ns + "Algorithm")
                    .Select(a => (string?)a.Attribute("Name"))
                    .Where(n => n != null)
                    .ToHashSet();
                string want = clearCoils ? "atHome" : "AtHomeEnd";
                if (!algoNames.Contains(want))
                {
                    result.Warnings.Add(
                        $"SevenStateCentreHomeActuator.fbt: algorithm '{want}' not found; AtHome coil-clear patch skipped.");
                    return;
                }

                var current = (string?)ecAction.Attribute("Algorithm");
                bool changed = false;
                if (current != want)
                {
                    ecAction.SetAttributeValue("Algorithm", want);
                    changed = true;
                }

                var outputEventAction = atHomeState.Elements(ns + "ECAction")
                    .FirstOrDefault(a => (string?)a.Attribute("Output") == "output_event");
                if (clearCoils)
                {
                    if (outputEventAction == null)
                    {
                        atHomeState.Add(new XElement(ns + "ECAction",
                            new XAttribute("Output", "output_event")));
                        changed = true;
                    }
                }
                else if (outputEventAction != null)
                {
                    outputEventAction.Remove();
                    changed = true;
                }

                if (!changed) return;
                doc.Save(fbt);

                result.PatchesApplied.Add(
                    $"SevenStateCentreHomeActuator.fbt: AtHome ECState now runs '{want}' " +
                    (clearCoils
                        ? "and emits output_event (clears both coils at home)"
                        : "without output_event (legacy no-clear mode)"));
                MapperLogger.Info(
                    $"[Deploy] SevenStateCentreHomeActuator.fbt: AtHome -> '{want}' " +
                    (clearCoils ? "(coils cleared and published at home)" : "(coils held)"));
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"SevenStateCentreHomeActuator.fbt AtHome coil-clear patch failed: {ex.Message}");
            }
        }

        // Gated MapperConfig.SwivelHomeHoldBothCoils (default OFF): OFF de-energises both 'atHome' coils
        // (a venting swivel rests off-centre); TRUE holds both to drive a cylinder into a mechanical mid-stop.
        // SAFETY: with NO mid-stop, both-on drives toward an extreme — rig only, e-stop ready, abort if it heads to Work2.
        internal static void PatchSwivelAtHomeBothCoils(string eaeProjectDir, bool holdBothCoils, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "SevenStateCentreHomeActuator.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "SevenStateCentreHomeActuator.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var atHomeAlgo = root.Descendants(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "atHome");
                var st = atHomeAlgo?.Element(ns + "ST");
                if (st == null)
                {
                    result.Warnings.Add(
                        "SevenStateCentreHomeActuator.fbt: no 'atHome' algorithm ST; home-hold patch skipped.");
                    return;
                }

                string coil = holdBothCoils ? "TRUE" : "FALSE";
                string body = st.Value;
                string newBody = System.Text.RegularExpressions.Regex.Replace(
                    body, @"outputToWork1:=\s*(?:TRUE|FALSE);", $"outputToWork1:= {coil};");
                newBody = System.Text.RegularExpressions.Regex.Replace(
                    newBody, @"outputToWork2:=\s*(?:TRUE|FALSE);", $"outputToWork2:= {coil};");
                if (newBody == body) return;

                st.ReplaceNodes(new System.Xml.Linq.XCData(newBody));
                doc.Save(fbt);

                result.PatchesApplied.Add(
                    $"SevenStateCentreHomeActuator.fbt: home 'atHome' coils -> {coil}/{coil} " +
                    (holdBothCoils ? "(HOLD at mid-stop -- centre-home overshoot fix)" : "(de-energise -- default)"));
                MapperLogger.Info(
                    $"[Deploy] SevenStateCentreHomeActuator.fbt: home coils -> {coil}/{coil} " +
                    (holdBothCoils ? "(both-coils hold at centre)" : "(de-energise)"));
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"SevenStateCentreHomeActuator.fbt home-hold patch failed: {ex.Message}");
            }
        }
    }
}
