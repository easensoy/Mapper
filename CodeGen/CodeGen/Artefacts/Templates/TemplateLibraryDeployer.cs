using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;
using CodeGen.Devices.M262;
using static CodeGen.Services.TemplateArtifactDeployer;
using static CodeGen.Services.FbtXmlEditor;
using static CodeGen.Services.HmiTemplatePatcher;
using static CodeGen.Services.TelemetryTemplatePatcher;
using static CodeGen.Services.ProcessRuntimeTemplatePatcher;
using static CodeGen.Services.ActuatorCatTemplatePatcher;
using static CodeGen.Services.RingRelayPatcher;
using static CodeGen.Services.SwivelCatPatcher;
using static CodeGen.Services.InterlockCatPatcher;
using System.IO.Compression;
using CodeGen.Models;
using CodeGen.Mapping;
using CodeGen.Translation.Interlocks;
using CodeGen.Devices.Core;

namespace CodeGen.Services
{
    public static class TemplateLibraryDeployer
    {
        public static DeployResult DeployUniversalArchitecture(GenerationContext ctx)
        {
            var cfg = ctx.Config;
            var result = new DeployResult();
            var libPath = cfg.TemplateLibraryPath;
            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var eaeProjectDir = DeriveEaeProjectDir(cfg);
            if (string.IsNullOrWhiteSpace(eaeProjectDir))
                throw new InvalidOperationException("Cannot determine EAE project directory from syslay path.");

            // ExtractToEae is copy-if-absent, so delete first to force-re-extract anything later patches reshape.
            // An artefact left stale against a freshly reshaped CAT is an EAE "member/port does not exist" error.
            foreach (var ext in new[] { ".fbt", ".doc.xml", ".meta.xml" })
            foreach (var basic in TemplateManifest.ForceRefresh(ArtefactKind.Basic))
            {
                var stale = Path.Combine(eaeProjectDir, "IEC61499", basic + ext);
                try { if (File.Exists(stale)) File.Delete(stale); }
                catch (Exception ex)
                { MapperLogger.Info($"[Deploy][Refresh] could not remove stale {stale}: {ex.Message}"); }
            }

            foreach (var catRefresh in TemplateManifest.ForceRefresh(ArtefactKind.Cat))
            {
                var dir = Path.Combine(eaeProjectDir, "IEC61499", catRefresh);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception ex)
                { MapperLogger.Info($"[Deploy][Refresh] could not remove deployed CAT {dir}: {ex.Message}"); }
            }

            foreach (var name in TemplateManifest.Deployed(ArtefactKind.Basic))
                DeployArtifact(libPath, "Basic", name, eaeProjectDir, result, isBasic: true);

            SweepRetiredType(eaeProjectDir, "SimCentreHomeSensor_7SCH", result);

            foreach (var name in TemplateManifest.Deployed(ArtefactKind.Adapter))
                DeployArtifact(libPath, "Adapter", name, eaeProjectDir, result, isBasic: true);

            foreach (var name in TemplateManifest.Deployed(ArtefactKind.Composite))
                DeployArtifact(libPath, "Composite", name, eaeProjectDir, result, isBasic: false);

