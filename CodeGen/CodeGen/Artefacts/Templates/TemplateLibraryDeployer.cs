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
using static CodeGen.Services.ActuatorCatTemplatePatcher;

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
            NormalizeFiveStateInterlockConstants(eaeProjectDir, result);
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
            NormalizeSwivelSimSensorSource(eaeProjectDir, result);
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
            NormalizeFiveStateSimSensorSource(eaeProjectDir, result);
            NormalizeFiveStateFaultEnables(eaeProjectDir, result);
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
