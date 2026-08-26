using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using static CodeGen.Services.FbtXmlEditor;
using System.IO;
using CodeGen.Mapping;
using CodeGen.Models;

namespace CodeGen.Services
{
    // Deploy-time patches for the centre-home swivel CAT (Seven_State_Actuator_Centre_Home).
    internal static class SwivelCatPatcher
    {
        // Keeps Inputs on the real sensor symlinks; hard-fails if SimCentreHomeSensor_7SCH survives.
        internal static void NormalizeSwivelSimSensorSource(FbtEditScope scope, DeployResult result)
        {
            var fbt = FindDeployedFbt(scope.Root, TemplateManifest.FbtOf("centreHomeCat"));
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Seven_State_Actuator_Centre_Home_CAT.fbt not found; swivel sim-sensor normalize skipped.");
                return;
            }
            try
            {
                var doc = LoadXmlWithRetry(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace, scope.Retries);
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

                var events = new ConnectionSet(eventConns, ns);
                var data = new ConnectionSet(dataConns, ns);

                void RemoveEvent(string source, string destination)
                    => changed |= events.Remove(source, destination);

                void AddEvent(string source, string destination)
                    => changed |= events.Add(source, destination);

                void RemoveDataTo(params string[] destinations)
                    => changed |= data.RemoveTo(destinations);

                void AddData(string source, string destination)
                    => changed |= data.Add(source, destination);

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
                    SaveXmlWithRetry(doc, fbt, scope.Retries);
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

        internal static void EnsureSensorBoolReadEvent(FbtEditScope scope, DeployResult result)
            => EditDeployedFbt(scope, TemplateManifest.FbtOf("sensorCat"), "Sensor_Bool_CAT RD event inject failed", result,
                (doc, root, ns, fbt) =>
            {
                var ei = root.Element(ns + "InterfaceList")?.Element(ns + "EventInputs");
                var net = root.Element(ns + "FBNetwork");
                if (ei == null || net == null) return;
                var ec = net.Element(ns + "EventConnections");
                if (ec == null) return;

                bool changed = false;

                if (!ei.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "RD"))
                {
                    ei.Add(new XElement(ns + "Event", new XAttribute("Name", "RD"),
                        new XAttribute("Comment", "Re-sample the input (for broker-fed sensors that have no I/O-scan event)")));

                    var rdMarker = new XElement(ns + "Input", new XAttribute("Name", "RD"),
                        new XAttribute("x", "12"), new XAttribute("y", "80"), new XAttribute("Type", "Event"));
                    var initMarker = net.Elements(ns + "Input").FirstOrDefault(i => (string?)i.Attribute("Name") == "INIT");
                    if (initMarker != null) initMarker.AddAfterSelf(rdMarker); else net.Add(rdMarker);

                    ec.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", "RD"), new XAttribute("Destination", "FB2.REQ")));
                    changed = true;
                }

                // RD must NOT reach StateHandling.REQ: that bypasses Sensor_Bool's change gate, so every
                // cyclic re-read emits a ring frame and a scan-driven sensor publishes forever.
                // Reconciled rather than merely not-added, so a tree deployed with the bypass self-heals.
                foreach (var stale in ec.Elements(ns + "Connection")
                             .Where(c => (string?)c.Attribute("Source") == "RD" &&
                                         (string?)c.Attribute("Destination") == "StateHandling.REQ")
                             .ToList())
                {
                    stale.Remove();
                    changed = true;
                }

                if (!ec.Elements(ns + "Connection").Any(c =>
                        (string?)c.Attribute("Source") == "StateHandling.INITO" &&
                        (string?)c.Attribute("Destination") == "FB2.REQ"))
                {
                    ec.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", "StateHandling.INITO"),
                        new XAttribute("Destination", "FB2.REQ")));
                    changed = true;
                }

                // Superseded by the addressed refresh below; reconciled away so a tree deployed with it heals.
                foreach (var stale in ec.Elements(ns + "Connection")
                             .Where(c => (string?)c.Attribute("Source") == "stateRprtCmd_in.CNF" &&
                                         (string?)c.Attribute("Destination") == "FB2.REQ")
                             .ToList())
                {
                    stale.Remove();
                    changed = true;
                }

                // Addressed refresh: sample the input, THEN report it even when unchanged. Serial, never
                // fanned out -- publishing from the sampling event would republish the CACHED level.
                // See Docs/PATCH_RATIONALES P-4.
                foreach (var (src, dst) in new[]
                         {
                             ("StateHandling.CNF", "FB1.RPT"),
                             ("FB1.SMP", "FB2.REQ"),
                         })
                {
                    if (ec.Elements(ns + "Connection").Any(c =>
                            (string?)c.Attribute("Source") == src &&
                            (string?)c.Attribute("Destination") == dst)) continue;
                    ec.Add(new XElement(ns + "Connection",
                        new XAttribute("Source", src), new XAttribute("Destination", dst)));
                    changed = true;
                }