            foreach (var name in TemplateManifest.Deployed(ArtefactKind.HmiCat))
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            foreach (var name in TemplateManifest.Deployed(ArtefactKind.Cat))
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            // Held back so their .dfbproj entries land after every other artefact of their kind.
            foreach (var name in TemplateManifest.DeployedLast(ArtefactKind.Basic))
                DeployArtifact(libPath, "Basic", name, eaeProjectDir, result, isBasic: true);
            foreach (var name in TemplateManifest.DeployedLast(ArtefactKind.Cat))
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            if (cfg.DeployBx1IoBroker)
            {
                DeployArtifact(libPath, "Basic", "changeEventM262_2", eaeProjectDir, result, isBasic: true);
                // Force-re-extract so a corrected safe-start type reaches an already-generated tree.
                if (cfg.Bx1CoverSafeStart)
                {
                    var fsFbt = Path.Combine(eaeProjectDir, "IEC61499", "Bx1CoverFailsafe.fbt");
                    if (File.Exists(fsFbt)) { try { File.Delete(fsFbt); } catch { /* locked -> keep existing */ } }
                    DeployArtifact(libPath, "Basic", "Bx1CoverFailsafe", eaeProjectDir, result, isBasic: true);
                }
                DeployArtifact(libPath, "Composite", "PLC_RW_BX1", eaeProjectDir, result, isBasic: false);
                // Applies in BOTH bridge modes, so it sits here rather than in the embed path.
                if (CodeGen.Devices.BX1.Bx1IoBrokerInjector.EnsureInitWordDecodeInComposite(
                        Path.Combine(eaeProjectDir, "IEC61499", "PLC_RW_BX1.fbt")))
                    result.PatchesApplied.Add("PLC_RW_BX1: decode the EtherNet/IP input word at INIT so the cover "
                        + "change detector sees the real bits at power-on, not one scan later.");
                if (cfg.Bx1BridgeInsideComposite)
                    CodeGen.Devices.BX1.Bx1IoBrokerInjector.EmbedCoverBridgeInComposite(
                        Path.Combine(eaeProjectDir, "IEC61499", "PLC_RW_BX1.fbt"));
                // Forces cover_hr HOME on start only; EAE Clean/STOP still needs the TM3BC ToHome fallback 16#0002.
                if (cfg.Bx1CoverSafeStart &&
                    CodeGen.Devices.BX1.Bx1IoBrokerInjector.InjectCoverFailsafeIntoBrokerType(eaeProjectDir))
                    result.Warnings.Add("[Deploy][BX1] CoverPNP_Hr safe-start gate (Bx1CoverFailsafe) " +
                        "inserted into PLC_RW_BX1 — cover_hr forced HOME on every start.");
            }

            // Without the broker type EAE cannot instantiate RevPI_IO; pure M262 mode never deploys it.
            if (ctx.Profile.PartialRevPi)
            {
                DeployArtifact(libPath, "Composite", "PLC_RW_REVPI", eaeProjectDir, result, isBasic: false);
                // Internalise the Modbus symlink bridge so the RevPi sysres instantiates only RevPI_IO.
                    CodeGen.Devices.RevPi.RevPiIoBrokerInjector.EmbedBridgeInComposite(
                        Path.Combine(eaeProjectDir, "IEC61499", "PLC_RW_REVPI.fbt"));

            }

            DeployDataTypes(libPath, eaeProjectDir, result);
            PatchKnownArraySizeBugs(eaeProjectDir, result);
            // Every FB that declares state_table gets the size THIS plan needs, together.
            PatchStateTableCapacity(eaeProjectDir, ctx.StateTableCapacity, result);
            PatchProcessRuntimeCompatibility(eaeProjectDir, result);
            PatchSensorBoolCatDstQi(eaeProjectDir, result);
            PatchCatSymlinkQi(eaeProjectDir, "Five_State_Actuator_CAT", result);
            EnsureFiveStateInputPoll(eaeProjectDir, result);
            // QI=TRUE on the SYMLINKMULTIVARDST/SRC or the subscriber is dropped and the core is islanded from its IO.
            PatchCatSymlinkQi(eaeProjectDir, TemplateMap.SevenStateCentreHomeCat, result);
            EnsureSevenStateStateOut(eaeProjectDir, result);
            foreach (var cat in TemplateManifest.WithHmiFaceplate)
                FixCatHmiOpcuaFrame(eaeProjectDir, cat.Name, result);
            PatchActuatorModeInitialValue(eaeProjectDir, "FiveStateActuator.fbt", result);
            PatchActuatorModeInitialValue(eaeProjectDir, "SevenStateCentreHomeActuator.fbt", result);
            PatchSwivelStartup(eaeProjectDir, ctx, result);
            PatchSwivelAtHomeCoilClear(eaeProjectDir, clearCoils: true, result);
            // Runs LAST: the directional brake rewrites the whole atHome algorithm.
            PatchSwivelBrakeHome(eaeProjectDir, true,
                GenerationConfig.Current.BearingPnpHomeBrakeMs, result);
            PatchSwivelRelaxWorkLatch(eaeProjectDir, relax: true, result);
            PatchSwivelInterlockEventCarriesStateVal(eaeProjectDir, add: true, result);
            PatchRingClearCommandLatchOnInit(eaeProjectDir, result);
            PatchRingReportClearDest(eaeProjectDir, result);
            PatchRingCommandCnfOnlyOnDestination(eaeProjectDir, result);
            NormalizeFiveStateInterlockConstants(eaeProjectDir, result);
            PatchProcess1RecipeArraySize(eaeProjectDir, result);
            PatchProcessNameStringSize(eaeProjectDir, result);

