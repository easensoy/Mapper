using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
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

        // Establish the initial sensor level and support explicit broker re-reads.
        internal static void EnsureSensorBoolReadEvent(string eaeProjectDir, DeployResult result)
            => EditDeployedFbt(eaeProjectDir, "Sensor_Bool_CAT.fbt", "Sensor_Bool_CAT RD event inject failed", result,
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

                // RD must NOT reach StateHandling.REQ. Sensor_Bool's ECC has no same-level self-transition
                // (Sensor_TRUE/Sensor_FALSE are entered only on a genuine level change), so the chain
                // RD -> FB2.REQ -> FB2.CNF -> FB1.REQ -> FB1.CNF -> StateHandling.REQ publishes onto the ring
                // exactly when the level changes, and INITO -> FB2.REQ still establishes the level at boot.
                // A direct RD -> StateHandling.REQ bypasses that gate and makes every cyclic re-read emit a
                // ring frame, so a broker-fed sensor driven by a free-running scan publishes forever — the
                // process engines then see state_change (and therefore SCNF) climb without bound while idle.
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

                // An unconditional per-frame re-sample buys nothing: FB1 reports only on a level change, so a
                // sensor whose ECC already holds the right level stays silent no matter how often it re-reads.
                // Superseded by the addressed refresh below; reconciled away so a tree deployed with it heals.
                foreach (var stale in ec.Elements(ns + "Connection")
                             .Where(c => (string?)c.Attribute("Source") == "stateRprtCmd_in.CNF" &&
                                         (string?)c.Attribute("Destination") == "FB2.REQ")
                             .ToList())
                {
                    stale.Remove();
                    changed = true;
                }

                // Addressed refresh: sample the physical input, THEN report it even when it has not changed.
                // A sensor announces a level exactly once, on the edge that produced it. A level that was
                // already true before this PLC started produces no edge at all, and the single frame emitted at
                // INIT is lost if the consuming ring is not up yet -- after which nothing can re-announce it and
                // the consumer's WAIT is dead until the sensor is physically toggled.
                //
                // StateHandling.CNF fires ONLY from updateComponentState's BREQ state, which is entered only when
                // component_state_in.dest_name = name. Reports set dest_name := '', so no report can trigger any
                // sensor: only a frame that names this component does. That makes the refresh strictly bounded --
                // one request in, one report out, nothing while the line is idle.
                //
                // The order matters and must be serial, not fanned out. Driving StateHandling.REQ from the same
                // event as the sample would publish the CACHED state_sts, which for an active-low input can
                // briefly report the wrong level and release a gate early. So the request enters FB1 (RPT), FB1
                // emits SMP, SMP samples FB2, FB2.CNF re-enters FB1 (REQ) and only then does FB1.CNF publish:
                //     StateHandling.CNF -> FB1.RPT -> FB1.SMP -> FB2.REQ -> FB2.CNF -> FB1.REQ -> FB1.CNF -> publish
                // RPT parks FB1's ECC in START, where both level transitions are unconditionally available, so the
                // following sample always emits CNF -- that is what makes an unchanged level report.
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

        // Gives Sensor_Bool the two ports the addressed refresh needs, and the one ECC state that makes an
        // UNCHANGED level report. The ECC is otherwise change-only: Sensor_TRUE/Sensor_FALSE are entered solely
        // on the opposite level, so once a level is latched no amount of re-sampling emits anything.
        //
        // RPT parks the ECC in START via a transient Arm state whose only action is SMP (the sample request).
        // From START both level transitions are unconditionally available, so the sample that SMP triggers
        // always fires one of them and therefore always emits CNF -- carrying the value just read, not a cached
        // one. Arm returns to START unconditionally, so the ECC never lingers there.
        //
        // Nothing else drives RPT: it comes only from updateComponentState's addressed CNF, so the free-running
        // I/O-scan push (FB2.CNF -> REQ, which every HCF-bound sensor relies on) keeps its change gate intact
        // and cannot be turned into a per-scan publisher.
        internal static void EnsureSensorBoolRefreshPath(string eaeProjectDir, DeployResult result)
            => EditDeployedFbt(eaeProjectDir, "Sensor_Bool.fbt", "Sensor_Bool refresh path inject failed", result,
                (doc, root, ns, fbt) =>
            {
                var iface = root.Element(ns + "InterfaceList");
                var ecc = root.Element(ns + "BasicFB")?.Element(ns + "ECC");
                if (iface == null || ecc == null) return;
                var ei = iface.Element(ns + "EventInputs");
                var eo = iface.Element(ns + "EventOutputs");
                if (ei == null || eo == null) return;

                bool changed = false;

                if (!ei.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "RPT"))
                {
                    ei.Add(new XElement(ns + "Event", new XAttribute("Name", "RPT"),
                        new XAttribute("Comment",
                            "Addressed refresh: sample the input, then report it even if it has not changed")));
                    changed = true;
                }

                if (!eo.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "SMP"))
                {
                    eo.Add(new XElement(ns + "Event", new XAttribute("Name", "SMP"),
                        new XAttribute("Comment", "Sample request issued before reporting")));
                    changed = true;
                }

                if (!ecc.Elements(ns + "ECState").Any(s => (string?)s.Attribute("Name") == "Arm"))
                {
                    var arm = new XElement(ns + "ECState",
                        new XAttribute("Name", "Arm"),
                        new XAttribute("Comment", "Request a fresh sample, then re-enter START so the next REQ reports"),
                        new XAttribute("x", "300"), new XAttribute("y", "1100"),
                        new XElement(ns + "ECAction", new XAttribute("Output", "SMP")));
                    var lastState = ecc.Elements(ns + "ECState").LastOrDefault();
                    if (lastState != null) lastState.AddAfterSelf(arm); else ecc.AddFirst(arm);
                    changed = true;
                }

                // Every level state must be able to accept a refresh, otherwise a sensor sitting in
                // Sensor_TRUE/Sensor_FALSE (the normal case) could not be asked.
                foreach (var (from, to, cond) in new[]
                         {
                             ("START", "Arm", "RPT"),
                             ("Sensor_TRUE", "Arm", "RPT"),
                             ("Sensor_FALSE", "Arm", "RPT"),
                             ("Arm", "START", "1"),
                         })
                {
                    if (ecc.Elements(ns + "ECTransition").Any(t =>
                            (string?)t.Attribute("Source") == from &&
                            (string?)t.Attribute("Destination") == to &&
                            (string?)t.Attribute("Condition") == cond)) continue;
                    ecc.Add(new XElement(ns + "ECTransition",
                        new XAttribute("Source", from), new XAttribute("Destination", to),
                        new XAttribute("Condition", cond),
                        new XAttribute("x", "300"), new XAttribute("y", "1100")));
                    changed = true;
                }

                if (!changed) return;

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    "Sensor_Bool: RPT/SMP refresh ports + Arm state added - an addressed request samples the "
                    + "input and reports it even when the level is unchanged.");
                MapperLogger.Info("[Deploy] Sensor_Bool.fbt: addressed-refresh ECC path (RPT/SMP/Arm)");
            }, notFoundNote: "Sensor_Bool.fbt not found; addressed-refresh path skipped.");

        // Swivel work-arrival latch: relax=true (rig) fires ToWorkN->AtWorkN on atWorkN=TRUE alone;
        // strict=false (sim) also requires atWorkOther=FALSE.
        // The swivel-core patches all edit the deployed SevenStateCentreHomeActuator core .fbt.
        private static void EditSwivelCore(string eaeProjectDir, string failNote, DeployResult result,
            Action<XDocument, XElement, XNamespace, string> edit)
            => EditDeployedFbt(eaeProjectDir, "SevenStateCentreHomeActuator.fbt", failNote, result, edit);

        internal static void PatchSwivelRelaxWorkLatch(string eaeProjectDir, bool relax, DeployResult result)
            => EditSwivelCore(eaeProjectDir, "SevenStateCentreHomeActuator.fbt work-latch patch failed", result,
                (doc, root, ns, fbt) =>
            {
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
            });

        internal static void PatchSwivelInterlockEventCarriesStateVal(string eaeProjectDir, bool add, DeployResult result)
            => EditSwivelCore(eaeProjectDir, "SevenStateCentreHomeActuator.fbt ilck_event state_val patch failed", result,
                (doc, root, ns, fbt) =>
            {
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
            });



        // Gated SwivelBrakeHome: a timed reverse-coil brake at centre so the swivel homes directly from
        // AtWork1 (Disassembly) without coasting into the ejector. Directional — at AtHome the algorithm
        // reverses the coil only when homing from AtWork1; from AtWork2 (Assembly) it de-energises unchanged.
        // No-op when disabled; the ECC/CAT are force-refreshed so a flag flip reverts.
        internal static void PatchSwivelBrakeHome(string eaeProjectDir, bool enabled, int brakeMs, DeployResult result)
        {
            if (!enabled) return;
            if (brakeMs <= 0) brakeMs = 500;

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

        // SOLE writer of the swivel core's INIT arcs, derived from the twin.
        //
        // The model states where a cycle begins: the actuator's Initial_State. Startup therefore has one job --
        // establish that state before any recipe command runs -- and the CAT contract offers exactly two ways to
        // do it, so the arcs write themselves: where the work sensors read nothing the arm already is at its
        // centre reference and is CLASSIFIED there; where one of them reads, the arm is at that work position and
        // is DRIVEN to centre. Nothing is assumed about a position no sensor reports.
        //
        // A model whose swivel does not declare its centre stop as the initial state cannot be honoured by this
        // CAT, and generation fails rather than invent a startup policy: an unheld three-position pneumatic that
        // is neither classified nor driven simply drifts, and the first recipe command then starts from an
        // unknown place -- which is a collision, not a warning.
        internal static void PatchSwivelStartup(string eaeProjectDir,
            CodeGen.Translation.GenerationContext ctx, DeployResult result)
        {
            var startup = ResolveStartupState(ctx);
            // No centre-home swivel in this model: the type is deployed but never instantiated, so its
            // startup is not ours to rewrite. Leaving the shipped template alone is what keeps an
            // uninstantiated type from shipping with an ECC that can never leave INIT.
            if (startup.Count == 0) return;
            EditSwivelCore(eaeProjectDir, "SevenStateCentreHomeActuator.fbt startup patch failed", result,
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

        // The CAT's startup vocabulary: which ECC state each sensor reading resolves to. AtHomeInit classifies
        // (both coils FALSE, reports 0); ToHome drives the arm to its centre reference.
        private static readonly (string Sensor, string Destination)[] StartupFromWorkSensor =
        {
            ("atWork1", "ToHome"),
            ("atWork2", "ToHome"),
        };

        // INIT arcs for the one startup the model declares. Every centre-home swivel in the project must agree:
        // the arcs live on the shared TYPE, so two instances with different declared starts cannot both be honoured.
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

        // Wires AtHome to the coil-clearing 'atHome' algorithm + output_event so the Output
        // SYMLINKMULTIVARSRC writes both work coils FALSE (swaps which existing algorithm AtHome runs).
        internal static void PatchSwivelAtHomeCoilClear(string eaeProjectDir, bool clearCoils, DeployResult result)
            => EditSwivelCore(eaeProjectDir, "SevenStateCentreHomeActuator.fbt AtHome coil-clear patch failed", result,
                (doc, root, ns, fbt) =>
            {
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
            });

    }
}