                if (!changed) return;

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    "Sensor_Bool_CAT: addressed refresh wired sample-then-report (StateHandling.CNF -> FB1.RPT -> "
                    + "FB1.SMP -> FB2.REQ -> FB1.REQ -> publish); RD still reports only through the change gate.");
                MapperLogger.Info("[Deploy] Sensor_Bool_CAT.fbt: addressed refresh (sample-then-report) wired");
            }, notFoundNote: "Sensor_Bool_CAT.fbt not found; RD event inject skipped.");

        private static void EditSwivelCore(FbtEditScope scope, string failNote, DeployResult result,
            Action<XDocument, XElement, XNamespace, string> edit)
            => EditDeployedFbt(scope, TemplateManifest.FbtOf("centreHomeCore"), failNote, result, edit);

        // A reverse-coil brake at centre so the swivel homes directly from AtWork1 without coasting into
        // the ejector. Directional; from AtWork2 it de-energises unchanged.
        internal static void PatchSwivelBrakeHome(FbtEditScope scope, int brakeMs, DeployResult result)
        {
            // A second, undeclared default here would silently outrank config.yaml's declared one.
            if (brakeMs <= 0)
                throw new InvalidOperationException(
                    "[Swivel] config.yaml declares no bearingPnpHomeBrakeMs, so the brake pulse that " +
                    "stops the arm at its centre reference has no duration. Declare it; there is no " +
                    "safe default for how long to hold a coil.");

            var ecc = Path.Combine(scope.Root, "IEC61499", TemplateManifest.FbtOf("centreHomeCore"));
            if (!File.Exists(ecc))
            {
                ecc = Directory.EnumerateFiles(Path.Combine(scope.Root, "IEC61499"),
                        TemplateManifest.FbtOf("centreHomeCore"), SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
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

                // brake_done is a safety cap only; the sensor arc below is primary.
                root.Descendants(ns + "ECTransition").FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "AtHome" && (string?)t.Attribute("Destination") == "AtHomeInit"
                        && (string?)t.Attribute("Condition") != "atHome = FALSE")
                    ?.SetAttributeValue("Condition", "brake_done");

                // CRITICAL: AtHomeInit must emit output_event so both coils are written FALSE. Stock emits
                // only pst_out, leaving the reverse coil energised so the arm overshoots to AtWork1.
                var atHomeInit = root.Descendants(ns + "ECState").FirstOrDefault(s => (string?)s.Attribute("Name") == "AtHomeInit");
                if (atHomeInit != null && !atHomeInit.Elements(ns + "ECAction").Any(a => (string?)a.Attribute("Output") == "output_event"))
                    atHomeInit.Add(new XElement(ns + "ECAction", new XAttribute("Output", "output_event")));

                // Sensor-stopped de-energise: cut the coil at the DI02 centre-window edge, not on a timer.
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
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Swivel brake core ECC patch failed: {ex.Message} — a deploy-time patch could not be applied, so the deployed type does not have the shape the planner's parameters name. Usually EAE holding the .fbt open during Generate: CLOSE EAE and Generate again. Generation ABORTED rather than shipping a tree EAE will not run.", ex);
            }

            var cat = Directory.EnumerateFiles(Path.Combine(scope.Root, "IEC61499"),
                TemplateManifest.FbtOf("centreHomeCat"), SearchOption.AllDirectories).FirstOrDefault();
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
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Swivel brake composite patch failed: {ex.Message} — a deploy-time patch could not be applied, so the deployed type does not have the shape the planner's parameters name. Usually EAE holding the .fbt open during Generate: CLOSE EAE and Generate again. Generation ABORTED rather than shipping a tree EAE will not run.", ex);
            }
        }

        // SOLE writer of the swivel core's INIT arcs, derived from the twin's Initial_State. Where the work
        // sensors read nothing the arm is CLASSIFIED at centre; where one reads, it is DRIVEN to centre.
        // Nothing is assumed about a position no sensor reports, and a swivel that does not declare its centre
        // stop as its initial state fails generation rather than get an invented startup policy.
        internal static void PatchSwivelStartup(FbtEditScope scope,
            CodeGen.Translation.GenerationContext ctx, DeployResult result)
        {
            var startup = ResolveStartupState(ctx);
            // No centre-home swivel in this model: the type is deployed but never instantiated, so leave
            // the shipped template alone rather than ship an ECC that can never leave INIT.
            if (startup.Count == 0) return;
            EditSwivelCore(scope, "SevenStateCentreHomeActuator.fbt startup patch failed", result,
                (doc, root, ns, fbt) =>
            {
                var basic = root.Descendants(ns + "BasicFB").FirstOrDefault();
                var ecc = basic?.Element(ns + "ECC");
                if (basic == null || ecc == null)
                {
                    result.Warnings.Add("SevenStateCentreHomeActuator.fbt: no BasicFB/ECC; startup patch skipped.");
                    return;
                }

                // Retire any earlier startup-classification states so re-deploying converges.
                foreach (var name in new[] { "StartupAtWork1", "StartupAtWork2" })
                {
                    RemoveElems(ecc.Elements(ns + "ECState"), e => (string?)e.Attribute("Name") == name);
                    RemoveElems(basic.Elements(ns + "Algorithm"), a => (string?)a.Attribute("Name") == name);
                }
                RemoveElems(ecc.Elements(ns + "ECTransition"), e =>
                    (string?)e.Attribute("Source") == "INIT"
                    || ((string?)e.Attribute("Source") ?? "").StartsWith("Startup", StringComparison.Ordinal)
                    || ((string?)e.Attribute("Destination") ?? "").StartsWith("Startup", StringComparison.Ordinal));

                foreach (var (destination, condition) in startup)
                    ecc.Add(new XElement(ns + "ECTransition",
                        new XAttribute("Source", "INIT"),
                        new XAttribute("Destination", destination),
                        new XAttribute("Condition", condition)));

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    $"SevenStateCentreHomeActuator.fbt: {startup.Count} startup arc(s) derived from the twin's " +
                    "declared initial state");
            });
        }

        // Startup vocabulary: AtHomeInit classifies (both coils FALSE, reports 0); ToHome drives to centre.
        private static readonly (string Sensor, string Destination)[] StartupFromWorkSensor =
        {
            ("atWork1", "ToHome"),
            ("atWork2", "ToHome"),
        };

        // The arcs live on the shared TYPE, so two instances with different declared starts cannot both be honoured.
        private static List<(string Destination, string Condition)> ResolveStartupState(CodeGen.Translation.GenerationContext ctx)
        {
            var swivels = ctx.Station.Actuators
                .Where(a => string.Equals(ctx.CatTypes[a.Name.Trim()],
                    TemplateMap.SevenStateCentreHomeCat, StringComparison.Ordinal))
                .ToList();
            if (swivels.Count == 0) return new List<(string, string)>();

            var declared = swivels
                .Select(c => (Component: c, State: c.States.FirstOrDefault(s => s.InitialState)))
                .ToList();

            var undeclared = declared.Where(d => d.State == null).Select(d => d.Component.Name).ToList();
            if (undeclared.Count > 0)
                throw new InvalidOperationException(
                    $"[Startup] {string.Join(", ", undeclared)} resolve to {TemplateMap.SevenStateCentreHomeCat} " +
                    "but declare no Initial_State, so where the arm starts a cycle is unstated and the startup " +
                    "arcs cannot be derived. Mark the centre stop as the initial state in Control.xml.");

            var atWork = declared.Where(d => !d.State!.StaticState || d.State.StateNumber != CentreHomeStop).ToList();
            if (atWork.Count > 0)
                throw new InvalidOperationException(
                    "[Startup] " + string.Join("; ", atWork.Select(d =>
                        $"'{d.Component.Name}' declares initial state '{d.State!.Name}' (State_Number " +
                        $"{d.State.StateNumber}, StaticState {d.State.StaticState})")) +
                    $". {TemplateMap.SevenStateCentreHomeCat} can only establish its centre stop " +
                    $"(State_Number {CentreHomeStop}) at startup: from anywhere else it would have to move the arm " +
                    "to a position no sensor confirms. Declare the centre stop as the initial state, or model the " +
                    "actuator with a CAT whose startup contract covers that position.");

            // Classify home only when NEITHER work sensor reads; drive home from whichever one does.
            var noWork = string.Join(" AND ", StartupFromWorkSensor.Select(s => s.Sensor + " = FALSE"));
            var arcs = new List<(string, string)> { ("AtHomeInit", noWork) };
            foreach (var (sensor, destination) in StartupFromWorkSensor)
                arcs.Add((destination, string.Join(" AND ",
                    StartupFromWorkSensor.Select(o => o.Sensor + (o.Sensor == sensor ? " = TRUE" : " = FALSE"))
                        .Append("atHome = FALSE"))));
            return arcs;
        }

        // The centre-home CAT settles its centre stop to 0; the recipe commands use the same encoding.
        private const int CentreHomeStop = 0;

    }
}