            // PUBLISH binds to the injected MQTT_CONNECTION by matching ConnectionID value, with no wire.
            if (cfg.MqttPublishEnabled)
            {
                DeployMqttFormatter(cfg, eaeProjectDir, result);
                foreach (var cat in TemplateManifest.WithTelemetryTap)
                    PatchCatMqttPublish(eaeProjectDir, cat.Name,
                        cat.Telemetry!.StateEventSource, cat.Telemetry.StateDataSource,
                        cat.Telemetry.InitSource, cat.Telemetry.TopicNameSource, cfg, result);
            }

            // Runs unconditionally: with MQTT off both calls STRIP, leaving no publisher or stale declaration.
            ProcessRuntimeTemplatePatcher.PatchProcessTelemetryState(eaeProjectDir, cfg, result);
            PatchProcessMqttPublish(eaeProjectDir, cfg, result);

            // Both actuator CATs and the shared CommonInterlockEvaluator flip to/from the struct TOGETHER.
            // The guard aborts on a stale scalar/struct mix, which beats shipping a tree that fails EAE Build.
            bool interlockStruct = InterlockConfig.Current.UseStruct;
            bool targetStruct = InterlockConfig.Current.UseTargetStruct;
            ApplyInterlockNormalizers(cfg, eaeProjectDir, interlockStruct, targetStruct, result);
            AssertInterlockInterfaceConsistent(cfg, eaeProjectDir, interlockStruct, targetStruct, result);

            if (cfg.UseTelemetryCat)
            {
                // Sweep first: copy-if-absent would otherwise keep current and legacy Telemetry artefacts.
                SweepTelemetryCat(eaeProjectDir, result);
                DeployTelemetryConfigDatatype(cfg, eaeProjectDir, result);
                DeployTelemetryHealthDatatype(cfg, eaeProjectDir, result);
                DeployArtifact(libPath, "Basic", "TelemetryUnpack", eaeProjectDir, result, isBasic: true);
                DeployArtifact(libPath, "Basic", "TelemetryPack", eaeProjectDir, result, isBasic: true);
                DeployArtifact(libPath, "Composite", "Telemetry", eaeProjectDir, result, isBasic: false);
            }
            else
            {
                SweepTelemetryCat(eaeProjectDir, result);
            }
            NormalizeSwivelSimSensorSource(eaeProjectDir, result);
            StripCatHomeSensorPoll(eaeProjectDir, TemplateMap.SevenStateCentreHomeCat, result);
            // Broker-fed BX1 sensors have no I/O-scan event, so give them a scoped RD re-read the broker fires.
            EnsureSensorBoolReadEvent(eaeProjectDir, result);
            // A sensor reports only on a level change, so add an addressed refresh that reports even if unchanged.
            EnsureSensorBoolRefreshPath(eaeProjectDir, result);
            NormalizeFiveStateSimSensorSource(eaeProjectDir, result);
            NormalizeFiveStateFaultEnables(eaeProjectDir, result);
            bool recipeStruct = cfg.UseRecipeStruct;
            if (recipeStruct)
                DeployRecipeStepDatatype(cfg, eaeProjectDir, result);
            NormalizeProcess1RecipeArrays(eaeProjectDir, recipeStruct, result);
            // Required, not best-effort: EAE ignores the syslay's rdy_id parameter unless the type declares the pin.
            ProcessRuntimeTemplatePatcher.PromoteProcessPhaseReceiverSlot(eaeProjectDir);
            NormalizeProcessRuntimeRecipeArrays(eaeProjectDir, recipeStruct, result);
            NormalizeProcessEngineDebugWatch(eaeProjectDir, result);

