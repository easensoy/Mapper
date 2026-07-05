using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Translation.Interlocks;
using CodeGen.Devices.M262;
using CodeGen.Devices.Core;
using static CodeGen.Services.TemplateArtifactDeployer;
using static CodeGen.Services.FbtXmlEditor;
using static CodeGen.Services.HmiTemplatePatcher;
using static CodeGen.Services.TelemetryTemplatePatcher;
using static CodeGen.Services.ProcessRuntimeTemplatePatcher;

namespace CodeGen.Services
{
    public static class TemplateLibraryDeployer
    {
        static readonly Dictionary<string, string[]> CatDependencies = new()
        {
            { "Five_State_Actuator_CAT", new[] { "FiveStateActuator" } },
            { "Sensor_Bool_CAT",         new[] { "Sensor_Bool" } },
            { "Actuator_Fault_CAT",      new[] { "FaultLatch" } },
            { "Robot_Task_CAT",          new[] { "Robot_Task_Core" } },
            // Seven_State_Actuator_CAT for Bearing_PnP routing (the
            // PARALLEL+ALTERNATIVE-branched 13-state actuator). SevenStateActuator2
            // is its internal "InterlockManager" sub-FB analogue (same role as
            // CommonInterlockEvaluator for Five_State). Bearing_PnP is routed to the
            // verbatim CAT (no runtime parameter graft).
            { "Seven_State_Actuator_CAT", new[] { "SevenStateActuator", "SevenStateActuator2" } },
            // Centre-home swivel Basic leaf FBs: SevenStateCentreHomeActuator (core ECC),
            // No_Sensor_Handler_7SCH (synthesises atHome on the work->home timer), FaultLatch_7SCH
            // (leaf inside faultDetection_7SCH). The composite faultDetection_7SCH ships via
            // UniversalComposites; CommonInterlockEvaluator + updateComponentState via UniversalBasics.
            { "Seven_State_Actuator_Centre_Home_CAT",
              new[] { "SevenStateCentreHomeActuator", "No_Sensor_Handler_7SCH", "FaultLatch_7SCH",
                      "actuatorStateEvents_7SCH" } },
            { "Station_CAT",             new[] { "Station_Core", "Station_Fault", "Station_Status" } },
            { "Process1_Generic",        new[] { "ProcessRuntime_Generic_v1", "ProcessStateBusHandler" } },
        };

        static readonly Dictionary<string, string> ComponentTypeToCat = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Actuator_5",  "Five_State_Actuator_CAT" },
            { "Actuator_7",  "Seven_State_Actuator_CAT" },
            { "Sensor_2",    "Sensor_Bool_CAT" },
            { "Process_Any", "Process1_Generic" },
        };

        static readonly string[] UniversalCats = new[]
        {
            "Five_State_Actuator_CAT", "Sensor_Bool_CAT", "Process1_Generic",
            // Seven_State_Actuator_CAT restored for Bearing_PnP (13-state
            // PARALLEL+ALTERNATIVE branched). Deployed verbatim from the
            // Template Library — no runtime data-driven patching.
            "Seven_State_Actuator_CAT",
            // Centre-home swivel CAT — Bearing_PnP instantiates this (TemplateMap). Deployed always so
            // EAE can resolve the type on import.
            "Seven_State_Actuator_Centre_Home_CAT",
        };

        // No I/O-bridge FB is deployed. PLC_RW_M262 (the old "M262IO" broker)
        // is retired: Sensor_Bool_CAT / Five_State_Actuator_CAT do direct
        // symlink I/O to the TM3 channels via their own $${PATH} macros, so
        // nothing ever instantiates PLC_RW_M262. Shipping its orphan .fbt only
        // added an unreferenced type + a dfbproj entry, so Mapper no longer
        // deploys it.

        static readonly string[] UniversalComposites = new[]
        {
            "Area", "Station", "CaSAdptrTerminator", "faultDetection",
            // Composite fault FB for the centre-home swivel (wraps FaultLatch_7SCH).
            "faultDetection_7SCH",
        };

        static readonly string[] UniversalAdapters = new[]
        {
            "CaSAdptr", "AreaHMIAdptr", "StationHMIAdptr", "stateRptCmdAdptr"
        };

        static readonly string[] UniversalBasics = new[]
        {
            "FiveStateActuator", "Sensor_Bool",
            "Station_Core", "Station_Fault", "Station_Status",
            "ProcessRuntime_Generic_v1", "ProcessStateBusHandler",
            "FaultLatch", "actuatorStateEvents",
            "updateComponentState", "updateComponentState_Sensor",
            "No_Sensor_Handler",
            // Embedded by Five_State_Actuator_CAT as its "InterlockManager" sub-FB
            // (Type=Main:CommonInterlockEvaluator). Must be deployed or EAE cannot resolve the type.
            "CommonInterlockEvaluator",
            // Event-change handlers referenced by PLC_RW_M262's internal FB2/FB3 instances.
            "changeEventProcess1", "changeEventProcess2",
            // SevenStateActuator + SevenStateActuator2 — Basic FBs embedded by
            // Seven_State_Actuator_CAT. Both must be deployed when the CAT is in
            // scope or EAE fails with "type or namespace SevenStateActuator2
            // does not exist".
            "SevenStateActuator", "SevenStateActuator2",
            // Centre-home swivel leaf Basics: SevenStateCentreHomeActuator (core ECC),
            // No_Sensor_Handler_7SCH (synthesises atHome on the work->home timer), FaultLatch_7SCH +
            // actuatorStateEvents_7SCH (leaves inside faultDetection_7SCH). actuatorStateEvents_7SCH is
            // the two-work variant (state 1 -> toWork1_Event, state 3 -> toWork2_Event).
            "SevenStateCentreHomeActuator", "No_Sensor_Handler_7SCH", "FaultLatch_7SCH",
            "actuatorStateEvents_7SCH",
            // SimCentreHomeSensor_7SCH is not deployed (simulator-only); any stale copy is swept in
            // DeployUniversalArchitecture.
        };

        static readonly string[] UniversalHmiCats = new[]
        {
            "Area_CAT", "Station_CAT"
        };

        static readonly string[] UniversalDataTypes = new[]
        {
            "Component_State",
            "Component_State_Msg"
        };

        public static DeployResult DeployUniversalArchitecture(MapperConfig cfg)
        {
            var result = new DeployResult();
            var libPath = cfg.TemplateLibraryPath;
            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var eaeProjectDir = DeriveEaeProjectDir(cfg);
            if (string.IsNullOrWhiteSpace(eaeProjectDir))
                throw new InvalidOperationException("Cannot determine EAE project directory from syslay path.");

            // DeployArtifact below is COPY-IF-ABSENT, so force-refresh the artefacts that are reshaped
            // every deploy by later patches: delete them first so the pristine zip re-extracts and the
            // patches re-apply from a known base. This covers No_Sensor_Handler_7SCH (sensor-driven
            // home) + SevenStateCentreHomeActuator (its atHome/AtHomeInit ECC is reshaped by the
            // coil-clear/both-coils/brake patches, so a flag change would not otherwise revert) +
            // ProcessRuntime_Generic_v1 (the WAIT engine, so no stale check_wait body persists;
            // NormalizeProcessRuntimeRecipeArrays then gives the level-triggered form). Idempotent.
            foreach (var ext in new[] { ".fbt", ".doc.xml", ".meta.xml" })
            foreach (var basic in new[] { "No_Sensor_Handler_7SCH", "SevenStateCentreHomeActuator", "ProcessRuntime_Generic_v1" })
            {
                var stale = Path.Combine(eaeProjectDir, "IEC61499", basic + ext);
                try { if (File.Exists(stale)) File.Delete(stale); }
                catch (Exception ex)
                { MapperLogger.Info($"[Deploy][Refresh] could not remove stale {stale}: {ex.Message}"); }
            }

            // CAT/TYPE REFRESH: DeployArtifact is COPY-IF-ABSENT, so a deployed CAT/type folder persists
            // across Generates even when the committed zip is clean; a stale mutation from a prior deploy
            // (or a prior version of a deploy-time patch) would never be repaired by a plain re-Generate.
            // Delete the deployed folders first so the pristine zip is re-extracted every deploy and the
            // deployer's own normalizers/patches re-apply from a known base. Includes Process1_Generic
            // (whose recipe interface is reshaped to the Recipe struct each deploy). Pipeline-respecting
            // (no direct Demonstrator edit); PatchCatMqttPublish re-applies embedded MQTT after; EAE
            // recompiles on Build.
            foreach (var catRefresh in new[] { "Sensor_Bool_CAT", "Five_State_Actuator_CAT", "Seven_State_Actuator_Centre_Home_CAT", "Process1_Generic" })
            {
                var dir = Path.Combine(eaeProjectDir, "IEC61499", catRefresh);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception ex)
                { MapperLogger.Info($"[Deploy][Refresh] could not remove deployed CAT {dir}: {ex.Message}"); }
            }

            foreach (var name in UniversalBasics)
                DeployArtifact(libPath, "Basic", name, eaeProjectDir, result, isBasic: true);

            // Retire the simulator-only SimCentreHomeSensor_7SCH: delete any stale copy a prior sim
            // deploy left + strip its dfbproj entries so EAE shows no Missing Project Files.
            SweepRetiredType(eaeProjectDir, "SimCentreHomeSensor_7SCH", result);

            foreach (var name in UniversalAdapters)
                DeployArtifact(libPath, "Adapter", name, eaeProjectDir, result, isBasic: true);

            foreach (var name in UniversalComposites)
                DeployArtifact(libPath, "Composite", name, eaeProjectDir, result, isBasic: false);

            foreach (var name in UniversalHmiCats)
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            foreach (var name in UniversalCats)
                DeployArtifact(libPath, "CAT", name, eaeProjectDir, result, isBasic: false, isCat: true);

            // Robot_Task_CAT (UR3e task-handshake CAT: StartTask DO04 / Task_Complete DI10) + its
            // Robot_Task_Core Basic FB are ALWAYS deployed: the UR3e is a real Control.xml component, so
            // its type must resolve on import even when the robot INSTANCE / ring membership are gated
            // off separately (a defined-but-unused CAT type is harmless). Deploy the core first so the
            // composite resolves. DemonstratorWiper deletes the folder on Clean; this re-creates it.
            DeployArtifact(libPath, "Basic", "Robot_Task_Core", eaeProjectDir, result, isBasic: true);
            DeployArtifact(libPath, "CAT", "Robot_Task_CAT", eaeProjectDir, result, isBasic: false, isCat: true);

            // BX1 EtherNet/IP cover-I/O broker (gated). PLC_RW_BX1 is the composite
            // that unpacks the EtherNet/IP input word into cover sensor-bits and packs
            // cover coil-bits into the output word; changeEventM262_2 is its leaf
            // change-detector. The SYMLINKMULTIVARDST/SRC types it embeds are
            // compiler-generated by EAE; WordToBits/BitsToWord resolve from the
            // already-referenced SE.ModbusGateway library. Deploy leaf first.
            if (cfg.DeployBx1IoBroker)
            {
                DeployArtifact(libPath, "Basic", "changeEventM262_2", eaeProjectDir, result, isBasic: true);
                // SAFETY: the CoverPNP_Hr safe-start gate type. Deployed leaf-first so the broker
                // patch below can reference it. FORCE-REFRESH: DeployArtifact/CopyDirToEae is
                // copy-if-absent and the broker injector is idempotent, so a CORRECTED safety gate
                // would otherwise never reach an already-generated tree — delete the deployed type
                // first so the current source (e.g. the AtHome-AND-NOT-ToWork release) always lands.
                if (cfg.Bx1CoverSafeStart)
                {
                    var fsFbt = Path.Combine(eaeProjectDir, "IEC61499", "Bx1CoverFailsafe.fbt");
                    if (File.Exists(fsFbt)) { try { File.Delete(fsFbt); } catch { /* locked -> keep existing */ } }
                    DeployArtifact(libPath, "Basic", "Bx1CoverFailsafe", eaeProjectDir, result, isBasic: true);
                }
                DeployArtifact(libPath, "Composite", "PLC_RW_BX1", eaeProjectDir, result, isBasic: false);
                // INTERNALIZED cover bridge (cfg.Bx1BridgeInsideComposite, default): transform
                // the just-deployed PLC_RW_BX1.fbt so the per-cover sensor/coil symlink bridge
                // + scan cycle live INSIDE the composite (data-driven from
                // Bx1IoBrokerInjector.CoverIoBits). The generated BX1 resource then carries ONLY
                // BX1_IO — the injector skips the external bridge. Idempotent; BX1-only; the EIP
                // word symlinks + the .hcf binding to BX1_IO are left untouched.
                if (cfg.Bx1BridgeInsideComposite)
                    CodeGen.Devices.BX1.Bx1IoBrokerInjector.EmbedCoverBridgeInComposite(
                        Path.Combine(eaeProjectDir, "IEC61499", "PLC_RW_BX1.fbt"));
                // SAFETY: insert the CoverPNP_Hr safe-start gate into the broker output path so
                // cover_hr can never energise Work on deploy/login/restart (whenever the logic runs)
                // and is driven home if left at Work. Does NOT cover EAE Clean/STOP (logic stops, the
                // output word freezes) — that needs the TM3BC coupler ToHome fallback (16#0002). Runs
                // AFTER any internalize so it keys on the live bit wiring.
                if (cfg.Bx1CoverSafeStart &&
                    CodeGen.Devices.BX1.Bx1IoBrokerInjector.InjectCoverFailsafeIntoBrokerType(eaeProjectDir))
                    result.Warnings.Add("[Deploy][BX1] CoverPNP_Hr safe-start gate (Bx1CoverFailsafe) " +
                        "inserted into PLC_RW_BX1 — cover_hr forced HOME on every start.");
            }