            CodeGen.Hmi.HmiCatCfgEmitter.EmitAll(eaeProjectDir, cfg.TemplateLibraryPath);
            RegisterInDfbproj(eaeProjectDir, result);

            VerifyArraySizeConsistency(eaeProjectDir, result);

            // Trust-preservation guard: when an M262 sysdev exists, device-layer writes are skipped; application content still runs.
            bool m262DeviceExists = M262SysdevEmitter.M262SysdevAlreadyExists(cfg);

            string sysdevId = string.Empty;
            try
            {
                var sysdev = M262SysdevEmitter.Emit(ctx);
                result.SysdevPath = sysdev.SysdevPath;
                result.SystemFilePath = sysdev.SystemFilePath;
                result.MappingsAdded = sysdev.MappingsAdded;
                sysdevId = ReadSysdevId(sysdev.SysdevPath);
                if (sysdev.DevicePreserved)
                {
                    MapperLogger.Info(
                        "[Device] M262 sysdev exists, skipping device creation and " +
                        "config writes to preserve trust binding");
                    MapperLogger.Info("[Device] M262 sysdev preserved (trust binding intact)");
                }
                else
                {
                    MapperLogger.Info(
                        $"[Deploy] sysdev rewritten as M262_dPAC; {sysdev.MappingsAdded} APP→RES0 mapping(s) ensured");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"M262 sysdev emit failed: {ex.Message}");
            }

            if (m262DeviceExists)
            {
                MapperLogger.Info(
                    "[Device] M262 sysdev exists, skipping Topology Equipment JSON " +
                    "and network-profile writes to preserve trust binding");
            }
            else
            {
                try
                {
                    var topo = M262TopologyEmitter.Emit(cfg, sysdevId);
                    MapperLogger.Info(
                        $"[Deploy] Topology emitted: {topo.FilesWritten.Count} files, " +
                        $"{topo.TopologyProjEntriesAdded} topologyproj entries added");
                    foreach (var w in topo.Warnings)
                        result.Warnings.Add($"Topology: {w}");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"M262 topology emit failed: {ex.Message}");
                }
            }

            try
            {
                var hcf = HwConfigVerbatimCopier.CopyFor(cfg, CodeGen.Translation.PlcAssignment.M262, cfg.M262HcfTemplatePath);
                result.HcfPath = hcf.HcfPath;
                result.HcfParametersOverwritten.AddRange(hcf.ParametersOverwritten);
                foreach (var w in hcf.Warnings)
                    result.Warnings.Add($"HCF: {w}");
                MapperLogger.Info($"[Deploy] hcf copied from baseline; {hcf.ParametersOverwritten.Count} channel parameter(s) overwritten");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"M262 hcf copy failed: {ex.Message}");
            }

            result.Success = true;
            return result;
        }

        static void DeployDataTypes(string libPath, string eaeProjectDir, DeployResult result)
        {
            var srcDir = Path.Combine(libPath, "DataType");
            if (!Directory.Exists(srcDir))
            {
                result.Warnings.Add("Library DataType folder missing — Component_State*.dt won't be deployed.");
                return;
            }
            var destDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
            Directory.CreateDirectory(destDir);

            foreach (var name in TemplateManifest.Deployed(ArtefactKind.DataType))
            {
                var src = Path.Combine(srcDir, name + ".dt");
                if (!File.Exists(src))
                {
                    result.Warnings.Add($"DataType source missing: {name}.dt");
                    continue;
                }
                var dst = Path.Combine(destDir, name + ".dt");
                if (!File.Exists(dst) ||
                    new FileInfo(src).Length != new FileInfo(dst).Length)
                {
                    File.Copy(src, dst, overwrite: true);
                    result.FilesExtracted++;
                }
                result.DataTypesDeployed.Add(name);
                MapperLogger.Info($"[Deploy] DataType: {name}");
            }
        }

        static void DeployMqttFormatter(MapperConfig cfg, string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dst = Path.Combine(eaeProjectDir, "IEC61499", "MqttStateFormatter.fbt");
                File.WriteAllText(dst, TemplateDocument.Load(cfg,
                    @"Basic\MqttStateFormatter\IEC61499\MqttStateFormatter.fbt"));
                result.PatchesApplied.Add("MqttStateFormatter.fbt deployed (INT→STRING[255] payload)");
                MapperLogger.Info("[Deploy][MQTT] MqttStateFormatter.fbt written to IEC61499/");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"MqttStateFormatter deploy failed: {ex.Message}");
            }
        }

        // MQTT_PUBLISH does not resolve $${PATH} at runtime, so topicNameSource must be a concrete per-instance name.
        static void PatchCatMqttPublish(string eaeProjectDir, string catName,
            string stateEventSource, string stateDataSource, string initSource,
            string topicNameSource,
            MapperConfig cfg, DeployResult result)
        {
            var fbt = FindDeployedFbt(eaeProjectDir, catName + ".fbt");
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add($"{catName}.fbt not found; MQTT publish patch skipped.");
                return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var net = root.Element(ns + "FBNetwork");
                if (net == null) { result.Warnings.Add($"{catName}.fbt: no FBNetwork; MQTT patch skipped."); return; }

                // The CAT folder is copy-if-absent, so clear the previous emission before re-emitting.
                var staleFbs = net.Elements(ns + "FB")
                    .Where(f => (string?)f.Attribute("Name") is "MqttPub" or "MqttFmt")
                    .ToList();
                int removedFbs = staleFbs.Count;
                foreach (var f in staleFbs) f.Remove();

                int removedWires = 0;
                foreach (var section in new[] { "EventConnections", "DataConnections" })
                {
                    var sec = net.Element(ns + section);
                    if (sec == null) continue;
                    var staleConns = sec.Elements(ns + "Connection")
                        .Where(c =>
                        {
                            var s = (string?)c.Attribute("Source") ?? string.Empty;
                            var d = (string?)c.Attribute("Destination") ?? string.Empty;
                            return s.StartsWith("MqttFmt.", StringComparison.Ordinal)
                                || s.StartsWith("MqttPub.", StringComparison.Ordinal)
                                || d.StartsWith("MqttFmt.", StringComparison.Ordinal)
                                || d.StartsWith("MqttPub.", StringComparison.Ordinal);
                        })
                        .ToList();
                    removedWires += staleConns.Count;
                    foreach (var c in staleConns) c.Remove();
                }
                if (removedFbs > 0 || removedWires > 0)
                    result.PatchesApplied.Add(
                        $"{catName}: removed stale MQTT patch ({removedFbs} FB(s), {removedWires} wire(s)) before re-emit");

                // Allocate from max(existing ID)+1, not the IDCounter alone: later-added FBs sit past the counter.
                var idAttr = root.Elements(ns + "Attribute")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
                int idc = 0;
                if (idAttr != null && int.TryParse((string?)idAttr.Attribute("Value"), out var parsed)) idc = parsed;
                int maxFbId = net.Elements(ns + "FB")
                    .Select(f => int.TryParse((string?)f.Attribute("ID"), out var v) ? v : 0)
                    .DefaultIfEmpty(0).Max();
                int baseId = Math.Max(maxFbId + 1, idc);   // never collide, never go backwards
                int fmtId = baseId, pubId = baseId + 1;
                if (idAttr != null) idAttr.SetAttributeValue("Value", (baseId + 2).ToString());

                string Q(string s) => "'" + s + "'";   // ST string literal

                var fmtFb = new System.Xml.Linq.XElement(ns + "FB",
                    new System.Xml.Linq.XAttribute("ID", fmtId),
                    new System.Xml.Linq.XAttribute("Name", "MqttFmt"),
                    new System.Xml.Linq.XAttribute("Type", "MqttStateFormatter"),
                    new System.Xml.Linq.XAttribute("x", "8000"),
                    new System.Xml.Linq.XAttribute("y", "2580"),
                    new System.Xml.Linq.XAttribute("Namespace", "Main"));

                // The hash names a generic variant, and its numbered channel ports exist only once CNTX:=1 is set.
                const string MqttPublishVariant = "MQTT_PUBLISH_115480E69E664F878";
                var pubFb = new System.Xml.Linq.XElement(ns + "FB",
                    new System.Xml.Linq.XAttribute("ID", pubId),
                    new System.Xml.Linq.XAttribute("Name", "MqttPub"),
                    new System.Xml.Linq.XAttribute("Type", MqttPublishVariant),
                    new System.Xml.Linq.XAttribute("x", "8600"),
                    new System.Xml.Linq.XAttribute("y", "2580"),
                    new System.Xml.Linq.XAttribute("Namespace", "Main"));
                pubFb.Add(new System.Xml.Linq.XElement(ns + "Attribute",
                    new System.Xml.Linq.XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                    new System.Xml.Linq.XAttribute("Value", "Runtime.NetConnectivity#CNTX:=1")));
                void P(System.Xml.Linq.XElement fb, string n, string v) =>
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", n),
                        new System.Xml.Linq.XAttribute("Value", v)));
                P(pubFb, "QI", "TRUE");
                // ConnectionID is the shared binding key, not the unique ClientIdentifier.
                P(pubFb, "ConnectionID", Q(cfg.MqttConnectionName));

                P(pubFb, "RootPath", Q(cfg.MqttTopicRoot));
                // Topic1 wired below, not a parameter.
                P(pubFb, "QoS1", cfg.MqttQoS.ToString());
                P(pubFb, "Retain1", cfg.MqttRetain ? "TRUE" : "FALSE");

                var lastFb = net.Elements(ns + "FB").LastOrDefault();
                if (lastFb != null) { lastFb.AddAfterSelf(pubFb); lastFb.AddAfterSelf(fmtFb); }
                else { net.Add(fmtFb); net.Add(pubFb); }

                // A <Frame> with <Parameter> children is invalid inside a CAT FBNetwork.
                net.Elements(ns + "Frame")
                   .Where(fr => (string?)fr.Attribute("Name") == "FRAME_MQTT").Remove();

                var ec = Connections(net, ns, "EventConnections");
                var dc = Connections(net, ns, "DataConnections");


                ec.Append(stateEventSource, "MqttFmt.REQ");
                ec.Append("MqttFmt.CNF", "MqttPub.PUBLISH1");
                ec.Append(initSource, "MqttFmt.INIT");
                ec.Append(initSource, "MqttPub.INIT");
                dc.Append(stateDataSource, "MqttFmt.state");
                dc.Append("MqttFmt.payload", "MqttPub.Payload1");
                dc.Append(topicNameSource, "MqttPub.Topic1");

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    $"{catName}: MQTT publish injected (fan {stateEventSource} → MqttFmt → MqttPub.PUBLISH1, " +
                    $"ConnectionID={cfg.MqttConnectionName}, Topic=$${{PATH}}state)");
                MapperLogger.Info($"[Deploy][MQTT] {catName}.fbt: MQTT_PUBLISH wired off {stateEventSource}");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catName} MQTT publish patch failed: {ex.Message}");
            }
        }

        // Passive by construction: nothing waits on the publish, so a broker outage cannot stall the recipe.
        // The payload state is the VueOne State_Number from ProcessStateByRow, NOT CurrentStep (a row index).
        static void PatchProcessMqttPublish(string eaeProjectDir, MapperConfig cfg, DeployResult result)
        {
            var fbt = FindDeployedFbt(eaeProjectDir, "Process1_Generic.fbt");
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Process1_Generic.fbt not found; process MQTT patch skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "FBNetwork");
                if (net == null) { result.Warnings.Add("Process1_Generic.fbt: no FBNetwork; process MQTT patch skipped."); return; }

                // Strip any previous emission: the folder is copy-if-absent and telemetry-off must leave no residue.
                net.Elements(ns + "FB")
                   .Where(f => (string?)f.Attribute("Name") is "MqttFmt" or "MqttPub")
                   .ToList().ForEach(f => f.Remove());
                foreach (var section in new[] { "EventConnections", "DataConnections" })
                {
                    var sec = net.Element(ns + section);
                    sec?.Elements(ns + "Connection")
                        .Where(c =>
                        {
                            var s = (string?)c.Attribute("Source") ?? string.Empty;
                            var d = (string?)c.Attribute("Destination") ?? string.Empty;
                            return s.StartsWith("MqttFmt.", StringComparison.Ordinal)
                                || s.StartsWith("MqttPub.", StringComparison.Ordinal)
                                || d.StartsWith("MqttFmt.", StringComparison.Ordinal)
                                || d.StartsWith("MqttPub.", StringComparison.Ordinal)
                                || d.EndsWith(".ProcessStateByRow", StringComparison.Ordinal);
                        })
                        .ToList().ForEach(c => c.Remove());
                }

                if (!cfg.MqttPublishEnabled)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add("Process1_Generic: process MQTT publisher stripped (MQTT publishing off)");
                    return;
                }

                var engine = net.Elements(ns + "FB")
                    .FirstOrDefault(f => (string?)f.Attribute("Type") == "ProcessRuntime_Generic_v1");
                if (engine == null)
                {
                    result.Warnings.Add("Process1_Generic.fbt: ProcessRuntime_Generic_v1 instance not found; process MQTT patch skipped.");
                    return;
                }
                string eng = (string?)engine.Attribute("Name") ?? "ProcessEngine";

                var idAttr = root.Elements(ns + "Attribute")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "Configuration.FB.IDCounter");
                int idc = 0;
                if (idAttr != null && int.TryParse((string?)idAttr.Attribute("Value"), out var parsed)) idc = parsed;
                int maxFbId = net.Elements(ns + "FB")
                    .Select(f => int.TryParse((string?)f.Attribute("ID"), out var v) ? v : 0)
                    .DefaultIfEmpty(0).Max();
                int baseId = Math.Max(maxFbId + 1, idc);
                int fmtId = baseId, pubId = baseId + 1;
                if (idAttr != null) idAttr.SetAttributeValue("Value", (baseId + 2).ToString());

                string Q(string s) => "'" + s + "'";
                var fmtFb = new System.Xml.Linq.XElement(ns + "FB",
                    new System.Xml.Linq.XAttribute("ID", fmtId),
                    new System.Xml.Linq.XAttribute("Name", "MqttFmt"),
                    new System.Xml.Linq.XAttribute("Type", "MqttStateFormatter"),
                    new System.Xml.Linq.XAttribute("x", "8000"),
                    new System.Xml.Linq.XAttribute("y", "3200"),
                    new System.Xml.Linq.XAttribute("Namespace", "Main"));

                const string MqttPublishVariant = "MQTT_PUBLISH_115480E69E664F878";
                var pubFb = new System.Xml.Linq.XElement(ns + "FB",
                    new System.Xml.Linq.XAttribute("ID", pubId),
                    new System.Xml.Linq.XAttribute("Name", "MqttPub"),
                    new System.Xml.Linq.XAttribute("Type", MqttPublishVariant),
                    new System.Xml.Linq.XAttribute("x", "8600"),
                    new System.Xml.Linq.XAttribute("y", "3200"),
                    new System.Xml.Linq.XAttribute("Namespace", "Main"));
                pubFb.Add(new System.Xml.Linq.XElement(ns + "Attribute",
                    new System.Xml.Linq.XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                    new System.Xml.Linq.XAttribute("Value", "Runtime.NetConnectivity#CNTX:=1")));
                void P(System.Xml.Linq.XElement fb, string n, string v) =>
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", n),
                        new System.Xml.Linq.XAttribute("Value", v)));
                P(pubFb, "QI", "TRUE");
                // Same binding key as the component publishers, so this binds to its own resource's connection.
                P(pubFb, "ConnectionID", Q(cfg.MqttConnectionName));
                P(pubFb, "RootPath", Q(cfg.MqttTopicRoot + "/process"));
                P(pubFb, "QoS1", cfg.MqttQoS.ToString());
                P(pubFb, "Retain1", cfg.MqttRetain ? "TRUE" : "FALSE");

                var lastFb = net.Elements(ns + "FB").LastOrDefault();
                if (lastFb != null) { lastFb.AddAfterSelf(pubFb); lastFb.AddAfterSelf(fmtFb); }
                else { net.Add(fmtFb); net.Add(pubFb); }

                var ec = Connections(net, ns, "EventConnections");
                var dc = Connections(net, ns, "DataConnections");

                dc.Append("ProcessStateByRow", eng + ".ProcessStateByRow");
                ec.Append(eng + "." + ProcessRuntimeTemplatePatcher.PhaseEventName, "MqttFmt.REQ");
                ec.Append("MqttFmt.CNF", "MqttPub.PUBLISH1");
                ec.Append("INIT", "MqttFmt.INIT");
                ec.Append("INIT", "MqttPub.INIT");
                dc.Append(eng + ".CurrentProcessState", "MqttFmt.state");
                dc.Append("MqttFmt.payload", "MqttPub.Payload1");
                dc.Append("process_name", "MqttPub.Topic1");

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    $"Process1_Generic: process MQTT publish injected (fan {eng}.SCNF -> MqttFmt -> MqttPub.PUBLISH1, " +
                    $"ConnectionID={cfg.MqttConnectionName}, Topic={cfg.MqttTopicRoot}/process/<process_name>)");
                MapperLogger.Info($"[Deploy][MQTT] Process1_Generic.fbt: process-state MQTT_PUBLISH wired off {eng}.SCNF");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Process1_Generic process MQTT patch failed: {ex.Message}");
            }
        }

        static void VerifyArraySizeConsistency(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var sizes = new Dictionary<(string, string), string>(
                    EqualityComparer<(string, string)>.Default);

                foreach (var (fbt, doc) in EachDeployedFbt(eaeProjectDir))
                {
                    var fbType = Path.GetFileNameWithoutExtension(fbt);
                    foreach (var vd in doc.Descendants().Where(e => e.Name.LocalName == "VarDeclaration"))
                    {
                        var name = (string?)vd.Attribute("Name");
                        var arr = (string?)vd.Attribute("ArraySize");
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(arr)) continue;
                        sizes[(fbType, name)] = arr;
                    }
                }

                foreach (var (fbt, doc) in EachDeployedFbt(eaeProjectDir))
                {
                    var instances = doc.Descendants()
                        .Where(e => e.Name.LocalName == "FB")
                        .ToDictionary(
                            e => (string?)e.Attribute("Name") ?? "",
                            e => (string?)e.Attribute("Type") ?? "",
                            StringComparer.Ordinal);

                    foreach (var conn in doc.Descendants().Where(e => e.Name.LocalName == "Connection"))
                    {
                        var src = ((string?)conn.Attribute("Source") ?? "").Split('.', 2);
                        var dst = ((string?)conn.Attribute("Destination") ?? "").Split('.', 2);
                        if (src.Length != 2 || dst.Length != 2) continue;
                        if (!instances.TryGetValue(src[0], out var srcType)) continue;
                        if (!instances.TryGetValue(dst[0], out var dstType)) continue;

                        sizes.TryGetValue((srcType, src[1]), out var srcSize);
                        sizes.TryGetValue((dstType, dst[1]), out var dstSize);
                        if (srcSize == null || dstSize == null) continue;
                        if (!string.Equals(srcSize, dstSize, StringComparison.Ordinal))
                        {
                            var msg = $"ArraySize mismatch in {Path.GetFileName(fbt)}: " +
                                $"{src[0]}.{src[1]} (size {srcSize}) -> {dst[0]}.{dst[1]} (size {dstSize})";
                            result.Warnings.Add(msg);
                            MapperLogger.Warn("[Verify] " + msg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ArraySize verification crashed: {ex.Message}");
            }
        }

    }

    public class DeployResult
    {
        public bool Success { get; set; }
        public List<string> BasicFBsDeployed { get; set; } = new();
        public List<string> CATsDeployed { get; set; } = new();
        public List<string> AdaptersDeployed { get; set; } = new();
        public List<string> CompositesDeployed { get; set; } = new();
        public List<string> DataTypesDeployed { get; set; } = new();
        public List<string> PatchesApplied { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int FilesExtracted { get; set; }
        public int FilesSkipped { get; set; }

        public string? SysdevPath { get; set; }
        public string? SystemFilePath { get; set; }
        public int MappingsAdded { get; set; }

        public string? HcfPath { get; set; }
        public List<string> HcfParametersOverwritten { get; set; } = new();
    }
}