            DeployDataTypes(libPath, eaeProjectDir, result);
            PatchKnownArraySizeBugs(eaeProjectDir, result);
            PatchProcessRuntimeCompatibility(eaeProjectDir, result);
            PatchSensorBoolCatDstQi(eaeProjectDir, result);
            PatchCatSymlinkQi(eaeProjectDir, "Five_State_Actuator_CAT", result);
            // The centre-home swivel CAT's symlink FBs (Inputs SYMLINKMULTIVARDST + Output
            // SYMLINKMULTIVARSRC) ship with QI unset -> FALSE -> disabled, which islands the core from
            // its IO (coil commands never reach the DO channels, sensors never reach the core). Set QI.
            PatchCatSymlinkQi(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT", result);
            // A stale deploy can leave the boundary 'current_state_to_process' (still referenced by the
            // _HMI) orphaned after the old 'state_out' event was removed — EAE rejects the unWITHed var.
            // Re-pair state_out WITH current_state_to_process (no control-logic change; no-op on a clean CAT).
            EnsureSevenStateStateOut(eaeProjectDir, result);
            // VISUAL-only: keep each CAT's HMI/OPCUA section frame from spilling into the section
            // below (pull IThis inside, fix the frame). Self-skips CATs without an HMI/OPCUA frame.
            foreach (var hmiCat in new[] { "Five_State_Actuator_CAT", "Seven_State_Actuator_Centre_Home_CAT",
                                           "Sensor_Bool_CAT", "Robot_Task_CAT" })
                FixCatHmiOpcuaFrame(eaeProjectDir, hmiCat, result);
            PatchActuatorModeInitialValue(eaeProjectDir, "FiveStateActuator.fbt", result);
            // The centre-home swivel core needs the same auto-mode default; without it its ECC powers up
            // in AtHomeInit and ignores every recipe command (stalling the Assembly recipe on step 1).
            PatchActuatorModeInitialValue(eaeProjectDir, "SevenStateCentreHomeActuator.fbt", result);
            // AtHomeInit has no sensor-recovery arc: if the swivel's DI is not live the instant INIT runs
            // the core falls to AtHomeInit and freezes there even after a work sensor comes true. Add an
            // arc that corrects the logical state to match a physical work position, so the stock
            // AtWork->ToHome path can then carry the Home command. Real DI symlinks only; always on the rig.
            PatchSwivelAtHomeInitRecovery(eaeProjectDir, addArc: true, result);
            // Home must clear both work coils (the coil used to swing through centre would otherwise stay
            // energised at "home") and publish output_event at AtHome.
            PatchSwivelAtHomeCoilClear(eaeProjectDir, clearCoils: true, result);
            // Gated SwivelHomeHoldBothCoils (default OFF): ON holds both coils at home so a cylinder with
            // a mechanical mid-stop is driven into + held at centre instead of coasting past DI02; OFF
            // de-energises at centre. Bidirectional.
            PatchSwivelAtHomeBothCoils(eaeProjectDir, MapperConfig.SwivelHomeHoldBothCoils, result);
            // Gated SwivelBrakeHome (default ON, runs LAST so it owns the final atHome shape): the atHome
            // algorithm becomes a DIRECTIONAL brake (reverse the driving coil only when homing from
            // AtWork1 -> away from the ejector; de-energise otherwise so Assembly is untouched) with a
            // brakeTimer E_DELAY (DT from config.yaml bearingPnpHomeBrakeMs), so Disassembly can home
            // straight from AtWork1 without coasting into the ejector. OFF = the de-energise home stands.
            PatchSwivelBrakeHome(eaeProjectDir, MapperConfig.SwivelBrakeHome,
                GenerationConfig.Current.BearingPnpHomeBrakeMs, result);
            // Relax the work-arrival latches to fire on atWorkN=TRUE alone (a brief DI00/DI01 transit
            // overlap otherwise blocked the AtWork latch that gates gripper-release). atHome is
            // INTENTIONALLY driven by the real DI02, not the ReturnToHomeHandler timer (position-accurate).
            PatchSwivelRelaxWorkLatch(eaeProjectDir, relax: true, result);
            // The Place command fans an interlock check into ActuatorCore.ilck_event before the pst_event
            // command edge is visible; sample state_val on ilck_event too so AtWork1 -> ToWork2 sees the
            // same command value (3). Seven-only.
            PatchSwivelInterlockEventCarriesStateVal(eaeProjectDir, add: true, result);
            // updateComponentState.REQ must clear dest_name: Component_State_Msg is a reused struct, so a
            // report inheriting a stale dest_name would match a target actuator's BREQ and overwrite its
            // state_cmd with the reporting component's state (clobbering e.g. Bearing_PnP's Place).
            PatchRingReportClearDest(eaeProjectDir, result);
            // Emit CNF only on an actual command for this actuator (BCNF stays on every pass-through so
            // the ring advances): an unrelated ring report would otherwise re-fire the actuator with its
            // last retained command value.
            PatchRingCommandCnfOnlyOnDestination(eaeProjectDir, result);
            // reduce=false (rig): RESTORE the wired boundary inputs the embedded InterlockManager FB
            // declares. Must run every deploy (ExtractToEae/CopyDirToEae are copy-if-absent).
            NormalizeFiveStateInterlockConstants(eaeProjectDir, false, result);
            PatchProcess1RecipeArraySize(eaeProjectDir, result);
            PatchProcessNameStringSize(eaeProjectDir, result);
            // PatchSevenStateActuatorDataDriven removed — see CatToBasics comment.

            // MQTT event-driven state publishing (no-loss fix). Deploy-time,
            // additive, gated by cfg.MqttPublishEnabled so the stock template
            // library .cat.zip stays pristine and the hardware/sim paths are
            // untouched when MQTT is off. When on: drop the MqttStateFormatter
            // basic FB, then patch each CAT to fan an MQTT_PUBLISH off the
            // post-update state-change event (ActuatorCore.pst_out for the
            // actuator — fires on entry to all 5 states; FB1.CNF for the
            // sensor). The single shared MQTT_CONNECTION is injected into the
            // syslay by SystemLayoutInjector; PUBLISH binds to it by matching
            // ConnectionID value (no wire). See RUNBOOK.txt + the jitter gate.
            if (cfg.MqttPublishEnabled)
            {
                DeployMqttFormatter(eaeProjectDir, result);
                PatchCatMqttPublish(eaeProjectDir, "Five_State_Actuator_CAT",
                    stateEventSource: "ActuatorCore.pst_out",
                    stateDataSource: "ActuatorCore.current_state_to_process",
                    initSource: "StateHandling.INITO",
                    topicNameSource: "actuator_name", cfg, result);
                // The centre-home swivel (Bearing_PnP) uses the SAME ring-node CAT shape as Five_State:
                // ActuatorCore.pst_out fires on each state entry, ActuatorCore.current_state_to_process
                // carries the value, StateHandling.INITO inits, actuator_name is the per-instance topic.
                // So it gets the identical embedded publisher and publishes smc/bearing_pnp/state. Runs
                // before NormalizeSwivelSimSensorSource, which preserves the injected MqttPub/MqttFmt.
                PatchCatMqttPublish(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT",
                    stateEventSource: "ActuatorCore.pst_out",
                    stateDataSource: "ActuatorCore.current_state_to_process",
                    initSource: "StateHandling.INITO",
                    topicNameSource: "actuator_name", cfg, result);
                // The UR3e robot (Robot_Task_CAT) reports through the same ring node (StateHandling /
                // updateComponentState), but its core is StateMachine (Robot_Task_Core): StateMachine.pst_out
                // fires on each task-state change, StateMachine.current_state_to_process carries the value,
                // StateHandling.INITO inits, actuator_name is the per-instance topic. Publishes smc/robot/state
                // through the M262 connection. Only instantiated when EnableRobotTaskTail emits the robot; the
                // patch safely no-ops (warns) if the CAT is absent.
                PatchCatMqttPublish(eaeProjectDir, "Robot_Task_CAT",
                    stateEventSource: "StateMachine.pst_out",
                    stateDataSource: "StateMachine.current_state_to_process",
                    initSource: "StateHandling.INITO",
                    topicNameSource: "actuator_name", cfg, result);
                PatchCatMqttPublish(eaeProjectDir, "Sensor_Bool_CAT",
                    stateEventSource: "FB1.CNF",
                    stateDataSource: "FB1.Status",
                    initSource: "StateHandling.INITO",
                    topicNameSource: "name", cfg, result);
                // The CAT boundary is not modified for MQTT: the embedded MqttPub (above) taps
                // ActuatorCore.pst_out internally and needs no state_out exposure.
            }

            // Interlock interface: the four parallel Rule arrays + standalone RuleCount -> ONE
            // encapsulated input RuleTable : InterlockTable (= { Count : INT; Rules : ARRAY OF
            // InterlockRule }). The InterlockRule + InterlockTable datatypes + the three bidirectional
            // normalizers (both actuator CATs + the shared CommonInterlockEvaluator Basic FB) flip
            // together with the instance param (InterlockEmitter), driven by interlock.yaml useStruct.
            // The Centre-Home swivel carries the SAME interface wired into its CommonInterlockManager,
            // so it must reshape too. false restores the four arrays + scalar RuleCount.
            bool interlockStruct = InterlockConfig.Current.UseStruct;
            if (interlockStruct)
            {
                DeployInterlockRuleDatatype(eaeProjectDir, result);
                DeployInterlockTableDatatype(eaeProjectDir, result);
            }
            NormalizeFiveStateRuleArrays(eaeProjectDir, "Five_State_Actuator_CAT.fbt", "InterlockManager", interlockStruct, result);
            NormalizeFiveStateRuleArrays(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT.fbt", "CommonInterlockManager", interlockStruct, result);
            NormalizeCommonInterlockEvaluatorRules(eaeProjectDir, interlockStruct, result);

            // Actuator target states -> one Target : TargetStates struct that flows WHOLE into the
            // interlock evaluator (a custom FB). useTargetStruct flips the CAT inputs, the evaluator,
            // and the instance param (TargetEmitter) together. false restores the scalar inputs.
            // (Timers stay scalar: toWorkTime/toHomeTime/enable feed standard E_DELAY/AND FBs that EAE
            // cannot source from a struct member.)
            bool targetStruct = InterlockConfig.Current.UseTargetStruct;
            if (targetStruct)
                DeployTargetStatesDatatype(eaeProjectDir, result);
            NormalizeTargetStates(eaeProjectDir, "Five_State_Actuator_CAT.fbt", "InterlockManager",
                new[] { "TargetWork1State", "TargetHomeState" }, targetStruct, result);
            NormalizeTargetStates(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT.fbt", "CommonInterlockManager",
                new[] { "TargetWork1State", "TargetWork2State", "TargetHomeState" }, targetStruct, result);
            NormalizeCommonInterlockEvaluatorTargets(eaeProjectDir, targetStruct, result);

            // Telemetry: wrap each resource-level MQTT_CONNECTION in the 'Telemetry' composite
            // (Config:TelemetryConfig in / Health:TelemetryHealth out). A plain composite FB (no HMI
            // faceplate) in the Composite folder. It carries TWO helper Basic FBs — TelemetryUnpack
            // (Config struct -> the 6 MQTT_CONNECTION scalar inputs) and TelemetryPack (the 6 status
            // outputs -> Health struct) — so EVERY internal connection is whole-struct or
            // scalar-to-scalar. EAE constraint: a STRUCT connects whole, not per-member (ERR_NOT_ADAPTER),
            // so the member split is done in ST inside the helper FBs. false emits the raw
            // MQTT_CONNECTION instead.
            if (cfg.UseTelemetryCat)
            {
                // Clean slate FIRST: SweepTelemetryCat removes ALL telemetry artifacts (files + dfbproj
                // entries) for the current 'Telemetry' name AND the legacy 'Telemetry_CAT' name + the
                // helper FBs + the datatypes. This (a) migrates the rename away and (b) defeats the
                // copy-if-absent staleness — an older unsized .dt (which triggered ERR_CONST_INIT on the
                // 24-char URL) or member-level composite would otherwise survive a re-deploy. Then deploy
                // fresh; the dfbproj registration loop re-adds the entries with the current names.
                SweepTelemetryCat(eaeProjectDir, result);
                DeployTelemetryConfigDatatype(eaeProjectDir, result);
                DeployTelemetryHealthDatatype(eaeProjectDir, result);
                DeployArtifact(libPath, "Basic", "TelemetryUnpack", eaeProjectDir, result, isBasic: true);
                DeployArtifact(libPath, "Basic", "TelemetryPack", eaeProjectDir, result, isBasic: true);
                DeployArtifact(libPath, "Composite", "Telemetry", eaeProjectDir, result, isBasic: false);
            }
            else
            {
                // Flag OFF: SWEEP any previously-deployed Telemetry wrapper + helpers + datatypes. EAE
                // compiles every type in the dfbproj even when no instance uses it, so a lingering type
                // would still error. Removing the files + dfbproj entries clears that; the syslay/sysres
                // carry raw MQTT_CONNECTION instances (InjectMqttConn, same gate).
                SweepTelemetryCat(eaeProjectDir, result);
            }
            // Simulator-only swivel sensor synthesis. The Centre-Home swivel reads
            // atWork1/atWork2 from its internal Inputs SYMLINKMULTIVARDST, which subscribes
            // '$${PATH}atwork1' / '$${PATH}atWork2'. On the rig those resolve to the physical
            // SwivelArmAtPick / AtWork sensors (HCF-bound to that symbol). In the SIMULATOR
            // there are no physical sensors and nothing publishes them, so the core's ECC
            // stalls at ToWork1 forever (atWork1 never TRUE) — exactly the "swivel stuck at
            // Pick, engine waiting for target state" symptom. Repoint the two subscriptions at
            // the swivel's OWN drive-coil symlinks ('$${PATH}OutputToWork1' / 'OutputToWork2',
            // published by its Output SYMLINKMULTIVARSRC) so atWork1/atWork2 follow the coils:
            // the instant the ECC energises a coil to move to a work position, the matching
            // atWork closes and ToWork1->AtWork1 / ToWork2->AtWork2 fire. Entirely internal to
            // the Bearing_PnP instance (same resource, same $${PATH} scope) — no bridge FB, no
            // cross-resource symlink. Bidirectional + gated like NormalizeFiveStateRuleArrays:
            // reduce==false (rig) RESTORES '$${PATH}atwork1'/'atWork2' so the hardware path is
            // byte-identical. athome (NAME1) is untouched — it is timer-driven by the CAT's
            // ReturnToHomeHandler / No_Sensor_Handler_7SCH, so it needs no external publish.
            NormalizeSwivelSimSensorSource(eaeProjectDir, false, result);
            // Bearing_PnP's home is recipe-only (Assembly/Disassembly command bearing_pnp Home at the
            // end). This only STRIPS any previously-injected HomePoll/poll-gate FBs from the deployed CAT
            // so a re-deploy cleans the live tree; it adds nothing. Note: the 'Inputs' symlink is
            // sample-on-REQ, so if the core fails to re-observe atHome/atWork during a move, fix the
            // CAT/interface, not by re-adding a polling FB.
            StripCatHomeSensorPoll(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT", result);
            // Same coil->sensor synthesis for the Five_State actuators (Bearing_Gripper,
            // Shaft_*, CoverPNP_*). Bearing_Gripper deploys with WorkSensorFitted=TRUE on
            // M580 and the per-PLC no-sensor sysres override has not reliably reached the
            // M580 .sysres, so the gripper sat stuck at ToWork (atwork=FALSE) waiting for a
            // sensor nothing publishes. This type-level repoint makes its atwork/athome read
            // its own coils, so it advances regardless of WorkSensorFitted or which .sysres
            // it lives in. Inert for the M262 no-sensor actuators (they use their timer).
            NormalizeFiveStateSimSensorSource(eaeProjectDir, false, result);
            NormalizeFiveStateFaultEnables(eaeProjectDir, false, result);
            // Process FB recipe struct: collapse the 6 overlapping recipe arrays
            // into one Recipe : ARRAY OF RecipeStep on Process1_Generic + the
            // ProcessRuntime engine (datatype, NOT a new FB). Driven by UseRecipeStruct,
            // so the Test Runtime path uses the struct. The three
            // pieces flip together (deploy the datatype, reshape the composite,
            // reshape the engine) so the FB interface, engine ST and instance
            // parameter never disagree. Set UseRecipeStruct=false to revert the
            // runtime to the six parallel arrays.
            bool recipeStruct = cfg.UseRecipeStruct;
            if (recipeStruct)
                DeployRecipeStepDatatype(eaeProjectDir, result);
            NormalizeProcess1RecipeArrays(eaeProjectDir, recipeStruct, result);
            NormalizeProcessRuntimeRecipeArrays(eaeProjectDir, recipeStruct, result);
            // Strip the debug OutputVars (CurrentStep/CurrentStepType/WaitSatisfied) from the CMDREQ/SCNF
            // WITH lists so the deployed engine is byte-identical (migrates back a tree that had them added
            // for EAE Online Watch).
            NormalizeProcessEngineDebugWatch(eaeProjectDir, result);
            // check_wait is LEVEL-triggered: WaitSatisfied := state_table[Recipe[CurrentStep].Wait1Id].state
            // = Recipe[CurrentStep].Wait1State. That single line is the raw ProcessRuntime_Generic_v1
            // template body; NormalizeProcessRuntimeRecipeArrays above rewrites the array index into the
            // Recipe struct. A WAIT whose target state is already true when it arms is satisfied
            // immediately (valid for sensor levels like PartInHopper=On, process handoffs, stable home
            // states). The engine + Process1_Generic are force-re-extracted each deploy so no stale
            // check_wait body persists.

            GenerateCfgFiles(eaeProjectDir, result);
            RegisterInDfbproj(eaeProjectDir, result);
            // NOTE: MQTT_PUBLISH / MQTT_CONNECTION resolve via the already-
            // referenced Runtime.Base library (the deployed runtime DLL is
            // Runtime.Base and contains Runtime.NetConnectivity#MQTT_*).
            // Confirmed: EAE Projects\TrainingIIoT uses MQTT_CONNECTION with
            // ONLY a Runtime.Base reference, no separate Runtime.NetConnectivity.
            // A separate reference is a PHANTOM library ("Missing Referenced
            // Libraries: Runtime.NetConnectivity,24.1.0.22") so we do NOT add one.

            VerifyArraySizeConsistency(eaeProjectDir, result);

            // Trust-preservation guard. When an M262 sysdev already exists,
            // every device-layer write (sysdev rewrite, sysres resource-decl
            // rename, Topology Equipment JSON, network profiles) is skipped
            // to keep the controller-side trust binding intact. Application
            // content — sysres FBNetwork mirror, dfbproj registrations,
            // .hcf — still runs.
            bool m262DeviceExists = M262SysdevEmitter.M262SysdevAlreadyExists(cfg);

            string sysdevId = string.Empty;
            try
            {
                var sysdev = M262SysdevEmitter.Emit(cfg);
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
                var hcf = M262HwConfigCopier.Copy(cfg);
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

            foreach (var name in UniversalDataTypes)
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

        // Idempotent guard: ensure the internal SYMLINKMULTIVARDST inside the deployed Sensor_Bool_CAT.fbt
        // carries QI=TRUE. Without QI the DST defaults FALSE (disabled subscriber -> publishes to
        // '$${PATH}Input' silently dropped, sensor never registers on the ring). The zip already ships it;
        // this guards a future zip re-swap. Touches only Sensor_Bool_CAT.fbt.
        static void PatchSensorBoolCatDstQi(string eaeProjectDir, DeployResult result)
        {
            // Sensor_Bool_CAT deploys flat-ish under IEC61499/Sensor_Bool_CAT/.
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
                    // Already present (idempotent no-op) — but force the value
                    // to TRUE in case a swap shipped QI=FALSE.
                    foreach (var p in dst.Elements(ns + "Parameter")
                                 .Where(p => (string?)p.Attribute("Name") == "QI"))
                        p.SetAttributeValue("Value", "TRUE");
                }
                else
                {
                    // Insert QI=TRUE right after the NAME1 parameter so it
                    // sits alongside it, matching the shipped template shape.
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



        static void PatchProcess1RecipeArraySize(string eaeProjectDir, DeployResult result)
        {
            string[] recipeArrays =
            {
                "StepType", "CmdTargetName", "CmdStateArr",
                "Wait1Id", "Wait1State", "NextStep",
            };

            void PatchOne(string fbtPath, string label)
            {
                if (!File.Exists(fbtPath))
                {
                    fbtPath = Directory.EnumerateFiles(
                            Path.Combine(eaeProjectDir, "IEC61499"),
                            Path.GetFileName(fbtPath), SearchOption.AllDirectories)
                        .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                        ?? string.Empty;
                    if (string.IsNullOrEmpty(fbtPath)) return;
                }
                try
                {
                    var doc = System.Xml.Linq.XDocument.Load(
                        fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    var root = doc.Root;
                    if (root == null) return;
                    System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                    // Single source of truth for the recipe-array capacity lives
                    // in CodeGen so the deployed .fbt ArraySize and the recipe-
                    // length guard in ProcessRecipeArrayGenerator.Generate() can
                    // never drift apart.
                    var target = CodeGen.Translation.Process.ProcessRecipeArrayGenerator
                        .RecipeArraySize.ToString();
                    int changed = 0;
                    foreach (var vd in root.Descendants(ns + "VarDeclaration"))
                    {
                        var nm = (string?)vd.Attribute("Name") ?? string.Empty;
                        if (Array.IndexOf(recipeArrays, nm) < 0) continue;
                        if ((string?)vd.Attribute("ArraySize") == target) continue;
                        vd.SetAttributeValue("ArraySize", target);
                        changed++;
                    }
                    if (changed > 0)
                    {
                        doc.Save(fbtPath);
                        result.PatchesApplied.Add(
                            $"{label}: forced ArraySize={target} on {changed} recipe array InputVar(s)");
                        MapperLogger.Info(
                            $"[Deploy] {label}: recipe arrays ArraySize -> {target} ({changed} changed)");
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{label} recipe ArraySize guard failed: {ex.Message}");
                }
            }

            PatchOne(Path.Combine(eaeProjectDir, "IEC61499", "Process1_Generic",
                "Process1_Generic.fbt"), "Process1_Generic.fbt");
            PatchOne(Path.Combine(eaeProjectDir, "IEC61499",
                "ProcessRuntime_Generic_v1.fbt"), "ProcessRuntime_Generic_v1.fbt");
        }




        // Same QI=FALSE-by-default guard as Sensor_Bool_CAT, for an actuator CAT: force QI=TRUE on BOTH
        // its internal SYMLINKMULTIVARDST ("Inputs", subscribes $${PATH}athome/atwork) and
        // SYMLINKMULTIVARSRC ("Output", publishes $${PATH}OutputToHome/OutputToWork). Without QI the DST
        // rejects sensor publishes and the SRC never writes the solenoid. Idempotent.
        static void PatchCatSymlinkQi(string eaeProjectDir, string catName, DeployResult result)
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

        // ============================================================
        // MQTT event-driven state publishing — deploy-time injection.
        // All three methods below are no-ops unless cfg.MqttPublishEnabled
        // (the caller already gates them). Strictly additive to the CAT
        // FB-network; idempotent (skip if MqttPub already present).
        // ============================================================

        // Deploy the MqttStateFormatter basic FB (INT state -> STRING payload via INT_TO_STRING) into
        // IEC61499/. The CAT patch wires it between the actuator/sensor state output and
        // MQTT_PUBLISH.Payload1. Always overwrites so a re-deploy picks up the sized [255] payload.
        static void DeployMqttFormatter(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dst = Path.Combine(eaeProjectDir, "IEC61499", "MqttStateFormatter.fbt");
                // ALWAYS overwrite (was copy-if-absent): the const is deterministic + the type is
                // Mapper-owned, and a re-deploy onto a tree that still holds the OLD unsized-payload
                // version must pick up the sized [255] payload (else the WRN_UNSIZED_STRING marker stays).
                File.WriteAllText(dst, MqttStateFormatterFbt);
                result.PatchesApplied.Add("MqttStateFormatter.fbt deployed (INT→STRING[255] payload)");
                MapperLogger.Info("[Deploy][MQTT] MqttStateFormatter.fbt written to IEC61499/");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"MqttStateFormatter deploy failed: {ex.Message}");
            }
        }


        // InterlockRule datatype (4 INT fields) — the STRUCT the four parallel Rule arrays collapse
        // into (RuleTable : ARRAY OF InterlockRule). EAE regenerates the nxtDataType signature on load.
        const string InterlockRuleDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"InterlockRule\" Comment=\"One interlock rule as a struct: FromState/ToState/SourceID/BlockedState\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"5/24/2026\" Remarks=\"array-of-struct packaging of the 4 Rule arrays\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"FromState\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"ToState\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"SourceID\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"BlockedState\" Type=\"INT\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        // Deploy the InterlockRule datatype (one rule's four INT fields), registered via the
        // DataTypesDeployed loop. Idempotent (copy-if-absent).
        static void DeployInterlockRuleDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "InterlockRule.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, InterlockRuleDt);
                if (!result.DataTypesDeployed.Contains("InterlockRule"))
                    result.DataTypesDeployed.Add("InterlockRule");
                result.PatchesApplied.Add("InterlockRule.dt deployed + registered");
                MapperLogger.Info("[Deploy] InterlockRule.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"InterlockRule.dt deploy failed: {ex.Message}");
            }
        }

        // InterlockTable: the encapsulated interlock interface — Count : INT + Rules : ARRAY OF
        // InterlockRule. Rules' ArraySize comes from interlock.yaml ruleArraySize (no magic number).
        static string BuildInterlockTableDt() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"InterlockTable\" Comment=\"Encapsulated interlock interface: Count + the InterlockRule array\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"6/21/2026\" Remarks=\"single STRUCT input wrapping the rule count and rules\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"Count\" Type=\"INT\" />\r\n" +
            $"    <VarDeclaration Name=\"Rules\" Type=\"InterlockRule\" ArraySize=\"{InterlockConfig.Current.RuleArraySize}\" Namespace=\"Main\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        // Deploy the InterlockTable datatype so EAE resolves the single RuleTable : InterlockTable input
        // the normalizers expose on the actuator CATs + CommonInterlockEvaluator. Idempotent.
        static void DeployInterlockTableDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "InterlockTable.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, BuildInterlockTableDt());
                if (!result.DataTypesDeployed.Contains("InterlockTable"))
                    result.DataTypesDeployed.Add("InterlockTable");
                result.PatchesApplied.Add("InterlockTable.dt deployed + registered (encapsulated interlock input)");
                MapperLogger.Info("[Deploy] InterlockTable.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"InterlockTable.dt deploy failed: {ex.Message}");
            }
        }

        // TargetStates: the actuator target states folded into one struct.
        const string TargetStatesDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"TargetStates\" Comment=\"Actuator target states: Work1/Work2/Home\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"6/21/2026\" Remarks=\"single STRUCT input wrapping the target states\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"Work1\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Work2\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Home\" Type=\"INT\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        // Deploy the TargetStates datatype so EAE resolves the Target : TargetStates input the
        // normalizers expose. Idempotent.
        static void DeployTargetStatesDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "TargetStates.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, TargetStatesDt);
                if (!result.DataTypesDeployed.Contains("TargetStates"))
                    result.DataTypesDeployed.Add("TargetStates");
                result.PatchesApplied.Add("TargetStates.dt deployed + registered (encapsulated target input)");
                MapperLogger.Info("[Deploy] TargetStates.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"TargetStates.dt deploy failed: {ex.Message}");
            }
        }

        // Scalar target InputVar -> TargetStates field, and the Input-pin coords used on restore.
        static readonly Dictionary<string, string> TargetVarToField = new()
        {
            ["TargetWork1State"] = "Work1",
            ["TargetWork2State"] = "Work2",
            ["TargetHomeState"]  = "Home",
        };
        static readonly Dictionary<string, (string X, string Y)> TargetVarCoords = new()
        {
            ["TargetWork1State"] = ("1380", "2092"),
            ["TargetWork2State"] = ("1440", "2172"),
            ["TargetHomeState"]  = ("1380", "2192"),
        };
        // Which evaluator event originally sampled which target member (for the scalar restore).
        static readonly Dictionary<string, string> EvaluatorEventToTargetVar = new()
        {
            ["INIT"]      = "TargetWork1State",
            ["REQ_WORK2"] = "TargetWork2State",
            ["REQ_HOME"]  = "TargetHomeState",
        };

        // Fold an actuator CAT's target InputVars into one Target : TargetStates that flows whole into the
        // interlock evaluator instance interlockFbName. Bidirectional + idempotent; reduce==false restores scalars.
        static void NormalizeTargetStates(
            string eaeProjectDir, string catFileName, string interlockFbName,
            string[] targetInputs, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"),
                    catFileName, SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal)) ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add($"{catFileName} not found; Target normalize skipped.");
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
                    result.Warnings.Add($"{catFileName}: missing InterfaceList/FBNetwork; Target normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var initEvent = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                    .FirstOrDefault(e => e.Elements(ns + "With").Any(w =>
                        targetInputs.Contains((string?)w.Attribute("Var")) || (string?)w.Attribute("Var") == "Target"))
                    ?? iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");
                bool changed = false;

                if (reduce)
                {
                    var tgtVar = inputVars?.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "Target");
                    if (tgtVar == null && inputVars != null)
                    {
                        var t = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "Target"),
                            new System.Xml.Linq.XAttribute("Type", "TargetStates"),
                            new System.Xml.Linq.XAttribute("Namespace", "Main"));
                        var first = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => targetInputs.Contains((string?)v.Attribute("Name")));
                        if (first != null) first.AddBeforeSelf(t); else inputVars.Add(t);
                        changed = true;
                    }
                    foreach (var a in targetInputs)
                    {
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                        changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                        changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == a);
                        changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == a);
                    }
                    if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Target"))
                    {
                        initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "Target")));
                        changed = true;
                    }
                    if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == "Target"))
                    {
                        var pin = new System.Xml.Linq.XElement(ns + "Input",
                            new System.Xml.Linq.XAttribute("Name", "Target"),
                            new System.Xml.Linq.XAttribute("x", "1380"),
                            new System.Xml.Linq.XAttribute("y", "2092"),
                            new System.Xml.Linq.XAttribute("Type", "Data"));
                        var lastInput = net.Elements(ns + "Input").LastOrDefault();
                        if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                        changed = true;
                    }
                    if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == "Target"))
                    {
                        dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                            new System.Xml.Linq.XAttribute("Source", "Target"),
                            new System.Xml.Linq.XAttribute("Destination", interlockFbName + ".Target")));
                        changed = true;
                    }
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "Target");
                    changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "Target");
                    changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == "Target");
                    changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == "Target");
                    foreach (var a in targetInputs)
                    {
                        if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                        {
                            inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", a), new System.Xml.Linq.XAttribute("Type", "INT")));
                            changed = true;
                        }
                        if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == a))
                        {
                            initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", a)));
                            changed = true;
                        }
                        if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == a))
                        {
                            var (x, y) = TargetVarCoords[a];
                            var pin = new System.Xml.Linq.XElement(ns + "Input",
                                new System.Xml.Linq.XAttribute("Name", a),
                                new System.Xml.Linq.XAttribute("x", x), new System.Xml.Linq.XAttribute("y", y),
                                new System.Xml.Linq.XAttribute("Type", "Data"));
                            var lastInput = net.Elements(ns + "Input").LastOrDefault();
                            if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                            changed = true;
                        }
                        if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == a))
                        {
                            dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                                new System.Xml.Linq.XAttribute("Source", a),
                                new System.Xml.Linq.XAttribute("Destination", interlockFbName + "." + a)));
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    var catLabel = Path.GetFileNameWithoutExtension(catFileName);
                    result.PatchesApplied.Add(reduce
                        ? $"{catLabel}: target states -> Target : TargetStates (encapsulated)"
                        : $"{catLabel}: Target -> scalar target states (legacy)");
                    MapperLogger.Info($"[Deploy] {catLabel} Target normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catFileName} Target normalize failed: {ex.Message}");
            }
        }

        // Fold the CommonInterlockEvaluator's three target InputVars into one Target : TargetStates and
        // rewrite the Work1/Work2/Home algorithms to read Target.Work1/Work2/Home. Bidirectional + idempotent.
        static void NormalizeCommonInterlockEvaluatorTargets(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(Path.Combine(eaeProjectDir, "IEC61499"),
                    "CommonInterlockEvaluator.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal)) ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("CommonInterlockEvaluator.fbt not found; Target normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var iface = root.Element(ns + "InterfaceList");
                var basic = root.Element(ns + "BasicFB");
                if (iface == null || basic == null)
                {
                    result.Warnings.Add("CommonInterlockEvaluator.fbt: missing InterfaceList/BasicFB; Target normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var eventInputs = iface.Element(ns + "EventInputs");
                var targetVars = TargetVarToField.Keys.ToArray();
                bool changed = false;

                if (reduce)
                {
                    var tgtVar = inputVars?.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "Target");
                    if (tgtVar == null && inputVars != null)
                    {
                        var t = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "Target"),
                            new System.Xml.Linq.XAttribute("Type", "TargetStates"),
                            new System.Xml.Linq.XAttribute("Namespace", "Main"));
                        var first = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => targetVars.Contains((string?)v.Attribute("Name")));
                        if (first != null) first.AddBeforeSelf(t); else inputVars.Add(t);
                        changed = true;
                    }
                    foreach (var a in targetVars)
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => targetVars.Contains((string?)w.Attribute("Var")) || (string?)w.Attribute("Var") == "Target")) continue;
                        foreach (var a in targetVars)
                            changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Target"))
                        {
                            ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "Target")));
                            changed = true;
                        }
                    }
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "Target");
                    if (inputVars != null)
                        foreach (var a in targetVars)
                            if (!inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                            {
                                inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                    new System.Xml.Linq.XAttribute("Name", a), new System.Xml.Linq.XAttribute("Type", "INT")));
                                changed = true;
                            }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Target")) continue;
                        changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "Target");
                        // Restore only the member this event originally sampled (INIT→Work1, REQ_WORK2→Work2, REQ_HOME→Home).
                        var evName = (string?)ev.Attribute("Name");
                        if (evName != null && EvaluatorEventToTargetVar.TryGetValue(evName, out var tv)
                            && !ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == tv))
                        {
                            ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", tv)));
                            changed = true;
                        }
                    }
                }

                // Work1/Work2/Home algorithms: TargetWorkNState <-> Target.<field>.
                foreach (var alg in basic.Elements(ns + "Algorithm"))
                {
                    var stEl = alg.Element(ns + "ST");
                    if (stEl == null) continue;
                    var st = stEl.Value;
                    var before = st;
                    foreach (var kv in TargetVarToField)
                        st = reduce ? st.Replace(kv.Key, "Target." + kv.Value)
                                    : st.Replace("Target." + kv.Value, kv.Key);
                    if (st != before)
                    {
                        stEl.ReplaceNodes(new System.Xml.Linq.XCData(st));
                        changed = true;
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "CommonInterlockEvaluator: target states -> Target : TargetStates + algorithms (encapsulated)"
                        : "CommonInterlockEvaluator: Target -> scalar target states + algorithms (legacy)");
                    MapperLogger.Info($"[Deploy] CommonInterlockEvaluator Target normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"CommonInterlockEvaluator Target normalize failed: {ex.Message}");
            }
        }

        // The four parallel interlock-rule arrays that collapse into one
        // RuleTable : InterlockRule[10]. Order matches struct field order.
        static readonly string[] RuleArrayNames =
            { "RuleFromState", "RuleToState", "RuleSourceID", "RuleBlockedState" };
        static readonly Dictionary<string, string> RuleArrayToField = new()
        {
            ["RuleFromState"] = "FromState",
            ["RuleToState"] = "ToState",
            ["RuleSourceID"] = "SourceID",
            ["RuleBlockedState"] = "BlockedState",
        };

        // Interlock-struct reduction on an actuator CAT (gated by interlock.yaml useStruct): collapse the
        // four parallel Rule arrays (face InputVar + INIT With + boundary Input + DataConnection to the
        // interlock FB) into one RuleTable : InterlockRule[10]. Bidirectional + idempotent; reduce==false
        // restores the four arrays. The deployer is copy-if-absent, so the .fbt is reshaped to the flag each deploy.
        static void NormalizeFiveStateRuleArrays(
            string eaeProjectDir, string catFileName, string interlockFbName,
            bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    catFileName, SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add($"{catFileName} not found; RuleTable normalize skipped.");
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
                    result.Warnings.Add($"{catFileName}: missing InterfaceList/FBNetwork; RuleTable normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                // Find the event whose WITH list carries the rule data (the 4 arrays
                // when expanded, or RuleTable when collapsed). Five_State uses INIT; the
                // Centre-Home CAT may use a different event -- search, don't hardcode.
                var initEvent = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                    .FirstOrDefault(e => e.Elements(ns + "With").Any(w =>
                        RuleArrayNames.Contains((string?)w.Attribute("Var"))
                        || (string?)w.Attribute("Var") == "RuleTable"))
                    ?? iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");

                bool changed = false;
                // The CAT-level interlock inputs the encapsulated form removes (4 arrays + scalar count).
                var scalarAndArrays = RuleArrayNames.Concat(new[] { "RuleCount" }).ToArray();
                string cap = InterlockConfig.Current.RuleArraySize.ToString();

                if (reduce)
                {
                    // Encapsulated interface: ONE input RuleTable : InterlockTable (= Count + Rules[]).
                    // Place RuleTable where the interlock block was (before RuleCount), then strip the
                    // 4 arrays + standalone RuleCount. An existing flat RuleTable (InterlockRule[]) is
                    // retyped in place to InterlockTable.
                    var rtVar = inputVars?.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleTable");
                    if (rtVar != null)
                    {
                        if ((string?)rtVar.Attribute("Type") != "InterlockTable" || rtVar.Attribute("ArraySize") != null)
                        {
                            rtVar.SetAttributeValue("Type", "InterlockTable");
                            rtVar.SetAttributeValue("Namespace", "Main");
                            rtVar.Attribute("ArraySize")?.Remove();
                            changed = true;
                        }
                    }
                    else if (inputVars != null)
                    {
                        var rt = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "RuleTable"),
                            new System.Xml.Linq.XAttribute("Type", "InterlockTable"),
                            new System.Xml.Linq.XAttribute("Namespace", "Main"));
                        var rc = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleCount");
                        if (rc != null) rc.AddBeforeSelf(rt); else inputVars.Add(rt);
                        changed = true;
                    }
                    foreach (var a in scalarAndArrays)
                    {
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                        changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                        changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == a);
                        changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == a);
                    }
                    if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "RuleTable"))
                    {
                        initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "RuleTable")));
                        changed = true;
                    }
                    if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == "RuleTable"))
                    {
                        var pin = new System.Xml.Linq.XElement(ns + "Input",
                            new System.Xml.Linq.XAttribute("Name", "RuleTable"),
                            new System.Xml.Linq.XAttribute("x", "1320"),
                            new System.Xml.Linq.XAttribute("y", "1852"),
                            new System.Xml.Linq.XAttribute("Type", "Data"));
                        var lastInput = net.Elements(ns + "Input").LastOrDefault();
                        if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                        changed = true;
                    }
                    if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == "RuleTable"))
                    {
                        dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                            new System.Xml.Linq.XAttribute("Source", "RuleTable"),
                            new System.Xml.Linq.XAttribute("Destination", interlockFbName + ".RuleTable")));
                        changed = true;
                    }
                }
                else
                {
                    // Restore the legacy interface: scalar RuleCount + the 4 INT arrays; drop RuleTable.
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "RuleTable");
                    changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "RuleTable");
                    changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == "RuleTable");
                    changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == "RuleTable");

                    var coords = new Dictionary<string, (string X, string Y)>
                    {
                        ["RuleCount"] = ("1440", "1492"),
                        ["RuleFromState"] = ("1320", "2052"),
                        ["RuleToState"] = ("1320", "1752"),
                        ["RuleSourceID"] = ("1300", "1852"),
                        ["RuleBlockedState"] = ("1320", "1952"),
                    };
                    foreach (var a in scalarAndArrays)
                    {
                        if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                        {
                            var vd = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", a),
                                new System.Xml.Linq.XAttribute("Type", "INT"));
                            if (a != "RuleCount") vd.SetAttributeValue("ArraySize", cap);
                            inputVars.Add(vd);
                            changed = true;
                        }
                        if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == a))
                        {
                            initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", a)));
                            changed = true;
                        }
                        if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == a))
                        {
                            var (x, y) = coords[a];
                            var pin = new System.Xml.Linq.XElement(ns + "Input",
                                new System.Xml.Linq.XAttribute("Name", a),
                                new System.Xml.Linq.XAttribute("x", x),
                                new System.Xml.Linq.XAttribute("y", y),
                                new System.Xml.Linq.XAttribute("Type", "Data"));
                            var lastInput = net.Elements(ns + "Input").LastOrDefault();
                            if (lastInput != null) lastInput.AddAfterSelf(pin); else net.Add(pin);
                            changed = true;
                        }
                        if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == a))
                        {
                            dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                                new System.Xml.Linq.XAttribute("Source", a),
                                new System.Xml.Linq.XAttribute("Destination", interlockFbName + "." + a)));
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    var catLabel = Path.GetFileNameWithoutExtension(catFileName);
                    result.PatchesApplied.Add(reduce
                        ? $"{catLabel}: 4 arrays + RuleCount -> RuleTable : InterlockTable (encapsulated)"
                        : $"{catLabel}: RuleTable -> 4 arrays + RuleCount (legacy interface)");
                    MapperLogger.Info($"[Deploy] {catLabel} RuleTable normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State_Actuator_CAT RuleTable normalize failed: {ex.Message}");
            }
        }

        // Deploy-time normalizer for the centre-home swivel's sensor source. reduce=false (the rig path,
        // the only call today) keeps the physical Inputs on the real sensor symlinks. reduce=true inserted
        // a SimCentreHomeSensor_7SCH Basic deriving atHome/atWork1/atWork2 from current_state_to_process
        // (vestigial sim-position synthesis).
        static void NormalizeSwivelSimSensorSource(string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Seven_State_Actuator_Centre_Home_CAT.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
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

                void AddSimPosition()
                {
                    var fb = net.Elements(ns + "FB")
                        .FirstOrDefault(f => (string?)f.Attribute("Name") == simFbName);
                    if (fb == null)
                    {
                        var nextId = net.Elements(ns + "FB")
                            .Select(f => int.TryParse((string?)f.Attribute("ID"), out var id) ? id : 0)
                            .DefaultIfEmpty(0)
                            .Max() + 1;
                        fb = new XElement(ns + "FB",
                            new XAttribute("ID", nextId),
                            new XAttribute("Name", simFbName),
                            new XAttribute("Type", "SimCentreHomeSensor_7SCH"),
                            new XAttribute("x", "6200"),
                            new XAttribute("y", "4040"),
                            new XAttribute("Namespace", "Main"));
                    }
                    else
                    {
                        fb.Remove();
                    }

                    var firstNonFb = net.Elements()
                        .FirstOrDefault(e => e.Name.LocalName != "FB");
                    if (firstNonFb != null) firstNonFb.AddBeforeSelf(fb);
                    else net.Add(fb);
                    changed = true;
                }

                // Always leave the actual Inputs block subscribed to the real physical
                // sensor names. In sim mode the block is no longer the source of the
                // core's position pins; in hardware mode these connections are restored.
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

                if (reduce)
                {
                    AddSimPosition();

                    RemoveEvent("Inputs.INITO", "ActuatorCore.INIT");
                    AddEvent("Inputs.INITO", "SimPosition.INIT");
                    AddEvent("SimPosition.INITO", "ActuatorCore.INIT");
                    AddEvent("ActuatorCore.pst_out", "SimPosition.REQ");
                    AddEvent("SimPosition.CNF", "FB1.EI");

                    AddData("ActuatorCore.current_state_to_process", "SimPosition.CurrentState");
                    AddData("SimPosition.atHome", "ActuatorCore.atHome");
                    AddData("SimPosition.atWork1", "ActuatorCore.atWork1");
                    AddData("SimPosition.atWork2", "ActuatorCore.atWork2");
                    AddData("SimPosition.atHome", "IThis.atHome");
                    AddData("SimPosition.atWork1", "IThis.atWork1");
                    AddData("SimPosition.atWork2", "IThis.atWork2");
                    AddData("SimPosition.atHome", "FaultHandling.atHome");
                    AddData("SimPosition.atWork1", "FaultHandling.atWork1");
                    AddData("SimPosition.atWork2", "FaultHandling.atWork2");
                }
                else
                {
                    RemoveSimPosition();

                    AddEvent("Inputs.INITO", "ActuatorCore.INIT");
                    // Hardware/Test Runtime must use the real atHome input. The
                    // ReturnToHomeHandler also raises atHomeOutput from work->home
                    // timers, which is useful only for synthetic/no-sensor behaviour;
                    // using it on the rig can make a Home command stop on a timer
                    // instead of the centre sensor, or miss the centre and continue
                    // toward the opposite work position.
                    AddData("Inputs.VALUE1", "ActuatorCore.atHome");
                    AddData("Inputs.VALUE2", "ActuatorCore.atWork1");
                    AddData("Inputs.VALUE3", "ActuatorCore.atWork2");
                    AddData("Inputs.VALUE1", "IThis.atHome");
                    AddData("Inputs.VALUE2", "IThis.atWork1");
                    AddData("Inputs.VALUE3", "IThis.atWork2");
                    AddData("Inputs.VALUE1", "FaultHandling.atHome");
                    AddData("Inputs.VALUE2", "FaultHandling.atWork1");
                    AddData("Inputs.VALUE3", "FaultHandling.atWork2");
                }

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

                if (!reduce && hasSimPosition)
                {
                    throw new InvalidOperationException(
                        "Hardware/Test Runtime cannot use simulator centre-home wiring: " +
                        "Seven_State_Actuator_Centre_Home_CAT still contains SimPosition/SimCentreHomeSensor_7SCH.");
                }

                if (changed)
                {
                    // Retry on a transient file lock (EAE briefly touching the .fbt during a
                    // background scan) instead of hard-aborting. A persistently-open editor tab
                    // still falls through to the abort below (the user must close it).
                    SaveXmlWithRetry(doc, fbt);
                    result.PatchesApplied.Add(reduce
                        ? "Seven_State_Actuator_Centre_Home_CAT: simulator position model inserted (mutually-exclusive home/work sensors)"
                        : "Seven_State_Actuator_Centre_Home_CAT: simulator position model removed; physical sensor wiring restored");
                    MapperLogger.Info($"[Deploy] Centre-Home swivel sim-sensor source normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                if (!reduce)
                    throw new InvalidOperationException(
                        "Hardware/Test Runtime cannot continue because the centre-home swivel CAT could not be restored to physical sensor wiring. " +
                        "Close any open Seven_State_Actuator_Centre_Home_CAT editor tab in EAE and regenerate.",
                        ex);

                result.Warnings.Add($"Centre-Home swivel sim-sensor normalize failed: {ex.Message}");
            }
        }

        // Bearing_PnP's home is recipe-only. This method ONLY STRIPS any previously-injected poll
        // machinery (HomePoll / PollGate1 / PollGate2 / PollWindow + their connections) from the deployed
        // CAT so a re-deploy cleans the live tree; it adds nothing and instantiates no replacement FB.
        // The committed .cat.zip never carried these (always deploy-injected). Note: the CAT's 'Inputs'
        // SYMLINKMULTIVARDST is sample-on-REQ; if the core fails to re-observe positions after a command,
        // fix the CAT/interface (an input-change event or a cyclic task driving Inputs.REQ), not by
        // re-adding a polling FB.
        static void StripCatHomeSensorPoll(string eaeProjectDir, string catName, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    catName + ".fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
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
                if (!pollFbNames.Any(Has)) return;   // already clean — nothing to strip, add nothing

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

        // Deploy-time normalizer for the Five_State CAT's internal Inputs SYMLINKMULTIVARDST source.
        // reduce=false (the rig path, the only call today) restores '$${PATH}athome'/'$${PATH}atwork' so
        // sensored actuators read their real sensors. reduce=true repointed athome/atwork at the OWN
        // drive-coil symlinks ('$${PATH}OutputTo*') so they follow the coils when nothing publishes the
        // physical sensors (vestigial sim path; inert for no-sensor instances, which use InputHandler's timer).
        static void NormalizeFiveStateSimSensorSource(string eaeProjectDir, bool reduce, DeployResult result)
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
                    ("NAME1", reduce ? "'$${PATH}OutputToHome'" : "'$${PATH}athome'"),
                    ("NAME2", reduce ? "'$${PATH}OutputToWork'" : "'$${PATH}atwork'"),
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
                    result.PatchesApplied.Add(reduce
                        ? "Five_State_Actuator_CAT: Inputs athome/atwork -> coil symlinks (sim sensor synthesis)"
                        : "Five_State_Actuator_CAT: Inputs athome/atwork -> physical sensor symlinks (hardware)");
                    MapperLogger.Info($"[Deploy] Five_State sim-sensor source normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State sim-sensor normalize failed: {ex.Message}");
            }
        }


        // Interlock-struct reduction on the CommonInterlockEvaluator Basic FB (gated by interlock.yaml
        // useStruct): collapse the four Rule arrays into RuleTable : InterlockRule[10] across the InputVars,
        // the three event With lists (REQ_WORK1/WORK2/HOME), AND the Evaluate ST. Bidirectional + idempotent;
        // reduce==false restores the four arrays. Same numbers feed Evaluate either way, just as struct fields.
        static void NormalizeCommonInterlockEvaluatorRules(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "CommonInterlockEvaluator.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("CommonInterlockEvaluator.fbt not found; RuleTable normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var iface = root.Element(ns + "InterfaceList");
                var basic = root.Element(ns + "BasicFB");
                if (iface == null || basic == null)
                {
                    result.Warnings.Add("CommonInterlockEvaluator.fbt: missing InterfaceList/BasicFB; RuleTable normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var eventInputs = iface.Element(ns + "EventInputs");

                bool changed = false;
                var scalarAndArrays = RuleArrayNames.Concat(new[] { "RuleCount" }).ToArray();
                string cap = InterlockConfig.Current.RuleArraySize.ToString();

                if (reduce)
                {
                    // ONE input RuleTable : InterlockTable. Retype a flat RuleTable if present, else
                    // insert before RuleCount; then strip the 4 arrays + scalar RuleCount.
                    var rtVar = inputVars?.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleTable");
                    if (rtVar != null)
                    {
                        if ((string?)rtVar.Attribute("Type") != "InterlockTable" || rtVar.Attribute("ArraySize") != null)
                        {
                            rtVar.SetAttributeValue("Type", "InterlockTable");
                            rtVar.SetAttributeValue("Namespace", "Main");
                            rtVar.Attribute("ArraySize")?.Remove();
                            changed = true;
                        }
                    }
                    else if (inputVars != null)
                    {
                        var rt = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "RuleTable"),
                            new System.Xml.Linq.XAttribute("Type", "InterlockTable"),
                            new System.Xml.Linq.XAttribute("Namespace", "Main"));
                        var rc = inputVars.Elements(ns + "VarDeclaration").FirstOrDefault(v => (string?)v.Attribute("Name") == "RuleCount");
                        if (rc != null) rc.AddBeforeSelf(rt); else inputVars.Add(rt);
                        changed = true;
                    }
                    foreach (var a in scalarAndArrays)
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == a);
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => scalarAndArrays.Contains((string?)w.Attribute("Var")) || (string?)w.Attribute("Var") == "RuleTable")) continue;
                        foreach (var a in scalarAndArrays)
                            changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == a);
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "RuleTable"))
                        {
                            ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "RuleTable")));
                            changed = true;
                        }
                    }
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "RuleTable");
                    if (inputVars != null)
                        foreach (var a in scalarAndArrays)
                            if (!inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == a))
                            {
                                var vd = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                    new System.Xml.Linq.XAttribute("Name", a),
                                    new System.Xml.Linq.XAttribute("Type", "INT"));
                                if (a != "RuleCount") vd.SetAttributeValue("ArraySize", cap);
                                inputVars.Add(vd);
                                changed = true;
                            }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "RuleTable")) continue;
                        changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "RuleTable");
                        foreach (var a in scalarAndArrays)
                            if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == a))
                            {
                                ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", a)));
                                changed = true;
                            }
                    }
                }

                // Evaluate ST: RuleCount<->RuleTable.Count and RuleX[i]<->RuleTable.Rules[i].X.
                var stEl = basic.Elements(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "Evaluate")?
                    .Element(ns + "ST");
                if (stEl != null)
                {
                    var st = stEl.Value;
                    var before = st;
                    if (reduce)
                    {
                        st = st.Replace("RuleCount", "RuleTable.Count");
                        st = st.Replace("RuleTable[i].", "RuleTable.Rules[i].");          // migrate a flat RuleTable[i].X form
                        foreach (var a in RuleArrayNames)
                            st = st.Replace(a + "[i]", "RuleTable.Rules[i]." + RuleArrayToField[a]);
                    }
                    else
                    {
                        st = st.Replace("RuleTable.Count", "RuleCount");
                        foreach (var a in RuleArrayNames)
                        {
                            st = st.Replace("RuleTable.Rules[i]." + RuleArrayToField[a], a + "[i]");
                            st = st.Replace("RuleTable[i]." + RuleArrayToField[a], a + "[i]"); // legacy flat
                        }
                    }
                    if (st != before)
                    {
                        stEl.ReplaceNodes(new System.Xml.Linq.XCData(st));
                        changed = true;
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "CommonInterlockEvaluator: 4 arrays + RuleCount -> RuleTable : InterlockTable + Evaluate ST (encapsulated)"
                        : "CommonInterlockEvaluator: RuleTable -> 4 arrays + RuleCount + Evaluate ST (legacy)");
                    MapperLogger.Info($"[Deploy] CommonInterlockEvaluator RuleTable normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"CommonInterlockEvaluator RuleTable normalize failed: {ex.Message}");
            }
        }

        // Interface reduction on Five_State_Actuator_CAT: drop the two derived fault-enable inputs
        // (enableToWorkFaultTimeout/enableToHomeFaultTimeout). The Mapper always sets each = its
        // sensor-fitted flag and FB17/FB14 already AND the enable with the same flag, so re-pointing
        // FB17.IN2/FB14.IN2 at the fitted lines gives AND(fitted,fitted)=fitted: identical, two fewer pins.
        // Bidirectional + idempotent; reduce==false (the rig path today) restores the inputs.
        static void NormalizeFiveStateFaultEnables(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var map = new[]
            {
                new { Enable = "enableToWorkFaultTimeout", Dest = "FB17.IN2", Fitted = "WorkSensorFitted", X = "1280", Y = "5772" },
                new { Enable = "enableToHomeFaultTimeout", Dest = "FB14.IN2", Fitted = "HomeSensorFitted", X = "1260", Y = "5292" },
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
                    // Re-point the AND-gate IN2: reduce -> sensor-fitted, restore -> enable input.
                    var wanted = reduce ? m.Fitted : m.Enable;
                    var conn = dataConns?.Elements(ns + "Connection")
                        .FirstOrDefault(c => (string?)c.Attribute("Destination") == m.Dest);
                    if (conn != null && (string?)conn.Attribute("Source") != wanted)
                    {
                        conn.SetAttributeValue("Source", wanted);
                        changed = true;
                    }

                    if (reduce)
                    {
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"),
                            v => (string?)v.Attribute("Name") == m.Enable);
                        changed |= RemoveElems(initEvent?.Elements(ns + "With"),
                            w => (string?)w.Attribute("Var") == m.Enable);
                        changed |= RemoveElems(net.Elements(ns + "Input"),
                            i => (string?)i.Attribute("Name") == m.Enable);
                    }
                    else
                    {
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
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "Five_State_Actuator_CAT: fault-enable inputs derived from sensor-fitted (sim — 2 inputs removed)"
                        : "Five_State_Actuator_CAT: fault-enable inputs restored as wired inputs (hardware)");
                    MapperLogger.Info($"[Deploy] Five_State_Actuator_CAT fault-enable normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Five_State_Actuator_CAT fault-enable normalize failed: {ex.Message}");
            }
        }


        // Additive deploy-time patch: fan an MQTT_PUBLISH off the CAT's POST-UPDATE state-change event so
        // every transition publishes the same scan it happens (before OPC UA/WebSocket samplers that alias
        // brief states out). Purely additive — the state event keeps its targets, one fan-out added.
        // Params: stateEventSource = post-update event (actuator ActuatorCore.pst_out / sensor FB1.CNF);
        // stateDataSource = INT state (current_state_to_process / FB1.Status); initSource = INITO to seed
        // MqttFmt/MqttPub INIT; topicNameSource = CAT STRING InputVar with the per-instance name
        // (actuator_name / name) wired into MqttPub.Topic1 — MQTT_PUBLISH does NOT resolve $${PATH} at
        // runtime, so the topic must be a concrete per-instance name.
        static void PatchCatMqttPublish(string eaeProjectDir, string catName,
            string stateEventSource, string stateDataSource, string initSource,
            string topicNameSource,
            MapperConfig cfg, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    catName + ".fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
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

                // Re-patchable: remove any existing MqttPub/MqttFmt FBs + their wires before re-emitting
                // fresh, so a stale topic/parameter shape can't persist across deploys (the CAT folder is
                // copy-if-absent). Removing first guarantees each deploy reflects the latest source.
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

                // Allocate the two new FB IDs from MAX existing FB ID + 1, NOT from the stale
                // Configuration.FB.IDCounter alone: the interlock refactor added FBs (e.g.
                // CommonInterlockManager=62) past the counter, so an IDCounter-only allocation
                // collided MqttFmt/MqttPub with them (and StatusScan=11 in Robot_Task). The
                // old MqttFmt/MqttPub were already removed above, so the max-scan excludes them.
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

                // EAE stores a configured GENERIC FB as a HASHED VARIANT in
                // namespace "Main" — confirmed by the working MQTT_SUBSCRIBE in
                // TrainingIIoT: Type="MQTT_SUBSCRIBE_115480E69E664F878"
                // Namespace="Main" + InterfaceParams="Runtime.NetConnectivity#CNTX:=1".
                // The hash 115480E69E664F878 is derived from the InterfaceParams
                // (CNTX:=1) and is identical for PUBLISH (EAE itself looked for
                // MQTT_PUBLISH_115480E69E664F878.gfbt). Emitting the plain base
                // type name in namespace Runtime.NetConnectivity is what caused
                // ERR_NO_SUCH_TYPE. NOTE: if channel count ever changes from 1,
                // this hash changes too.
                const string MqttPublishVariant = "MQTT_PUBLISH_115480E69E664F878";
                var pubFb = new System.Xml.Linq.XElement(ns + "FB",
                    new System.Xml.Linq.XAttribute("ID", pubId),
                    new System.Xml.Linq.XAttribute("Name", "MqttPub"),
                    new System.Xml.Linq.XAttribute("Type", MqttPublishVariant),
                    new System.Xml.Linq.XAttribute("x", "8600"),
                    new System.Xml.Linq.XAttribute("y", "2580"),
                    new System.Xml.Linq.XAttribute("Namespace", "Main"));
                // MQTT_PUBLISH is a GENERIC multi-channel FB. Its numbered
                // channel ports (Topic1/Payload1/QoS1/Retain1 + event PUBLISH1)
                // do NOT exist until the channel count is set via this
                // InterfaceParams attribute — exactly like the SYMLINKMULTIVAR
                // FBs already in this CAT. CNTX:=1 = one publish channel.
                // Confirmed against working MQTT_SUBSCRIBE / MQTT_CONNECTION
                // instances in EAE Projects\TrainingIIoT
                // (Configuration.GenericFBType.InterfaceParams =
                // "Runtime.NetConnectivity#CNTX:=1"). Omitting it is what
                // produced "Port 'MqttPub.Topic1' does not exist".
                pubFb.Add(new System.Xml.Linq.XElement(ns + "Attribute",
                    new System.Xml.Linq.XAttribute("Name", "Configuration.GenericFBType.InterfaceParams"),
                    new System.Xml.Linq.XAttribute("Value", "Runtime.NetConnectivity#CNTX:=1")));
                void P(System.Xml.Linq.XElement fb, string n, string v) =>
                    fb.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                        new System.Xml.Linq.XAttribute("Name", n),
                        new System.Xml.Linq.XAttribute("Value", v)));
                P(pubFb, "QI", "TRUE");
                // ConnectionID is the SHARED STRING binding key — the per-resource MQTT_CONNECTION AND
                // every embedded publisher must carry the SAME value (cfg.MqttConnectionName, e.g. 'SMC')
                // so each resource's publishers bind to its LOCAL connection. It is deliberately NOT the
                // per-resource ClientIdentifier (which stays unique so the broker keeps all three).
                P(pubFb, "ConnectionID", Q(cfg.MqttConnectionName));

                // Per-instance topic for both rig AND sim. RootPath is the
                // common prefix ('smc'); Topic1 is wired from each CAT's per-instance name InputVar (see
                // DataConnection below) so each instance publishes to <prefix>/<lowercased_name>
                // (smc/coverpnp_hr, smc/bearing_pnp, ...). EAE constraint: $${PATH} is not resolved at
                // runtime, so the topic must be concrete. Runtime note: M262/M580 firmware gate MQTT
                // (ReturnCode 50); only BX1 (Soft dPAC) actually pushes to the broker.
                P(pubFb, "RootPath", Q(cfg.MqttTopicRoot));
                // Topic1 intentionally NOT a parameter — wired below.
                P(pubFb, "QoS1", cfg.MqttQoS.ToString());
                P(pubFb, "Retain1", cfg.MqttRetain ? "TRUE" : "FALSE");

                // Insert the two FBs after the last existing <FB>.
                var lastFb = net.Elements(ns + "FB").LastOrDefault();
                if (lastFb != null) { lastFb.AddAfterSelf(pubFb); lastFb.AddAfterSelf(fmtFb); }
                else { net.Add(fmtFb); net.Add(pubFb); }

                // EAE constraint: a <Frame> carrying <Parameter> children (the syslay frame style) is
                // invalid inside a CAT (FBType) FBNetwork (ERR_XML_UNKNOWN_TAG). Strip any stale
                // FRAME_MQTT so a re-deploy cleans it out.
                net.Elements(ns + "Frame")
                   .Where(fr => (string?)fr.Attribute("Name") == "FRAME_MQTT").Remove();

                // Ensure connection containers exist.
                var ec = net.Element(ns + "EventConnections");
                if (ec == null) { ec = new System.Xml.Linq.XElement(ns + "EventConnections"); net.Add(ec); }
                var dc = net.Element(ns + "DataConnections");
                if (dc == null) { dc = new System.Xml.Linq.XElement(ns + "DataConnections"); net.Add(dc); }

                void Conn(System.Xml.Linq.XElement parent, string s, string d) =>
                    parent.Add(new System.Xml.Linq.XElement(ns + "Connection",
                        new System.Xml.Linq.XAttribute("Source", s),
                        new System.Xml.Linq.XAttribute("Destination", d)));

                // Events (additive fan-out — stateEventSource keeps its targets).
                Conn(ec, stateEventSource, "MqttFmt.REQ");
                Conn(ec, "MqttFmt.CNF", "MqttPub.PUBLISH1");
                Conn(ec, initSource, "MqttFmt.INIT");
                Conn(ec, initSource, "MqttPub.INIT");
                // Data.
                Conn(dc, stateDataSource, "MqttFmt.state");
                Conn(dc, "MqttFmt.payload", "MqttPub.Payload1");
                // Per-instance topic suffix — wire the CAT's per-instance name InputVar
                // (Five_State.actuator_name / Sensor_Bool.name) into MqttPub.Topic1. Runtime concatenates
                // RootPath/Topic1, so each instance publishes to <MqttTopicRoot>/<name>.
                Conn(dc, topicNameSource, "MqttPub.Topic1");

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

        // Interface reduction for Five_State_Actuator_CAT's InterlockManager constants (TargetWork1State=2,
        // TargetHomeState=4 — identical on every instance, hardcoded by BuildActuatorParameters). reduce=true
        // deletes those two boundary inputs and bakes the constants onto the embedded InterlockManager FB
        // (17->15 pins, no runtime value change). Bidirectional (the deployed CAT is a single reused file);
        // reduce=false (the rig path, the only call today) restores the wired inputs. Idempotent both ways.
        static void NormalizeFiveStateInterlockConstants(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            // Values mirror BuildActuatorParameters EXACTLY (2 = AtWork settled,
            // 4 = AtHome end-of-cycle latch) so the reduction changes the
            // interface only, never behaviour. Re-add coordinates mirror the
            // stock template (EAE recomputes layout on load regardless).
            var consts = new[]
            {
                new { Name = "TargetWork1State", Value = "2", X = "1380", Y = "2092" },
                new { Name = "TargetHomeState",  Value = "4", X = "1380", Y = "2192" },
            };

            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Five_State_Actuator_CAT.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add(
                    "Five_State_Actuator_CAT.fbt not found; interlock-constant normalize skipped.");
                return;
            }

            try
            {
                var doc = System.Xml.Linq.XDocument.Load(
                    fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var iface = root.Element(ns + "InterfaceList");
                var net = root.Element(ns + "FBNetwork");
                if (iface == null || net == null)
                {
                    result.Warnings.Add(
                        "Five_State_Actuator_CAT.fbt: missing InterfaceList/FBNetwork; normalize skipped.");
                    return;
                }

                var inputVars = iface.Element(ns + "InputVars");
                var initEvent = iface.Element(ns + "EventInputs")?
                    .Elements(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");
                var interlock = net.Elements(ns + "FB")
                    .FirstOrDefault(f => (string?)f.Attribute("Name") == "InterlockManager");
                if (interlock == null)
                {
                    result.Warnings.Add(
                        "Five_State_Actuator_CAT.fbt: InterlockManager FB not found; normalize skipped.");
                    return;
                }

                // Remove matching elements via instance Remove() (no
                // IEnumerable<XElement>.Remove() extension — this file has no
                // `using System.Xml.Linq`). Returns true if anything was removed.
                bool RemoveWhere(IEnumerable<System.Xml.Linq.XElement>? src,
                    Func<System.Xml.Linq.XElement, bool> pred)
                {
                    if (src == null) return false;
                    var hits = src.Where(pred).ToList();
                    foreach (var h in hits) h.Remove();
                    return hits.Count > 0;
                }

                bool changed = false;

                foreach (var c in consts)
                {
                    if (reduce)
                    {
                        changed |= RemoveWhere(inputVars?.Elements(ns + "VarDeclaration"),
                            v => (string?)v.Attribute("Name") == c.Name);
                        changed |= RemoveWhere(initEvent?.Elements(ns + "With"),
                            w => (string?)w.Attribute("Var") == c.Name);
                        changed |= RemoveWhere(net.Elements(ns + "Input"),
                            i => (string?)i.Attribute("Name") == c.Name);
                        changed |= RemoveWhere(dataConns?.Elements(ns + "Connection"),
                            x => (string?)x.Attribute("Source") == c.Name);

                        bool hasParam = interlock.Elements(ns + "Parameter")
                            .Any(p => (string?)p.Attribute("Name") == c.Name);
                        if (!hasParam)
                        {
                            interlock.Add(new System.Xml.Linq.XElement(ns + "Parameter",
                                new System.Xml.Linq.XAttribute("Name", c.Name),
                                new System.Xml.Linq.XAttribute("Value", c.Value)));
                            changed = true;
                        }
                    }
                    else
                    {
                        // Restore the wired interface (hardware / Button 3).
                        changed |= RemoveWhere(interlock.Elements(ns + "Parameter"),
                            p => (string?)p.Attribute("Name") == c.Name);

                        if (inputVars != null &&
                            !inputVars.Elements(ns + "VarDeclaration")
                                .Any(v => (string?)v.Attribute("Name") == c.Name))
                        {
                            inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", c.Name),
                                new System.Xml.Linq.XAttribute("Type", "INT")));
                            changed = true;
                        }
                        if (initEvent != null &&
                            !initEvent.Elements(ns + "With")
                                .Any(w => (string?)w.Attribute("Var") == c.Name))
                        {
                            initEvent.Add(new System.Xml.Linq.XElement(ns + "With",
                                new System.Xml.Linq.XAttribute("Var", c.Name)));
                            changed = true;
                        }
                        if (!net.Elements(ns + "Input")
                                .Any(i => (string?)i.Attribute("Name") == c.Name))
                        {
                            var pin = new System.Xml.Linq.XElement(ns + "Input",
                                new System.Xml.Linq.XAttribute("Name", c.Name),
                                new System.Xml.Linq.XAttribute("x", c.X),
                                new System.Xml.Linq.XAttribute("y", c.Y),
                                new System.Xml.Linq.XAttribute("Type", "Data"));
                            var lastInput = net.Elements(ns + "Input").LastOrDefault();
                            if (lastInput != null) lastInput.AddAfterSelf(pin);
                            else net.Add(pin);
                            changed = true;
                        }
                        if (dataConns != null &&
                            !dataConns.Elements(ns + "Connection")
                                .Any(x => (string?)x.Attribute("Source") == c.Name))
                        {
                            dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                                new System.Xml.Linq.XAttribute("Source", c.Name),
                                new System.Xml.Linq.XAttribute("Destination",
                                    "InterlockManager." + c.Name)));
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "Five_State_Actuator_CAT: interlock constants baked onto InterlockManager " +
                          "(sim interface reduced — TargetWork1State=2, TargetHomeState=4)"
                        : "Five_State_Actuator_CAT: interlock constants restored as wired inputs (hardware interface)");
                    MapperLogger.Info(
                        $"[Deploy] Five_State_Actuator_CAT interlock-constant normalize: reduce={reduce}");
                }
                else
                {
                    result.PatchesApplied.Add(
                        $"Five_State_Actuator_CAT: interlock interface already {(reduce ? "reduced" : "wired")} (no change)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"Five_State_Actuator_CAT interlock-constant normalize failed: {ex.Message}");
            }
        }

        // Embedded MqttStateFormatter basic FB (v1: bare INT state → STRING).
        // v1.1 extends the Fmt algorithm to JSON {actuator,state,ts}.
        const string MqttStateFormatterFbt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE FBType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<FBType GUID=\"f0a1b2c3-d4e5-4f60-8a1b-2c3d4e5f6071\" Name=\"MqttStateFormatter\" Comment=\"Basic FB - INT state to STRING payload for MQTT publish\" Namespace=\"Main\">\r\n" +
            "  <Identification Standard=\"61499-2\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"5/22/2026\" Remarks=\"v1 bare state-to-string\" />\r\n" +
            "  <InterfaceList>\r\n" +
            "    <EventInputs>\r\n" +
            "      <Event Name=\"INIT\" Comment=\"Initialization Request\"><With Var=\"state\" /></Event>\r\n" +
            "      <Event Name=\"REQ\" Comment=\"Format on state change\"><With Var=\"state\" /></Event>\r\n" +
            "    </EventInputs>\r\n" +
            "    <EventOutputs>\r\n" +
            "      <Event Name=\"INITO\" Comment=\"Initialization Confirm\"><With Var=\"payload\" /></Event>\r\n" +
            "      <Event Name=\"CNF\" Comment=\"Payload ready\"><With Var=\"payload\" /></Event>\r\n" +
            "    </EventOutputs>\r\n" +
            "    <InputVars>\r\n" +
            "      <VarDeclaration Name=\"state\" Type=\"INT\" Comment=\"current_state_to_process\" />\r\n" +
            "    </InputVars>\r\n" +
            "    <OutputVars>\r\n" +
            "      <VarDeclaration Name=\"payload\" Type=\"STRING[255]\" Comment=\"MQTT payload (state as text). Sized [255] so EAE does not flag WRN_UNSIZED_STRING (the yellow pin marker) inside every actuator/sensor CAT.\" />\r\n" +
            "    </OutputVars>\r\n" +
            "  </InterfaceList>\r\n" +
            "  <BasicFB>\r\n" +
            "    <Attribute Name=\"FBType.Basic.Algorithm.Order\" Value=\"INIT,Fmt\" />\r\n" +
            "    <ECC>\r\n" +
            "      <ECState Name=\"START\" Comment=\"Initial State\" x=\"300\" y=\"300\" />\r\n" +
            "      <ECState Name=\"INIT\" x=\"700\" y=\"120\"><ECAction Algorithm=\"Fmt\" Output=\"INITO\" /></ECState>\r\n" +
            "      <ECState Name=\"Format\" x=\"700\" y=\"520\"><ECAction Algorithm=\"Fmt\" Output=\"CNF\" /></ECState>\r\n" +
            "      <ECTransition Source=\"START\" Destination=\"INIT\" Condition=\"INIT\" x=\"450\" y=\"200\" />\r\n" +
            "      <ECTransition Source=\"INIT\" Destination=\"START\" Condition=\"1\" x=\"500\" y=\"300\" />\r\n" +
            "      <ECTransition Source=\"START\" Destination=\"Format\" Condition=\"REQ\" x=\"450\" y=\"420\" />\r\n" +
            "      <ECTransition Source=\"Format\" Destination=\"START\" Condition=\"1\" x=\"500\" y=\"520\" />\r\n" +
            "    </ECC>\r\n" +
            "    <Algorithm Name=\"INIT\" Comment=\"Initialization algorithm\"><ST><![CDATA[;]]></ST></Algorithm>\r\n" +
            "    <Algorithm Name=\"Fmt\" Comment=\"INT state to JSON payload\"><ST><![CDATA[payload := CONCAT(CONCAT('{state:', INT_TO_STRING(state)), '}');]]></ST></Algorithm>\r\n" +
            "  </BasicFB>\r\n" +
            "</FBType>\r\n";

        // Force the FiveStateActuator basic FB's "mode" InputVar InitialValue=1 so every instance powers up
        // in auto mode. Without it mode=0 at boot and no mode_event fires, so the ECC is stuck in
        // AtHomeInit forever (rig-confirmed) — the same Mode=0 bug as the engine's Mode/CycleType.
        // Idempotent deploy-time guard.
        static void PatchActuatorModeInitialValue(string eaeProjectDir, string fbtFileName, DeployResult result)
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

                // The "mode" VarDeclaration inside <InputVars>.
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
                if (iv == "1") return; // already correct — idempotent no-op
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

        // Relax the swivel core's work-arrival latches so a brief overlap of the two work sensors no
        // longer blocks the latch: ToWork1->AtWork1 / ToWork2->AtWork2 fire on atWorkN=TRUE alone
        // instead of "atWorkN=TRUE AND atWorkOther=FALSE". relax=true on the rig, strict on the
        // simulator. Idempotent.
        static void PatchSwivelRelaxWorkLatch(string eaeProjectDir, bool relax, DeployResult result)
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

                // (Source, Destination, relaxed condition, original strict condition)
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

        static void PatchSwivelInterlockEventCarriesStateVal(string eaeProjectDir, bool add, DeployResult result)
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


        // The ring relay updateComponentState.REQ (a component reporting its OWN state) sets
        // src_id/source_name/state but never clears dest_name. Component_State_Msg is a reused struct, so a
        // report with a stale dest_name spuriously satisfies a target actuator's BREQ match (dest_name==name)
        // and clobbers its state_cmd. Fix: REQ clears component_state_out.dest_name. Idempotent.
        static void PatchRingReportClearDest(string eaeProjectDir, DeployResult result)
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
                // Idempotent: REQ only references dest_name once this patch is applied.
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

        // The ring relay always passes messages forward with BCNF, but must only fire CNF into the actuator
        // core when the message is addressed to this actuator — else any unrelated report replays the last
        // retained state_cmd through ActuatorCore.pst_event.
        static void PatchRingCommandCnfOnlyOnDestination(string eaeProjectDir, DeployResult result)
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

        // Adds (rig) or strips (sim) two sensor-recovery ECTransitions on the Centre-Home swivel core so
        // a swivel that powered up before its IO went live (frozen in AtHomeInit while physically at a
        // work position) re-syncs to AtWork1/AtWork2 and can then accept the Home command. Identified by
        // Source=AtHomeInit AND Destination in {AtWork1,AtWork2}; the stock AtHomeInit arcs go to
        // ToWork1/ToWork2, so this never collides with them.
        //
        // Gated SwivelBrakeHome: reshapes the deployed centre-home ECC + composite into a timed
        // reverse-coil brake at centre so the swivel can home directly from AtWork1 (Disassembly) without
        // coasting into the ejector. Directional: at AtHome the algorithm reverses the driving coil only
        // when homing from AtWork1 (away from the ejector); homing from AtWork2 (Assembly) de-energises
        // unchanged. A brakeTimer E_DELAY (DT = bearingPnpHomeBrakeMs) holds the reverse pulse, then
        // AtHomeInit de-energises. No-op when disabled; the ECC/CAT are force-refreshed so a flag flip reverts.
        static void PatchSwivelBrakeHome(string eaeProjectDir, bool enabled, int brakeMs, DeployResult result)
        {
            if (!enabled) return;
            if (brakeMs <= 0) brakeMs = 500;

            // ---- 1. Core ECC: SevenStateCentreHomeActuator.fbt ----
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

                // a. 'atHome' -> directional brake (reverse the driving coil only when homing from AtWork1).
                var atHomeAlgo = root.Descendants(ns + "Algorithm").FirstOrDefault(a => (string?)a.Attribute("Name") == "atHome");
                if (atHomeAlgo == null) { result.Warnings.Add("Swivel brake: 'atHome' algorithm not found; skipped."); return; }
                atHomeAlgo.Element(ns + "ST")?.ReplaceNodes(new XCData(
                    "current_state_to_process := 6;\r\nIF outputToWork2 = TRUE THEN\r\n\toutputToWork1:= TRUE;\r\n\toutputToWork2:= FALSE;\r\nELSE\r\n\toutputToWork1:= FALSE;\r\n\toutputToWork2:= FALSE;\r\nEND_IF;\r\n"));

                // b. 'AtHomeInit' -> de-energise after the brake pulse.
                root.Descendants(ns + "Algorithm").FirstOrDefault(a => (string?)a.Attribute("Name") == "AtHomeInit")
                    ?.Element(ns + "ST")?.ReplaceNodes(new XCData(
                        "current_state_to_process := 0;\r\noutputToWork1:= FALSE;\r\noutputToWork2:= FALSE;\r\n"));

                // c. interface: brake_start (output) + brake_done (input).
                var eos = root.Descendants(ns + "EventOutputs").FirstOrDefault();
                if (eos != null && !eos.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "brake_start"))
                    eos.Add(new XElement(ns + "Event", new XAttribute("Name", "brake_start"),
                        new XAttribute("Comment", "centre-home brake pulse start")));
                var eis = root.Descendants(ns + "EventInputs").FirstOrDefault();
                if (eis != null && !eis.Elements(ns + "Event").Any(e => (string?)e.Attribute("Name") == "brake_done"))
                    eis.Add(new XElement(ns + "Event", new XAttribute("Name", "brake_done"),
                        new XAttribute("Comment", "centre-home brake pulse elapsed")));

                // d. AtHome ECState: also emit brake_start (starts the timer).
                var atHome = root.Descendants(ns + "ECState").FirstOrDefault(s => (string?)s.Attribute("Name") == "AtHome");
                if (atHome != null && !atHome.Elements(ns + "ECAction").Any(a => (string?)a.Attribute("Output") == "brake_start"))
                    atHome.Add(new XElement(ns + "ECAction", new XAttribute("Output", "brake_start")));

                // e. AtHome -> AtHomeInit: brake_done is now a SAFETY CAP only (the sensor arc in
                //    (g) is the primary). Set the non-sensor arc to brake_done (idempotent: never
                //    touches the 'atHome = FALSE' sensor arc).
                root.Descendants(ns + "ECTransition").FirstOrDefault(t =>
                        (string?)t.Attribute("Source") == "AtHome" && (string?)t.Attribute("Destination") == "AtHomeInit"
                        && (string?)t.Attribute("Condition") != "atHome = FALSE")
                    ?.SetAttributeValue("Condition", "brake_done");

                // f. CRITICAL: AtHomeInit must PUBLISH the coil-off (output_event). The stock state
                //    only emits pst_out, so AFTER the brake the reverse coil stays ENERGISED and the
                //    arm is DRIVEN + HELD at AtWork1 — the observed overshoot at ANY brake length.
                //    output_event drives the Output SYMLINKMULTIVARSRC, which writes both coils FALSE.
                var atHomeInit = root.Descendants(ns + "ECState").FirstOrDefault(s => (string?)s.Attribute("Name") == "AtHomeInit");
                if (atHomeInit != null && !atHomeInit.Elements(ns + "ECAction").Any(a => (string?)a.Attribute("Output") == "output_event"))
                    atHomeInit.Add(new XElement(ns + "ECAction", new XAttribute("Output", "output_event")));

                // g. SENSOR-STOPPED de-energise (the real fix): leave AtHome the instant the arm
                //    crosses back OUT of the DI02 centre window (atHome=FALSE) instead of after the
                //    fixed brake_done timer (which over-drove the arm to AtWork1). The Inputs
                //    subscriber pushes input_event on every sensor change — the SAME path the swivel
                //    uses to detect centre at all (the work-timers are inert T#0s) — so the ECC
                //    re-evaluates promptly and cuts the coil NEAR CENTRE. brake_done stays as the cap
                //    (fires only if the arm never leaves the window, e.g. the sensor mechanism fails).
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

            // ---- 2. Composite: brakeTimer E_DELAY + wiring ----
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

        static void PatchSwivelAtHomeInitRecovery(string eaeProjectDir, bool addArc, DeployResult result)
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

                // SELF-HOME ON POWER-UP. The core latches whatever work position it physically booted at
                // (INIT -> AtWork1 / INIT -> ToWork2). The swivel has no spring-centre (both coils off =>
                // it holds position), so the only way to make HOME its initial state is to DRIVE it home
                // at power-up. On the rig (addArc) redirect every "booted at a work position" boot path to
                // ToHome so the arm swings itself home before the engine starts. On the sim (!addArc)
                // restore INIT -> work states and strip the self-home arcs. Idempotent + bidirectional.
                // SAFETY: the arm physically swings home at power-up (toward centre), so the swing path
                // must be clear before a cold download.
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
                    // SIM / restore: INIT boot arcs back to the work states; strip the
                    // self-home arcs (and any older AtWork "re-sync" recovery arcs).
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

                // RIG: drive home on power-up via INIT only; do NOT add an AtHomeInit -> ToHome self-home
                // arc (with noisy DI00/DI01 it re-fires and the arm cycles atWork1<->atWork2, never
                // settling). Once the swivel reaches AtHomeInit it must stay there until the recipe
                // commands Pick/Place, so AtHomeInit must have no self-driving exit: redirect INIT to
                // ToHome and strip every AtHomeInit -> {ToHome, AtWork1, AtWork2} arc added here. The
                // stock AtHomeInit -> ToWork1/ToWork2 (Pick/Place) arcs stay intact.
                bool changed = false;

                // 1. INIT -> AtWork1 / INIT -> ToWork2  ==>  INIT -> ToHome (boot self-home).
                foreach (var t in initArcs)
                {
                    var dest = (string?)t.Attribute("Destination");
                    if (dest == "AtWork1" || dest == "ToWork2")
                    { t.SetAttributeValue("Destination", "ToHome"); changed = true; }
                }

                // 2. Strip every AtHomeInit -> {ToHome, AtWork1, AtWork2} arc we added
                //    (self-home AND the older re-sync-to-work). These re-fire on noisy
                //    sensors and cycle the swivel; AtHomeInit must be a stable rest state.
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

        // Wires the AtHome ECState to the coil-clearing 'atHome' algorithm and makes AtHome publish
        // output_event, so the Output SYMLINKMULTIVARSRC writes both work coils FALSE. Both algorithms
        // already exist in the core; this only swaps which one the AtHome state runs.
        static void PatchSwivelAtHomeCoilClear(string eaeProjectDir, bool clearCoils, DeployResult result)
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

                // Both candidate algorithms must exist before we swap.
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

                if (!changed) return; // idempotent
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

        // Gated MapperConfig.SwivelHomeHoldBothCoils (default OFF). Rewrites the 'atHome' algorithm's two
        // coil outputs: OFF de-energises both at centre (a venting swivel coasts past DI02, rests off-centre);
        // TRUE holds both coils so a cylinder with a mechanical mid-stop is driven into + held at centre.
        // Bidirectional + idempotent (only flips the coil VALUES; PatchSwivelAtHomeCoilClear runs first).
        // SAFETY: with NO mid-stop, both-on drives toward an extreme — enable only on the rig with the
        // e-stop ready, and abort if the arm heads toward Work2.
        static void PatchSwivelAtHomeBothCoils(string eaeProjectDir, bool holdBothCoils, DeployResult result)
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
                if (newBody == body) return; // idempotent (incl. the byte-identical flag-OFF default)

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

        static void PatchKnownArraySizeBugs(string eaeProjectDir, DeployResult result)
        {
            var fbtPath = Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            if (!File.Exists(fbtPath)) return;

            var text = File.ReadAllText(fbtPath);
            const string oldDecl =
                "<VarDeclaration Name=\"state_table\" Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"1\" />";
            const string newDecl =
                "<VarDeclaration Name=\"state_table\" Type=\"Component_State\" Namespace=\"Main\" ArraySize=\"20\" />";

            if (text.Contains(newDecl)) return;
            if (!text.Contains(oldDecl))
            {
                result.Warnings.Add(
                    "ProcessRuntime_Generic_v1.fbt: state_table declaration not found in expected " +
                    "shape (ArraySize=\"1\"). Skipping ArraySize patch — verify by hand.");
                return;
            }
            File.WriteAllText(fbtPath, text.Replace(oldDecl, newDecl));
            result.PatchesApplied.Add("ProcessRuntime_Generic_v1.state_table ArraySize 1 -> 20");
            MapperLogger.Info("[Deploy] Patched ProcessRuntime_Generic_v1.state_table ArraySize 1 -> 20");

            // Schneider template-side typo in check_wait: the right-hand-side reads
            // Wait1Id where it should read Wait1State. As shipped, no recipe of any
            // shape can ever satisfy a wait — the comparison checks state equals the
            // component's registry id, never the desired wait state. Patch to the
            // semantically correct comparison every deploy.
            const string brokenCheckWait =
                "WaitSatisfied := state_table[Wait1Id[CurrentStep]].state = Wait1Id[CurrentStep];";
            const string fixedCheckWait =
                "WaitSatisfied := state_table[Wait1Id[CurrentStep]].state = Wait1State[CurrentStep];";
            text = File.ReadAllText(fbtPath);
            if (text.Contains(brokenCheckWait))
            {
                File.WriteAllText(fbtPath, text.Replace(brokenCheckWait, fixedCheckWait));
                result.PatchesApplied.Add("ProcessRuntime_Generic_v1.check_wait typo Wait1Id -> Wait1State");
                MapperLogger.Info("[Deploy] Patched ProcessRuntime_Generic_v1.check_wait typo (Wait1Id -> Wait1State on RHS)");
            }
        }

        // ArraySize matches ProcessRecipeArrayGenerator.RecipeArraySize (100) so the
        // create-default agrees with what PatchProcess1RecipeArraySize / Normalize*
        // force onto every recipe-array InputVar — no 64->100 transient. Station-2
        // recipes (Assembly_Station 54 rows, Disassembly 43) need >64; the generator
        // refuses anything over RecipeArraySize, so declared and emitted stay in lock-step.
        static readonly (string Name, string Type, string ArraySize, string? Comment)[] RecipeArrayDecls = new[]
        {
            ("StepType",      "INT",         "100", "Phase 1: recipe arrays now external. 1=command, 2=wait, 9=end. Mapper writes literal at instance level via Process1_Generic."),
            ("CmdTargetName", "STRING[15]",  "100", (string?)null),
            ("CmdStateArr",   "INT",         "100", (string?)null),
            ("Wait1Id",       "INT",         "100", (string?)null),
            ("Wait1State",    "INT",         "100", (string?)null),
            ("NextStep",      "INT",         "100", (string?)null),
        };

        static void PatchProcessRuntimeCompatibility(string eaeProjectDir, DeployResult result)
        {
            var enginePath = Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            if (!File.Exists(enginePath))
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt not deployed; runtime compatibility patch skipped.");
                return;
            }

            try
            {
                PatchProcessRuntimeEccDeadEnd(enginePath, MapperConfig.EnableCyclicRestart, result);
                PatchProcessRuntimeStartBypass(enginePath, result);
                PatchProcessRuntimeEndSequenceNoOp(enginePath, result);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ProcessRuntime_Generic_v1 compatibility patch failed: {ex.Message}");
            }
        }


        static void VerifyArraySizeConsistency(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var iec = Path.Combine(eaeProjectDir, "IEC61499");
                if (!Directory.Exists(iec)) return;

                var sizes = new Dictionary<(string, string), string>(
                    EqualityComparer<(string, string)>.Default);

                foreach (var fbt in Directory.EnumerateFiles(iec, "*.fbt", SearchOption.AllDirectories))
                {
                    System.Xml.Linq.XDocument doc;
                    try { doc = System.Xml.Linq.XDocument.Load(fbt); }
                    catch { continue; }
                    var fbType = Path.GetFileNameWithoutExtension(fbt);
                    foreach (var vd in doc.Descendants().Where(e => e.Name.LocalName == "VarDeclaration"))
                    {
                        var name = (string?)vd.Attribute("Name");
                        var arr = (string?)vd.Attribute("ArraySize");
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(arr)) continue;
                        sizes[(fbType, name)] = arr;
                    }
                }

                foreach (var fbt in Directory.EnumerateFiles(iec, "*.fbt", SearchOption.AllDirectories))
                {
                    System.Xml.Linq.XDocument doc;
                    try { doc = System.Xml.Linq.XDocument.Load(fbt); }
                    catch { continue; }
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

        public static DeployResult Deploy(MapperConfig cfg, List<VueOneComponent> components)
        {
            var result = new DeployResult();
            var libPath = cfg.TemplateLibraryPath;

            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var eaeProjectDir = DeriveEaeProjectDir(cfg);
            if (string.IsNullOrWhiteSpace(eaeProjectDir))
                throw new InvalidOperationException(
                    "Cannot determine EAE project directory from syslay path.");

            var neededCats = ResolveNeededCats(components);
            var neededBasics = ResolveNeededBasics(neededCats);

            foreach (var basic in neededBasics)
            {
                var zipPath = FindPackage(libPath, "Basic", basic, ".Basic");
                if (zipPath == null)
                {
                    result.Warnings.Add($"Basic package not found: {basic}");
                    continue;
                }
                ExtractToEae(zipPath, eaeProjectDir, result);
                result.BasicFBsDeployed.Add(basic);
                MapperLogger.Info($"[Deploy] Basic: {basic}");
            }

            foreach (var cat in neededCats)
            {
                var zipPath = FindPackage(libPath, "CAT", cat, ".cat");
                if (zipPath == null)
                {
                    result.Warnings.Add($"CAT package not found: {cat}");
                    continue;
                }
                ExtractToEae(zipPath, eaeProjectDir, result);
                result.CATsDeployed.Add(cat);
                MapperLogger.Info($"[Deploy] CAT: {cat}");
            }

            GenerateCfgFiles(eaeProjectDir, result);
            RegisterInDfbproj(eaeProjectDir, result);

            result.Success = true;
            return result;
        }

        static HashSet<string> ResolveNeededCats(List<VueOneComponent> components)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in components)
            {
                var key = $"{c.Type}_{c.States.Count}";
                if (ComponentTypeToCat.TryGetValue(key, out var cat))
                    cats.Add(cat);

                if (string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase) &&
                    ComponentTypeToCat.TryGetValue("Process_Any", out var procCat))
                    cats.Add(procCat);
            }
            return cats;
        }

        static HashSet<string> ResolveNeededBasics(HashSet<string> cats)
        {
            var basics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in cats)
            {
                if (CatDependencies.TryGetValue(cat, out var deps))
                    foreach (var dep in deps)
                        basics.Add(dep);
            }
            return basics;
        }

        static string? FindPackage(string libPath, string subfolder, string name, string extension)
        {
            var dir = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(dir)) return null;

            foreach (var file in Directory.GetFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                    fileName.Contains(extension, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
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
