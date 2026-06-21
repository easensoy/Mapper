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
            // Seven_State_Actuator_CAT re-added per user request 2026-05-21
            // for Bearing_PnP routing (Mapper Validator was failing the
            // PARALLEL+ALTERNATIVE-branched 13-state actuator with "No template
            // found"). SevenStateActuator2 is its internal "InterlockManager"
            // sub-FB analogue (same role as CommonInterlockEvaluator for
            // Five_State). The previous removal was driven by data-driven
            // patch failures; with Bearing_PnP routed to the verbatim CAT
            // (no runtime parameter graft) those failures no longer apply.
            { "Seven_State_Actuator_CAT", new[] { "SevenStateActuator", "SevenStateActuator2" } },
            // Jyotsna's new centre-home swivel (2026-06-02). Basic leaf FBs:
            // SevenStateCentreHomeActuator (core ECC), No_Sensor_Handler_7SCH
            // (synthesises atHome on the work->home timer), FaultLatch_7SCH
            // (leaf inside the faultDetection_7SCH composite). The composite
            // faultDetection_7SCH itself ships via UniversalComposites; the
            // shared CommonInterlockEvaluator + updateComponentState are already
            // in UniversalBasics.
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
            // Jyotsna's centre-home swivel CAT (2026-06-02) — Bearing_PnP now
            // instantiates this instead of the old Seven CAT (TemplateMap).
            // Deployed always so EAE can resolve the type on import.
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
            // Embedded by Jyotsna's new Five_State_Actuator_CAT as its
            // "InterlockManager" sub-FB (Type=Main:CommonInterlockEvaluator,
            // a true Basic FB). Must be deployed or EAE fails to open the
            // project: "type or namespace 'Main:CommonInterlockEvaluator'
            // does not exist".
            "CommonInterlockEvaluator",
            // Event-change handlers referenced by PLC_RW_M262's internal FB2/FB3
            // instances. Sourced from C:\SMC_Rig_Expo_20260112-165857725.sln\IEC61499.
            "changeEventProcess1", "changeEventProcess2",
            // SevenStateActuator + SevenStateActuator2 — Basic FBs embedded by
            // Seven_State_Actuator_CAT. Both must be deployed when the CAT is in
            // scope or EAE fails with "type or namespace SevenStateActuator2
            // does not exist". Restored 2026-05-21 alongside the CAT for
            // Bearing_PnP routing.
            "SevenStateActuator", "SevenStateActuator2",
            // Centre-home swivel leaf Basics (2026-06-02). SevenStateCentreHomeActuator
            // is the core ECC; No_Sensor_Handler_7SCH synthesises atHome on the
            // work->home timer; FaultLatch_7SCH + actuatorStateEvents_7SCH are the
            // leaves inside faultDetection_7SCH. actuatorStateEvents_7SCH was MISSING
            // from Jyotsna's delivery (faultDetection_7SCH instantiates it as
            // 'ActuatorEvents' but no .fbt shipped -> EAE: "type 'Main:actuatorStateEvents_7SCH'
            // does not exist"); reconstructed from the non-7SCH actuatorStateEvents
            // as the two-work variant (state 1 -> toWork1_Event, state 3 -> toWork2_Event).
            // SimCentreHomeSensor_7SCH is simulator-only wiring inserted into the CAT by
            // NormalizeSwivelSimSensorSource; it derives mutually-exclusive atHome/atWork
            // signals from the core's current_state_to_process so the sim never presents
            // impossible sensor combinations like atHome=TRUE and atWork1=TRUE.
            "SevenStateCentreHomeActuator", "No_Sensor_Handler_7SCH", "FaultLatch_7SCH",
            "actuatorStateEvents_7SCH",
            // SimCentreHomeSensor_7SCH REMOVED (simulator-only; we no longer ship simulator code).
            // The rig path never instantiated it (NormalizeSwivelSimSensorSource reduce=false strips it);
            // it is no longer deployed and any stale copy is swept in DeployUniversalArchitecture.
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

            // HANDLER REFRESH (2026-06-16): the centre-home swivel's No_Sensor_Handler_7SCH was
            // changed to a SENSOR-DRIVEN home — its START->AtHome transition now gates on the
            // physical atHome sensor ('inputEvent AND atHomeInput = TRUE') instead of the open-loop
            // work1/work2ToHomeTimer that overshot centre (the swivel-never-homes bug). DeployArtifact
            // below is COPY-IF-ABSENT, so a previously-deployed (timer) handler would never be
            // refreshed from the Template Library zip. Delete the deployed handler files first so the
            // copy-if-absent re-extracts the fixed one. Pipeline-respecting (the deployer does it, no
            // direct Demonstrator edit), idempotent, and guarantees the deployed handler always
            // matches the committed zip. EAE recompiles the one small Basic FB on the next Build.
            foreach (var ext in new[] { ".fbt", ".doc.xml", ".meta.xml" })
            {
                var stale = Path.Combine(eaeProjectDir, "IEC61499", "No_Sensor_Handler_7SCH" + ext);
                try { if (File.Exists(stale)) File.Delete(stale); }
                catch (Exception ex)
                { MapperLogger.Info($"[Deploy][Refresh] could not remove stale {stale}: {ex.Message}"); }
            }

            // CAT REFRESH (2026-06-16): both ring CATs now CYCLICALLY re-read their DIs every 200ms
            // via an E_DELAY self-loop — Sensor_Bool_CAT (Poll.EO -> FB2.REQ) and Five_State_Actuator_CAT
            // (Poll.EO -> Inputs.REQ; re-process via the pre-existing Inputs.CNF -> InputHandler.inputEvent).
            // The original CATs read their DI EXACTLY ONCE at INIT, which was the universal stall: the
            // engine's check_wait is edge-triggered and at step 0 no ring token circulates, so the
            // one-shot publish raced the engine INIT and was lost (sensors) and sensor-fitted actuators
            // (Feeder/Transfer/grippers/shafts/clamp = WorkSensorFitted TRUE) never detected reaching
            // atWork/atHome after a CMD. DeployArtifact is COPY-IF-ABSENT, so delete the deployed CAT
            // folders first to force re-extract of the fixed zips. Pipeline-respecting (no direct
            // Demonstrator edit); PatchCatMqttPublish re-applies embedded MQTT after; EAE recompiles on Build.
            foreach (var catRefresh in new[] { "Sensor_Bool_CAT", "Five_State_Actuator_CAT" })
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

            // STAGE 5b foundation (gated, MapperConfig.EnableRobotTaskTail): make the UR3e robot
            // type resolvable. Robot_Task_CAT is the task-handshake CAT (StartTask DO04 / Task_Complete
            // DI10); its core state machine is the Robot_Task_Core Basic FB — deploy the core first so
            // the composite resolves on import. Default-off deploys NEITHER → byte-identical.
            // ROBOT_TASK_CAT ALWAYS DEPLOYED (2026-06-17): the UR3e robot is a real Control.xml
            // component, so its CAT type + Robot_Task_Core core must ALWAYS be present. The old
            // EnableRobotTaskTail gate conflated THREE separate concerns — type DEPLOY, robot
            // INSTANCE emission, and cross-PLC RING membership — so turning the tail OFF (to decouple
            // the M580 ring) ALSO deleted the deployed CAT while the dfbproj still referenced it ->
            // EAE Solution Integrity "Missing Project Files" (Robot_Task_CAT.cfg/.fbt/...). Deploying
            // the type unconditionally fixes that. The robot INSTANCE + its M262-local sub-sequence +
            // the cross-PLC handoff are gated/wired SEPARATELY (the conflation is split out in the
            // HandoffPlanner / RingWiringGenerator refactor). An unused-but-defined CAT type is
            // harmless; EAE compiles the HMI faceplate against it. (DemonstratorWiper still deletes the
            // folder on Clean; this re-creates it on the next deploy — consistent.)
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
            }

            DeployDataTypes(libPath, eaeProjectDir, result);
            PatchKnownArraySizeBugs(eaeProjectDir, result);
            PatchProcessRuntimeCompatibility(eaeProjectDir, result);
            PatchSensorBoolCatDstQi(eaeProjectDir, result);
            PatchCatSymlinkQi(eaeProjectDir, "Five_State_Actuator_CAT", result);
            // 2026-06-02: the new centre-home swivel CAT has the SAME symlink FBs
            // (Inputs SYMLINKMULTIVARDST + Output SYMLINKMULTIVARSRC) but ships with
            // QI unset -> defaults FALSE -> the FBs are DISABLED. On the rig that
            // islands the core from its IO: the coil commands never reach the DO
            // channels (all 1970/never-written) and the physical atwork/athome
            // sensors never reach the core (reads FALSE while the channel is TRUE).
            // The symbolic links themselves resolve fine ($${PATH} -> RES0.Bearing_PnP.*
            // bound to DO17/DI00 etc.) — it's purely the QI gate. Five_State got this
            // patch long ago (above); the new CAT was missed. Masked in the simulator
            // (no-sensor timers advance state, coils unused), fatal on the rig.
            PatchCatSymlinkQi(eaeProjectDir, "Seven_State_Actuator_Centre_Home_CAT", result);
            // Remove the dead 'state_out' boundary event (it fed the removed cross-resource MQTT
            // bridge; nothing consumes it). Visual/interface cleanup only — no control-logic change.
            StripSevenStateStateOut(eaeProjectDir, result);
            // VISUAL-only: keep each CAT's HMI/OPCUA section frame from spilling into the section
            // below (pull IThis inside, fix the frame). Self-skips CATs without an HMI/OPCUA frame.
            foreach (var hmiCat in new[] { "Five_State_Actuator_CAT", "Seven_State_Actuator_Centre_Home_CAT",
                                           "Sensor_Bool_CAT", "Robot_Task_CAT" })
                FixCatHmiOpcuaFrame(eaeProjectDir, hmiCat, result);
            PatchActuatorModeInitialValue(eaeProjectDir, "FiveStateActuator.fbt", result);
            // 2026-06-02: the centre-home swivel core needs the SAME auto-mode default.
            // Without it SevenStateCentreHomeActuator powers up at mode=0, its ECC sits
            // in AtHomeInit, and it ignores every recipe command — which stalls the whole
            // Assembly recipe on the first Bearing_PnP step (so even Bearing_Gripper,
            // which is downstream in the recipe, never gets commanded). Symptom on the
            // rig: all coil outputs read 1970/never-written while inputs read fine.
            PatchActuatorModeInitialValue(eaeProjectDir, "SevenStateCentreHomeActuator.fbt", result);
            // 2026-06-03: Rig swivel "boots at AtHomeInit, ignores the Home command,
            // never reaches atHome" fix. The Centre-Home core's ECC has INIT->AtWork1
            // (fires only if atWork1=TRUE at the *instant* INIT runs) but AtHomeInit has
            // NO sensor-recovery arc and NO Home exit -- its only exits are Pick
            // (state_val=1 -> ToWork1) and Place (state_val=3 -> ToWork2). On the rig the
            // swivel's DI is frequently not live the moment the core runs INIT, so atWork1
            // reads FALSE -> INIT->AtHomeInit. A scan later the IO comes alive and
            // atWork1=TRUE (swivel is physically parked at Pick) but nothing re-evaluates:
            // the core is frozen in AtHomeInit, and the engine's home-preamble
            // CMD bearing_pnp=5 lands on a state with no matching transition -> dead, so
            // the whole Assembly recipe stalls on step 1 (Feed_Station, all Five_State,
            // runs fine -- this is Seven_State-specific). Add a sensor-recovery arc that
            // mirrors INIT->AtWork1 but from AtHomeInit: if a work sensor says we're
            // actually at a work position, correct the logical state to match reality.
            // Pure logic correction -- the AtWork1/AtWork2 entry algo re-energises the
            // coil the swivel is already sitting on (NO motion) -- then the stock
            // AtWork1->ToHome / AtWork2->ToHome path carries the Home command through.
            // Real sensors only (atWork1/atWork2/atHome are the physical symlinks on the
            // rig; no SIM, no SimHopperForce). Always added on the rig (addArc: true);
            // the bidirectional patch is no longer called with addArc=false.
            PatchSwivelAtHomeInitRecovery(eaeProjectDir, addArc: true, result);
            // 2026-06-03: Centre-Home swivel home must clear both work coils. The
            // shipped AtHome state ran 'AtHomeEnd' (current_state:=6 only), so the
            // work coil used to swing through centre stayed energized at "home".
            // With the old simulator coil-mirror this was hidden by accepting the
            // transient 6. The simulator now has a proper state-derived position
            // model, so both simulator and hardware can use Jyotsna's coil-clearing
            // 'atHome' algorithm and publish output_event at AtHome.
            PatchSwivelAtHomeCoilClear(eaeProjectDir, clearCoils: true, result);
            // 2026-06-04 (Alex fix): the Centre-Home core's work-arrival latches required
            // the two physical work sensors to be perfectly mutually exclusive
            // (ToWork1->AtWork1 needed atWork1=TRUE AND atWork2=FALSE, ToWork2->AtWork2 the
            // mirror), so a brief transit overlap of DI00/DI01 blocked the AtWork latch --
            // which gates the gripper-release. Relax them to fire on atWorkN=TRUE alone.
            // Bidirectional + gated: relaxed on the rig, strict on the simulator.
            //
            // NB: atHome is INTENTIONALLY driven by the real DI02 on the rig (wired below
            // by NormalizeSwivelSimSensorSource), NOT the ReturnToHomeHandler timer output.
            // The timer can fire before the arm reaches centre and stop Home short or
            // overshoot; with the physical centre sensor working it is the correct,
            // position-accurate source. An earlier atHome-timer rewire was removed -- it
            // fought that deliberate wiring and was reverted by the normalizer anyway.
            PatchSwivelRelaxWorkLatch(eaeProjectDir, relax: true, result);
            // Seven centre-home command edge. The Place command reaches StateHandling and
            // immediately fans an interlock check into ActuatorCore.ilck_event before the
            // normal pst_event command edge is guaranteed to be visible in Watch/runtime.
            // Sample state_val on ilck_event too so AtWork1 -> ToWork2 sees the same
            // command value (3) as the interlock result. Seven-only; sim strips it back.
            PatchSwivelInterlockEventCarriesStateVal(eaeProjectDir, add: true, result);
            // 2026-06-08 (Alex/Jyotsna leftover-data fix): make the ProcessRuntime engine's
            // WAIT edge-triggered so a STALE state_table slot can no longer satisfy a WAIT.
            // The ring (updateComponentState) never clears state_table -- each slot holds the
            // last value an actuator ever reported -- and the engine's 'armed' guard only
            // blocks the FIRST check after a command; every later state_change (from ANY
            // actuator on the ring) re-tests state_table[Wait1Id].state == Wait1State against
            // that persistent table. So a slot left at the wait target by a prior cycle is
            // satisfied by the next unrelated report -> the engine races past the gripper
            // release / gripper-home waits and commands bearing_pnp=5 (Home) the instant the
            // swivel reaches AtWork2 -> "atWork2 then immediately returns", bearing never
            // released. The patch arms a 'leftoverSuspect' flag when the slot ALREADY equals
            // the target at arm time and only satisfies the WAIT on a genuine fresh transition
            // INTO the target (actuators always pass through an intermediate state, so a
            // stale-then-moved slot leaves the target -- clearing suspect -- and is detected
            // on return). Engine-only, one BOOL InternalVar, no ring/wiring change; applies to
            // every ProcessRuntime instance (Feed/Assembly/Disassembly). Idempotent.
            PatchEngineWaitFreshReport(eaeProjectDir, result);
            // 2026-06-08 (ROOT CAUSE, confirmed by EAE watch): the shared ring relay
            // updateComponentState.REQ (a component reporting its OWN state) sets
            // src_id/source_name/state but NEVER clears dest_name. Component_State_Msg is
            // a reused struct, so once a command (dest_name='bearing_pnp') has been
            // relayed through a component, that component's very next state REPORT inherits
            // the stale dest_name. The report (e.g. source_name='BearingSensor', state=0)
            // then satisfies the target actuator's BREQ match dest_name==name and
            // overwrites its state_cmd with the REPORTING component's state. On the rig
            // this clobbers Bearing_PnP's Place command (state_cmd:=3) back to 0 on the
            // next BearingSensor report -> the swivel never leaves AtWork1, never reaches
            // AtWork2, never reports 4 -> the gripper-release step is never reached. Fix:
            // REQ clears dest_name so a report carries NO command target and cannot
            // spuriously re-command anyone.
            PatchRingReportClearDest(eaeProjectDir, result);
            // 2026-06-10: updateComponentState originally emitted CNF on every BREQ
            // pass-through, even when component_state_in.dest_name did NOT match this
            // actuator. Since StateHandling.CNF is wired to ActuatorCore.pst_event, an
            // unrelated ring report could re-fire the actuator with its last retained
            // command value. Live symptom: Shaft_Gripper finished a cycle with
            // state_cmd/state_val=3 (release), then on the next cycle it gripped and an
            // unrelated report immediately replayed that stale release at AtWork. Keep
            // BCNF on every pass-through so the ring still advances and state_table still
            // updates, but emit CNF only on an actual command for this actuator.
            PatchRingCommandCnfOnlyOnDestination(eaeProjectDir, result);
            // Interlock-constants normalizer, called with reduce=false (rig): it RESTORES
            // the wired boundary inputs the embedded InterlockManager FB declares. MUST run
            // every deploy because ExtractToEae/CopyDirToEae are copy-if-absent, so the
            // deployed CAT persists across runs. (The sim reduce=true direction that baked
            // the constants + dropped the inputs 17→15 is no longer used.)
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
                // PatchCatExposeState call REMOVED 2026-06-01 with the bridge.
                // It only existed to expose state_out at the Five_State CAT
                // boundary for the cross-resource bridge wires; with the bridge
                // gone there's no consumer, so we don't modify the CAT boundary.
                // The embedded MqttPub (above) taps ActuatorCore.pst_out
                // INTERNALLY and needs no boundary exposure. (Method retained
                // in this file but no longer called.)
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

            // Telemetry: wrap each resource-level MQTT_CONNECTION in the Telemetry_CAT composite
            // (Config:TelemetryConfig in / Health:TelemetryHealth out). Deploy the two structs + the
            // composite TYPE together so EAE resolves them; the syslay/sysres then carry Telemetry_*
            // instances (SystemLayoutInjector.InjectMqttConn, same gate). false leaves them undeployed
            // and emits the raw MQTT_CONNECTION instead — the one-rebuild revert if EAE rejects the
            // wrapper's member-level struct connections (Config.QI->Conn.QI).
            if (cfg.UseTelemetryCat)
            {
                DeployTelemetryConfigDatatype(eaeProjectDir, result);
                DeployTelemetryHealthDatatype(eaeProjectDir, result);
                DeployArtifact(libPath, "Composite", "Telemetry_CAT", eaeProjectDir, result, isBasic: false);
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
            // Home-sensor poll REMOVED (2026-06-19, user). The HomePoll/poll-gate CAT machinery
            // (HomePoll + PollGate1/2 + the earlier PollWindow) is gone — Bearing_PnP's home is
            // RECIPE-ONLY now (Assembly/Disassembly command bearing_pnp Home at the end). This call
            // only STRIPS any previously-injected poll FBs out of the deployed CAT so a re-deploy
            // cleans the live tree; it adds nothing. RUNTIME RISK (reported, not worked around): the
            // 'Inputs' symlink is sample-on-REQ and HomePoll was its only REQ driver, so without it the
            // core may not re-observe atHome/atWork during a move — a CAT/interface gap to fix properly
            // if it manifests on the rig, NOT by re-adding a polling FB. See StripCatHomeSensorPoll.
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
            // Simulator-only debug watchability. CurrentStep / CurrentStepType /
            // WaitSatisfied are "Exposed for debug" OutputVars on ProcessRuntime_Generic_v1
            // but are in NO output event's WITH list (EAE WRN_XML_VAR_WITHOUT_OEVENT), so
            // EAE Online Watch can never refresh them — they sit frozen at their power-up
            // 0/FALSE regardless of which recipe step the engine is really on. That is the
            // "CurrentStep stuck at 0, WaitSatisfied stuck FALSE" confusion: the engine is
            // stepping (cmd_target_name advances on CMDREQ), but the step counter never
            // updates in Watch. Add the three to the CMDREQ + SCNF output-event WITH lists
            // so they refresh on every command + step confirmation. Pure watch aid — the
            // vars are already computed by the ECC; this changes no logic. reduce==false
            // (rig) strips them so the hardware engine is byte-identical.
            NormalizeProcessEngineDebugWatch(eaeProjectDir, false, result);
            // Process-engine WAIT guard — RIG ONLY. On the rig the engine inits last
            // and its state_table boots blank, so a home WAIT races; the guard makes a
            // post-command WAIT wait for that actuator's fresh report. In the SIMULATOR
            // the sim-force FBs keep state_table accurate and the swivel boots home, so
            // the guard is unnecessary AND would stall the home-preamble no-op -> the
            // sim path STRIPS the guard (original entry-check engine). Runs after the
            // recipe-array normalize so it patches the final check_wait.
            PatchProcessRuntimeWaitGuard(eaeProjectDir, apply: true, result);
            // The WAIT guard above still read the target state from the persistent
            // state_table. On repeated moves of the same actuator in one recipe
            // (shaft_vr Work -> Home -> Work), a stale table slot can look correct
            // before the second physical move has actually reported. Wire the
            // ProcessHandler's current ring message into ProcessRuntime and make
            // check_wait satisfy only on a fresh report from the waited component.
            PatchProcessRuntimeFreshWaitTrigger(eaeProjectDir, result);
            PatchProcess1FreshWaitTriggerWiring(eaeProjectDir, result);

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

        /// <summary>
        /// Ensure the internal SYMLINKMULTIVARDST (FB2) inside the deployed
        /// Sensor_Bool_CAT.fbt carries Parameter QI=TRUE. Without QI the DST
        /// defaults QI=FALSE, which disables it as a live subscriber so every
        /// publish to '$${PATH}Input' is silently dropped — the sensor never
        /// registers state on the ring (runtime-confirmed root cause). The
        /// template zip already ships QI=TRUE; this is an idempotent
        /// deploy-time guard so a future zip re-swap that loses QI can't
        /// silently reintroduce the bug. Adds the parameter only when the
        /// SYMLINKMULTIVARDST FB lacks it; no-op once present. Touches only
        /// Sensor_Bool_CAT.fbt — no event/ECC/NAME1 changes.
        /// </summary>
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

        /// <summary>
        /// The recipe-array InputVars (StepType, CmdTargetName, CmdStateArr,
        /// Wait1Id, Wait1State, NextStep) ship at ArraySize="10" in the
        /// Process1_Generic.fbt and ProcessRuntime_Generic_v1.fbt templates,
        /// but Mapper now emits up to 20 entries (Transfer widening +
        /// auto-retract). EAE silently truncates the over-long array literal
        /// so ProcessEngine receives a partial recipe and stalls on
        /// StepType=0 (Unknown step). Force ArraySize="20" on all six in BOTH
        /// deployed FBTs to match the existing state_table ArraySize=20
        /// convention. Idempotent: SetAttributeValue replaces if present,
        /// inserts if absent; no-op once at 20. Runs on hardware AND sim
        /// paths. (PatchProcessRuntimeEngine's RecipeArrayDecls path is gated
        /// off because the new template already ships the arrays as InputVars,
        /// so this unconditional guard is the only thing that fixes the size.)
        /// </summary>
        /// <summary>
        /// Expands the <c>process_name</c> InputVar inside
        /// <c>Process1_Generic.fbt</c> from the shipped <c>STRING[15]</c> to
        /// <c>STRING[150]</c> (the size the reference <c>Process2_CAT</c>
        /// uses for the same field). Without this, any Process FB instance
        /// whose name exceeds 15 characters — e.g. <c>Assembly_Station</c>
        /// (16 chars), <c>Disassembly_Station</c> (19 chars) — fails to wire
        /// at deploy time with EAE's runtime CreateResource:
        /// <code>
        /// Unable to deploy: Deploy Full Command: Error while parsing the application
        /// CreateResource: Cannot connect parameter to data input process_name
        /// </code>
        /// because the string literal Mapper emits in the syslay Parameter
        /// (<c>Value="'Assembly_Station'"</c>) overflows the InputVar's
        /// declared size.
        ///
        /// <para>Idempotent — only touches the one VarDeclaration; channel
        /// rewrites, recipe arrays, and everything else stay untouched.
        /// Runs on hardware AND sim paths.</para>
        /// </summary>
        static void PatchProcessNameStringSize(string eaeProjectDir, DeployResult result)
        {
            var candidates = new[]
            {
                Path.Combine(eaeProjectDir, "IEC61499",
                    "Process1_Generic", "Process1_Generic.fbt"),
                Path.Combine(eaeProjectDir, "IEC61499",
                    "ProcessRuntime_Generic_v1.fbt"),
            };
            foreach (var fbtPath in candidates)
            {
                if (!File.Exists(fbtPath)) continue;
                try
                {
                    var doc = System.Xml.Linq.XDocument.Load(fbtPath,
                        System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    var root = doc.Root;
                    if (root == null) continue;
                    System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                    var processName = root.Descendants(ns + "VarDeclaration")
                        .FirstOrDefault(v =>
                            string.Equals((string?)v.Attribute("Name"),
                                "process_name", StringComparison.Ordinal));
                    if (processName == null) continue;

                    var typeAttr = processName.Attribute("Type");
                    var current = typeAttr?.Value ?? string.Empty;
                    // Already wide enough? STRING[150] / STRING[100] / etc.
                    if (current.StartsWith("STRING[", StringComparison.Ordinal))
                    {
                        // Extract the inner number.
                        int lb = current.IndexOf('[');
                        int rb = current.IndexOf(']', lb + 1);
                        if (rb > lb &&
                            int.TryParse(current.Substring(lb + 1, rb - lb - 1),
                                out var size) && size >= 150)
                        {
                            continue;   // already fine
                        }
                    }
                    processName.SetAttributeValue("Type", "STRING[150]");
                    doc.Save(fbtPath);
                    result.PatchesApplied.Add(
                        $"{Path.GetFileName(fbtPath)}: process_name {current} -> STRING[150] " +
                        "(supports long Process names like Assembly_Station)");
                    MapperLogger.Info(
                        $"[Deploy] {Path.GetFileName(fbtPath)}: process_name expanded to STRING[150].");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(
                        $"process_name STRING-size patch failed on {Path.GetFileName(fbtPath)}: {ex.Message}");
                }
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

        /// <summary>
        /// Anti-race guard for ProcessRuntime_Generic_v1 (the process engine).
        /// The engine inits LAST in the resource INIT chain, so it misses the
        /// actuators' boot-time state reports and its state_table starts blank (0).
        /// check_wait was evaluated on ENTRY to a WAIT step, so a WAIT whose target
        /// equals that blank value (e.g. "home" = 0) was satisfied INSTANTLY and the
        /// engine blew through the whole recipe in one tick without any actuator
        /// actually sequencing (rig-observed: CMDREQ pulsed for every step, swivel
        /// never left its boot Pick position, gripper never gripped).
        ///
        /// Fix: an internal <c>armed</c> flag. A WAIT is NOT satisfied on its first
        /// (entry) check_wait — only after a fresh state report arrives (the
        /// WAIT_STEP -> WAIT_STEP "state_change" self-loop re-runs check_wait). So
        /// the engine must observe the commanded actuator actually report before it
        /// advances. <c>armed</c> is cleared by AdvanceStep / initializeinit so every
        /// new WAIT re-arms. The recipe is built so each commanded step is a REAL
        /// move that reports (the Seven_State swivel home-preamble drives it off its
        /// boot work position; Five_State actuators boot home and their first command
        /// moves them), so the guard never stalls on a no-op.
        ///
        /// Idempotent XML deploy-time patch (same pattern as PatchCatSymlinkQi):
        /// no-op once present. The WaitSatisfied EXPRESSION is lifted verbatim from
        /// the existing check_wait so this works for both the struct and six-array
        /// recipe forms.
        /// </summary>
        static void PatchProcessRuntimeWaitGuard(string eaeProjectDir, bool apply, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "ProcessRuntime_Generic_v1.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(
                    fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                bool changed = false;

                System.Xml.Linq.XElement? St(string algName) =>
                    root.Descendants(ns + "Algorithm")
                        .FirstOrDefault(a => (string?)a.Attribute("Name") == algName)
                        ?.Element(ns + "ST");

                // Set/strip the trailing `armed := TRUE/FALSE;` in an algorithm's ST.
                bool SetArmed(System.Xml.Linq.XElement? st, string? desired)
                {
                    if (st == null) return false;
                    var before = st.Value;
                    var text = System.Text.RegularExpressions.Regex.Replace(
                        before, @"\s*armed\s*:=\s*(?:TRUE|FALSE)\s*;", "").TrimEnd();
                    if (desired != null) text += "\r\narmed := " + desired + ";";
                    if (text == before) return false;
                    st.ReplaceNodes(new System.Xml.Linq.XCData(text));
                    return true;
                }

                var internalVars = root.Descendants(ns + "InternalVars").FirstOrDefault();
                var armedDecl = internalVars?.Elements(ns + "VarDeclaration")
                    .FirstOrDefault(v => (string?)v.Attribute("Name") == "armed");
                var cw = St("check_wait");

                if (apply)
                {
                    // RIG path. The engine inits LAST in the resource INIT chain, so it
                    // misses the actuators' boot reports and state_table boots all-0; a
                    // WAIT whose target is 0 (home) then passes instantly -> race.
                    // Install an `armed` guard: armed defaults TRUE (initializeinit) so a
                    // STANDALONE/sensor WAIT still evaluates on entry (never stalls), but
                    // IssueCmd clears it FALSE so the WAIT that FOLLOWS a command must
                    // wait for THAT actuator's fresh report. AdvanceStep must NOT clear it.
                    if (internalVars != null && armedDecl == null)
                    {
                        internalVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "armed"),
                            new System.Xml.Linq.XAttribute("Type", "BOOL"),
                            new System.Xml.Linq.XAttribute("Comment",
                                "Anti-race: WAIT after a command waits for a fresh state report.")));
                        changed = true;
                    }
                    if (cw != null && !cw.Value.Contains("NOT armed"))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            cw.Value, @"WaitSatisfied\s*:=\s*(?<expr>[^;]*);");
                        if (m.Success)
                        {
                            var expr = m.Groups["expr"].Value.Trim();
                            var tail = cw.Value.Substring(m.Index + m.Length);
                            cw.ReplaceNodes(new System.Xml.Linq.XCData(
                                "IF NOT armed THEN\r\n\tarmed := TRUE;\r\n\tWaitSatisfied := FALSE;\r\n" +
                                "ELSE\r\n\tWaitSatisfied := " + expr + ";\r\nEND_IF;" + tail));
                            changed = true;
                        }
                    }
                    changed |= SetArmed(St("initializeinit"), "TRUE");
                    changed |= SetArmed(St("IssueCmd"), "FALSE");
                    changed |= SetArmed(St("AdvanceStep"), null);
                    if (changed)
                    {
                        doc.Save(fbt);
                        result.PatchesApplied.Add("ProcessRuntime_Generic_v1.fbt: anti-race WAIT guard applied (rig)");
                        MapperLogger.Info("[Deploy] ProcessRuntime_Generic_v1: anti-race WAIT guard applied (rig)");
                    }
                }
                else
                {
                    // SIMULATOR path. The sim-force FBs (SimSwivelForce / SimHopperForce
                    // + no-sensor timers) keep the engine's state_table accurate AFTER
                    // init, so the ORIGINAL entry-check engine is correct here. The guard
                    // is not only unnecessary but HARMFUL in sim: the sim swivel boots
                    // home, so the home-preamble's "CMD home" is a no-op that produces no
                    // fresh report and the guard would stall on it. So strip the guard:
                    // unwrap check_wait back to the plain assignment + remove every armed.
                    if (cw != null && cw.Value.Contains("NOT armed"))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(cw.Value,
                            @"IF\s+NOT\s+armed\s+THEN.*?ELSE\s*WaitSatisfied\s*:=\s*(?<expr>[^;]*);\s*END_IF;(?<tail>.*)",
                            System.Text.RegularExpressions.RegexOptions.Singleline);
                        if (m.Success)
                        {
                            cw.ReplaceNodes(new System.Xml.Linq.XCData(
                                "WaitSatisfied := " + m.Groups["expr"].Value.Trim() + ";" + m.Groups["tail"].Value));
                            changed = true;
                        }
                    }
                    changed |= SetArmed(St("initializeinit"), null);
                    changed |= SetArmed(St("IssueCmd"), null);
                    changed |= SetArmed(St("AdvanceStep"), null);
                    if (armedDecl != null) { armedDecl.Remove(); changed = true; }
                    if (changed)
                    {
                        doc.Save(fbt);
                        result.PatchesApplied.Add("ProcessRuntime_Generic_v1.fbt: WAIT guard removed (simulator uses original engine)");
                        MapperLogger.Info("[Deploy] ProcessRuntime_Generic_v1: WAIT guard removed (sim)");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ProcessRuntime wait-guard patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Tightens the rig WAIT semantics from "the state_table slot currently has
        /// the requested value" to "the ring message that woke the engine is a fresh
        /// report from the waited component with the requested value".
        ///
        /// This matters for repeated actuator targets in one recipe, especially
        /// Shaft_Vr Work -> Home -> Work. If the persistent state_table still carries
        /// the first Work report when the second Work wait is armed, the release row can
        /// be reached before the second physical down stroke really reports. Feeding the
        /// current ProcessHandler.component_state_out into the engine makes WAITs edge-
        /// true on the actual reporting component instead of table history.
        /// </summary>
        static void PatchProcessRuntimeFreshWaitTrigger(string eaeProjectDir, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "ProcessRuntime_Generic_v1.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }

            try
            {
                var doc = XDocument.Load(fbt, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                XNamespace ns = root.GetDefaultNamespace();
                bool changed = false;

                var inputVars = root.Element(ns + "InterfaceList")?.Element(ns + "InputVars");
                if (inputVars != null &&
                    !inputVars.Elements(ns + "VarDeclaration")
                        .Any(v => (string?)v.Attribute("Name") == "last_state_msg"))
                {
                    inputVars.Add(new XElement(ns + "VarDeclaration",
                        new XAttribute("Name", "last_state_msg"),
                        new XAttribute("Type", "Component_State_Msg"),
                        new XAttribute("Namespace", "Main"),
                        new XAttribute("Comment",
                            "Current stateRprtCmd ring message from ProcessHandler; WAITs satisfy only on this fresh report.")));
                    changed = true;
                }

                var stateChange = root.Element(ns + "InterfaceList")
                    ?.Element(ns + "EventInputs")
                    ?.Elements(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "state_change");
                if (stateChange != null &&
                    !stateChange.Elements(ns + "With")
                        .Any(w => (string?)w.Attribute("Var") == "last_state_msg"))
                {
                    stateChange.Add(new XElement(ns + "With",
                        new XAttribute("Var", "last_state_msg")));
                    changed = true;
                }

                var checkWait = root.Descendants(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "check_wait");
                var st = checkWait?.Element(ns + "ST");
                if (st == null)
                {
                    result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: no check_wait ST; fresh WAIT trigger skipped.");
                }
                else if (!st.Value.Contains("last_state_msg.src_id", StringComparison.Ordinal))
                {
                    var current = st.Value;
                    var waitId = current.Contains("Recipe[CurrentStep].Wait1Id", StringComparison.Ordinal)
                        ? "Recipe[CurrentStep].Wait1Id"
                        : "Wait1Id[CurrentStep]";
                    var waitState = current.Contains("Recipe[CurrentStep].Wait1State", StringComparison.Ordinal)
                        ? "Recipe[CurrentStep].Wait1State"
                        : "Wait1State[CurrentStep]";

                    string newBody =
                        "IF NOT armed THEN\r\n" +
                        "\tarmed := TRUE;\r\n" +
                        "\tWaitSatisfied := FALSE;\r\n" +
                        "\t(* Anti-leftover: if the target slot ALREADY equals the wait target at arm\r\n" +
                        "\t   time it is a stale value from a prior cycle (the actuator has not moved\r\n" +
                        "\t   yet this step). Mark it suspect so a stale slot cannot satisfy the WAIT. *)\r\n" +
                        $"\tleftoverSuspect := (state_table[{waitId}].state = {waitState});\r\n" +
                        "ELSE\r\n" +
                        $"\tIF (last_state_msg.src_id = {waitId}) AND (last_state_msg.state <> {waitState}) THEN\r\n" +
                        "\t\tleftoverSuspect := FALSE;\r\n" +
                        "\tEND_IF;\r\n" +
                        $"\tWaitSatisfied := (last_state_msg.src_id = {waitId}) AND (last_state_msg.state = {waitState}) AND (NOT leftoverSuspect);\r\n" +
                        "END_IF;\r\n" +
                        "\r\n" +
                        "PreviousStepText := ThisStepText;\r\n" +
                        "ThisStepText := 'Waiting for target state';\r\n" +
                        "NextStepText := 'Advance when satisfied';";

                    st.ReplaceNodes(new XCData(newBody));
                    changed = true;
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        "ProcessRuntime_Generic_v1: WAIT now requires the triggering ring message to be from the waited component (fresh-report guard)");
                    MapperLogger.Info("[Deploy] ProcessRuntime_Generic_v1: WAIT fresh-report trigger guard applied");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ProcessRuntime fresh WAIT trigger patch failed: {ex.Message}");
            }
        }

        static void PatchProcess1FreshWaitTriggerWiring(string eaeProjectDir, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Process1_Generic.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Process1_Generic.fbt not found; fresh WAIT trigger wiring skipped.");
                return;
            }

            try
            {
                var doc = XDocument.Load(fbt, LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                XNamespace ns = root.GetDefaultNamespace();
                var dataConns = root.Element(ns + "FBNetwork")?.Element(ns + "DataConnections");
                if (dataConns == null)
                {
                    result.Warnings.Add("Process1_Generic.fbt: DataConnections not found; fresh WAIT trigger wiring skipped.");
                    return;
                }

                if (dataConns.Elements(ns + "Connection").Any(c =>
                        (string?)c.Attribute("Source") == "ProcessHandler.component_state_out" &&
                        (string?)c.Attribute("Destination") == "ProcessEngine.last_state_msg"))
                    return;

                dataConns.Add(new XElement(ns + "Connection",
                    new XAttribute("Source", "ProcessHandler.component_state_out"),
                    new XAttribute("Destination", "ProcessEngine.last_state_msg"),
                    new XAttribute("dx1", "80")));
                doc.Save(fbt);
                result.PatchesApplied.Add(
                    "Process1_Generic: ProcessHandler.component_state_out wired to ProcessEngine.last_state_msg");
                MapperLogger.Info("[Deploy] Process1_Generic: wired fresh WAIT trigger message into ProcessRuntime");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Process1_Generic fresh WAIT trigger wiring failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Same QI=FALSE-by-default bug as Sensor_Bool_CAT, in
        /// Five_State_Actuator_CAT. Its internal SYMLINKMULTIVARDST
        /// (Name "Inputs", subscribes $${PATH}athome/atwork) and
        /// SYMLINKMULTIVARSRC (Name "Output", publishes
        /// $${PATH}OutputToHome/OutputToWork) both default QI=FALSE — the
        /// DST rejects every TM3DI16 publish so the actuator never sees its
        /// sensors, and the SRC never writes TM3DQ16 so the solenoid stays
        /// cold even when the ECC commands it (rig-confirmed). Force QI=TRUE
        /// on BOTH: insert after NAME1 if absent, flip FALSE→TRUE if present.
        /// Idempotent deploy-time guard against a future zip re-swap losing
        /// QI. Runs on hardware AND sim paths. Five_State_Actuator_CAT only —
        /// no event/ECC/NAME1 changes; Sensor_Bool_CAT untouched here.
        /// </summary>
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

        /// <summary>
        /// Drops the <c>MqttStateFormatter</c> basic FB (INT state → STRING
        /// payload via <c>INT_TO_STRING</c>) into the deployed
        /// <c>IEC61499/</c> folder if absent. The CAT patch wires this FB
        /// between the actuator/sensor state output and MQTT_PUBLISH.Payload1.
        /// Idempotent.
        /// </summary>
        static void DeployMqttFormatter(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dst = Path.Combine(eaeProjectDir, "IEC61499", "MqttStateFormatter.fbt");
                if (File.Exists(dst)) { result.PatchesApplied.Add("MqttStateFormatter.fbt already present"); return; }
                File.WriteAllText(dst, MqttStateFormatterFbt);
                result.PatchesApplied.Add("MqttStateFormatter.fbt deployed (INT→STRING payload)");
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

        /// <summary>
        /// Deploy the InterlockRule datatype (one rule's four INT fields). Registered via the
        /// DataTypesDeployed loop (same path as RecipeStep). Idempotent (copy-if-absent).
        /// </summary>
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

        /// <summary>
        /// Deploy the InterlockTable datatype so EAE resolves the single <c>RuleTable : InterlockTable</c>
        /// input the normalizers expose on the actuator CATs + CommonInterlockEvaluator. Registered via
        /// the DataTypesDeployed loop. Idempotent (copy-if-absent).
        /// </summary>
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

        /// <summary>Deploy the TargetStates datatype so EAE resolves the <c>Target : TargetStates</c>
        /// input the normalizers expose. Registered via the DataTypesDeployed loop. Idempotent.</summary>
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

        // TelemetryConfig: the resource-telemetry connection inputs folded into one struct —
        // matches MQTT_CONNECTION's QI/ConnectionID/URL/ClientIdentifier/ValidateCert/CACert types.
        const string TelemetryConfigDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"TelemetryConfig\" Comment=\"Telemetry connection config: wraps the MQTT_CONNECTION inputs\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"6/21/2026\" Remarks=\"single STRUCT input for Telemetry_CAT\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"QI\" Type=\"BOOL\" />\r\n" +
            "    <VarDeclaration Name=\"ConnectionID\" Type=\"STRING\" />\r\n" +
            "    <VarDeclaration Name=\"URL\" Type=\"STRING\" />\r\n" +
            "    <VarDeclaration Name=\"ClientIdentifier\" Type=\"STRING\" />\r\n" +
            "    <VarDeclaration Name=\"ValidateCert\" Type=\"USINT\" />\r\n" +
            "    <VarDeclaration Name=\"CACert\" Type=\"STRING\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        /// <summary>Deploy the TelemetryConfig datatype (the Telemetry_CAT Config input).
        /// Registered via the DataTypesDeployed loop. Idempotent (copy-if-absent).</summary>
        static void DeployTelemetryConfigDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "TelemetryConfig.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, TelemetryConfigDt);
                if (!result.DataTypesDeployed.Contains("TelemetryConfig"))
                    result.DataTypesDeployed.Add("TelemetryConfig");
                result.PatchesApplied.Add("TelemetryConfig.dt deployed + registered");
                MapperLogger.Info("[Deploy] TelemetryConfig.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"TelemetryConfig.dt deploy failed: {ex.Message}");
            }
        }

        // TelemetryHealth: the MQTT_CONNECTION status outputs folded into one struct.
        const string TelemetryHealthDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"TelemetryHealth\" Comment=\"Telemetry connection health: wraps the MQTT_CONNECTION status outputs\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"6/21/2026\" Remarks=\"single STRUCT output for Telemetry_CAT\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"IsConnected\" Type=\"BOOL\" />\r\n" +
            "    <VarDeclaration Name=\"ReturnCode\" Type=\"USINT\" />\r\n" +
            "    <VarDeclaration Name=\"Status\" Type=\"STRING\" />\r\n" +
            "    <VarDeclaration Name=\"NetworkState\" Type=\"STRING\" />\r\n" +
            "    <VarDeclaration Name=\"SecurityState\" Type=\"STRING\" />\r\n" +
            "    <VarDeclaration Name=\"ProtocolState\" Type=\"STRING\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        /// <summary>
        /// Strip the dead <c>state_out</c> boundary event from the deployed Seven-State centre-home
        /// CAT: the <c>&lt;Event Name="state_out"&gt;</c> (EventOutputs), its <c>&lt;Output&gt;</c> pin,
        /// and the <c>ActuatorCore.pst_out → state_out</c> connection. It fed the removed cross-resource
        /// MQTT bridge and has no consumer. Interface/visual cleanup only — the internal
        /// ActuatorCore.current_state_to_process (interlock + MqttFmt) is untouched. Idempotent.
        /// </summary>
        static void StripSevenStateStateOut(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var fbt = Path.Combine(eaeProjectDir, "IEC61499",
                    "Seven_State_Actuator_Centre_Home_CAT", "Seven_State_Actuator_Centre_Home_CAT.fbt");
                if (!File.Exists(fbt)) return;
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                int removed = 0;
                bool Is(System.Xml.Linq.XElement e, string local, string name) =>
                    e.Name.LocalName == local &&
                    string.Equals((string?)e.Attribute("Name"), name, StringComparison.Ordinal);
                foreach (var e in doc.Descendants().Where(e => Is(e, "Event", "state_out")).ToList())
                    { e.Remove(); removed++; }
                foreach (var o in doc.Descendants().Where(e => Is(e, "Output", "state_out")).ToList())
                    { o.Remove(); removed++; }
                foreach (var c in doc.Descendants().Where(e => e.Name.LocalName == "Connection" &&
                             string.Equals((string?)e.Attribute("Destination"), "state_out", StringComparison.Ordinal)).ToList())
                    { c.Remove(); removed++; }
                if (removed > 0)
                {
                    doc.Save(fbt, System.Xml.Linq.SaveOptions.DisableFormatting);
                    result.PatchesApplied.Add($"Seven_State_Actuator_Centre_Home_CAT: stripped dead state_out ({removed} node(s))");
                    MapperLogger.Info($"[Deploy] Seven_State state_out stripped ({removed} node(s))");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"StripSevenStateStateOut failed: {ex.Message}");
            }
        }

        /// <summary>
        /// VISUAL-only: keep the "HMI &amp; OPCUA Connectivity" section frame from spilling into the
        /// section below it. Sets the frame MoveStyle="None" (it was "AnyContained" and auto-grew over
        /// the next section when the IThis faceplate overflowed), caps its height to ~30 above the next
        /// section, and pulls the IThis FB up near the frame top so it sits inside. No wiring change.
        /// </summary>
        static void FixCatHmiOpcuaFrame(string eaeProjectDir, string catName, DeployResult result)
        {
            try
            {
                var fbt = Path.Combine(eaeProjectDir, "IEC61499", catName, catName + ".fbt");
                if (!File.Exists(fbt)) return;
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var net = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "FBNetwork");
                if (net == null) return;
                var ns = net.Name.Namespace;
                double Dv(System.Xml.Linq.XElement e, string a) =>
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
                doc.Save(fbt, System.Xml.Linq.SaveOptions.DisableFormatting);
                result.PatchesApplied.Add($"{catName}: HMI/OPCUA frame fixed (MoveStyle=None, H={newH}, IThis inside)");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"FixCatHmiOpcuaFrame({catName}) failed: {ex.Message}");
            }
        }

        /// <summary>Deploy the TelemetryHealth datatype (the Telemetry_CAT Health output).
        /// Registered via the DataTypesDeployed loop. Idempotent (copy-if-absent).</summary>
        static void DeployTelemetryHealthDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "TelemetryHealth.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, TelemetryHealthDt);
                if (!result.DataTypesDeployed.Contains("TelemetryHealth"))
                    result.DataTypesDeployed.Add("TelemetryHealth");
                result.PatchesApplied.Add("TelemetryHealth.dt deployed + registered");
                MapperLogger.Info("[Deploy] TelemetryHealth.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"TelemetryHealth.dt deploy failed: {ex.Message}");
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

        /// <summary>
        /// Fold an actuator CAT's target InputVars (<paramref name="targetInputs"/>) into one
        /// <c>Target : TargetStates</c> that flows whole into the interlock evaluator instance
        /// <paramref name="interlockFbName"/>. Bidirectional + idempotent; reduce==false restores the
        /// scalar inputs.
        /// </summary>
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

        /// <summary>
        /// Fold the CommonInterlockEvaluator's three target InputVars into one
        /// <c>Target : TargetStates</c> and rewrite the Work1/Work2/Home algorithms to read
        /// Target.Work1/Work2/Home. Bidirectional + idempotent.
        /// </summary>
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

        // Remove matching elements via instance Remove() (no
        // IEnumerable<XElement>.Remove() extension — this file has no
        // `using System.Xml.Linq`). Returns true if anything was removed.
        static bool RemoveElems(IEnumerable<System.Xml.Linq.XElement>? src,
            Func<System.Xml.Linq.XElement, bool> pred)
        {
            if (src == null) return false;
            var hits = src.Where(pred).ToList();
            foreach (var h in hits) h.Remove();
            return hits.Count > 0;
        }

        /// <summary>
        /// Simulator interface reduction on Five_State_Actuator_CAT: collapse the
        /// four parallel Rule arrays (face InputVar + INIT With + boundary Input +
        /// DataConnection to InterlockManager) into a single
        /// RuleTable : InterlockRule[10]. Bidirectional + idempotent like
        /// NormalizeFiveStateInterlockConstants (deployer is copy-if-absent, so the
        /// .fbt persists and must be reshaped to match the flag each deploy).
        /// reduce==true collapses to RuleTable; reduce==false restores the four
        /// arrays so the byte-identical hardware slice is untouched.
        /// </summary>
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

        /// <summary>
        /// Simulator-only centre-home swivel position synthesis. The earlier sim patch
        /// pointed the CAT's atHome and atWork1 subscriptions at the same OutputToWork1
        /// coil symlink, which made EAE Watch show an impossible physical state:
        /// atHome=TRUE and atWork1=TRUE. This normalizer now leaves the physical Inputs
        /// block on the real sensor symlinks and, in simulator mode only, inserts a small
        /// SimCentreHomeSensor_7SCH Basic inside the CAT. That helper derives mutually
        /// exclusive atHome/atWork1/atWork2 from ActuatorCore.current_state_to_process
        /// on ActuatorCore.pst_out, then feeds the normal ActuatorCore.input_event path.
        /// Hardware mode removes the helper and restores Jyotsna's physical wiring.
        /// </summary>
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

        /// <summary>
        /// Loads an .fbt/.xml with a short retry on a transient file lock (EAE briefly
        /// holding the file during a background scan). Throws the last lock exception
        /// after the final attempt, so a persistently-open editor tab still surfaces to
        /// the caller (which decides whether to abort or warn).
        /// </summary>
        static System.Xml.Linq.XDocument LoadXmlWithRetry(string path, System.Xml.Linq.LoadOptions opts)
        {
            for (int attempt = 1, delay = 50; ; attempt++, delay = Math.Min(delay * 2, 800))
            {
                try { return System.Xml.Linq.XDocument.Load(path, opts); }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 8)
                {
                    System.Threading.Thread.Sleep(delay);
                }
            }
        }

        /// <summary>
        /// Saves an XDocument with a short retry on a transient file lock, using the same
        /// default formatting as XDocument.Save(path) (so the on-disk bytes are identical
        /// to a plain doc.Save — byte-identical generation is preserved). Throws the last
        /// lock exception after the final attempt.
        /// </summary>
        static void SaveXmlWithRetry(System.Xml.Linq.XDocument doc, string path)
        {
            for (int attempt = 1, delay = 50; ; attempt++, delay = Math.Min(delay * 2, 800))
            {
                try { doc.Save(path); return; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 8)
                {
                    System.Threading.Thread.Sleep(delay);
                }
            }
        }

        /// <summary>
        /// Adds the fast home-sensor poll to the centre-home swivel CAT. Its atHome/atWork DIs are read by
        /// a sample-on-REQ SYMLINKMULTIVARDST ("Inputs") whose REQ was never driven, so the core re-sampled
        /// the sensors only intermittently and the non-spring arm coasted past centre before the coils cut.
        /// This injects the SAME poll the Five_State CAT uses — a self-looping E_DELAY ("HomePoll") firing
        /// Inputs.REQ every <paramref name="pollDtMs"/> ms — so ToHome->AtHome (gated on atHome) cuts the
        /// coils right at centre, the same both directions. Idempotent; purely additive (no recipe / state
        /// id / coil change). No-op if the CAT or its Inputs FB is absent.
        /// </summary>
        // 2026-06-19: the HomePoll / poll-gate CAT patch is REMOVED per the user. Bearing_PnP's home
        // is RECIPE-ONLY now (Assembly + Disassembly command bearing_pnp Home at the end of the bearing
        // sequence — see ApplyAssemblyRuntimeRecipe / ApplyDisassemblyRuntimeRecipe, final rows
        // CmdStateArr=5 / Wait1State=0). This method now ONLY STRIPS any previously-injected poll
        // machinery (HomePoll / PollGate1 / PollGate2 / PollWindow + their event/data connections) out
        // of the deployed CAT so a re-deploy cleans the live tree; it ADDS NOTHING and instantiates NO
        // replacement FB. The committed .cat.zip never carried these (they were always deploy-injected),
        // so a Clean + re-extract is already clean (Docs/INVARIANTS.md I-7).
        //
        // RUNTIME RISK (REPORTED, not worked around — per the user's explicit instruction): the CAT's
        // 'Inputs' SYMLINKMULTIVARDST is sample-on-REQ and HomePoll was the ONLY driver of Inputs.REQ
        // (commit b12f800). With the poll gone, NOTHING re-reads atHome/atWork1/atWork2 after a command,
        // so the core may not re-observe reaching a position and ToWork->AtWork / ToHome->AtHome may not
        // fire on the rig. If the swivel stops confirming positions, that is a CAT/interface architecture
        // gap to fix PROPERLY (an input-change event source, or an EAE cyclic/resource task driving
        // Inputs.REQ) — do NOT re-add a polling FB here.
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

        /// <summary>
        /// Simulator-only: repoint the Five_State_Actuator_CAT's internal Inputs
        /// SYMLINKMULTIVARDST so athome/atwork read the actuator's OWN drive-coil symlinks
        /// instead of the (unpublished-in-sim) physical sensor symlinks. The core's
        /// athome/atwork come from InputHandler (the No_Sensor_Handler), which — when
        /// WorkSensorFitted/HomeSensorFitted=TRUE — passes the physical sensor (Inputs.VALUE*
        /// = '$${PATH}athome'/'$${PATH}atwork', which NOTHING publishes in sim, so the ECC
        /// stalls at ToWork). Repointing those subscriptions at '$${PATH}OutputToHome' /
        /// '$${PATH}OutputToWork' makes athome/atwork follow the coils, so a sensored
        /// Five_State actuator (e.g. Bearing_Gripper, which deploys with WorkSensorFitted=TRUE
        /// on M580 and which the per-PLC no-sensor sysres override has not reliably reached)
        /// advances the instant it energises a coil. SAFE for no-sensor instances (M262
        /// Feed_Station, WorkSensorFitted=FALSE): InputHandler uses its TIMER for those and
        /// IGNORES Inputs.VALUE*, so the repoint is inert for them. Type-level (applies to
        /// every Five_State instance regardless of which .sysres it lives in) — that is why
        /// it works on M580 where the instance-parameter override did not. Bidirectional +
        /// idempotent: reduce==false (rig) restores '$${PATH}athome'/'$${PATH}atwork' so the
        /// hardware path reads its real sensors.
        /// </summary>
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

        // Debug OutputVars on the engine that have no output-event mapping out of the box.
        static readonly string[] EngineDebugVars = { "CurrentStep", "CurrentStepType", "WaitSatisfied" };

        /// <summary>
        /// Simulator-only watch aid: map the engine's "Exposed for debug" OutputVars
        /// (CurrentStep / CurrentStepType / WaitSatisfied) into the CMDREQ and SCNF
        /// output-event WITH lists so EAE Online Watch refreshes them (instead of leaving
        /// them frozen at power-up 0/FALSE — the WRN_XML_VAR_WITHOUT_OEVENT symptom). The
        /// vars are already computed by the ECC, so this is purely a watch/observability
        /// change — no logic, no ST, no algorithm touched. Bidirectional + idempotent:
        /// reduce==false (rig) strips the WITH entries so the hardware engine is byte-identical.
        /// </summary>
        static void NormalizeProcessEngineDebugWatch(string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "ProcessRuntime_Generic_v1.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt not found; engine debug-watch normalize skipped.");
                return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();
                var eventOutputs = root.Element(ns + "InterfaceList")?.Element(ns + "EventOutputs");
                if (eventOutputs == null)
                {
                    result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: EventOutputs not found; engine debug-watch normalize skipped.");
                    return;
                }

                bool changed = false;
                foreach (var evName in new[] { "CMDREQ", "SCNF" })
                {
                    var ev = eventOutputs.Elements(ns + "Event")
                        .FirstOrDefault(e => (string?)e.Attribute("Name") == evName);
                    if (ev == null) continue;
                    foreach (var v in EngineDebugVars)
                    {
                        var existing = ev.Elements(ns + "With")
                            .Where(w => (string?)w.Attribute("Var") == v).ToList();
                        if (reduce)
                        {
                            if (existing.Count == 0)
                            { ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", v))); changed = true; }
                        }
                        else
                        {
                            foreach (var w in existing) { w.Remove(); changed = true; }
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "ProcessRuntime_Generic_v1: CurrentStep/CurrentStepType/WaitSatisfied mapped to CMDREQ+SCNF (sim watchable)"
                        : "ProcessRuntime_Generic_v1: debug-watch WITH entries removed (hardware)");
                    MapperLogger.Info($"[Deploy] Engine debug-watch normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Engine debug-watch normalize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator interface reduction on the CommonInterlockEvaluator Basic FB:
        /// collapse the four Rule arrays into RuleTable : InterlockRule[10] across
        /// the InputVars, the three event &lt;With&gt; lists (REQ_WORK1/WORK2/HOME),
        /// AND the Evaluate ST (RuleFromState[i] -> RuleTable[i].FromState, etc).
        /// Bidirectional + idempotent. reduce==false restores the four arrays so
        /// hardware is byte-identical. Logic is unchanged either way — the same
        /// numbers feed the Evaluate loop, just read as struct fields.
        /// </summary>
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

        /// <summary>
        /// Simulator interface reduction on Five_State_Actuator_CAT: drop the two
        /// derived fault-enable inputs (enableToWorkFaultTimeout /
        /// enableToHomeFaultTimeout). The Mapper always sets each equal to its
        /// sensor-fitted flag, and the CAT's FB17/FB14 already AND the enable with
        /// the SAME sensor-fitted input — so re-pointing FB17.IN2/FB14.IN2 at the
        /// sensor-fitted lines gives AND(fitted, fitted) = fitted: identical
        /// behaviour, two fewer pins, no new FB and no event-timing change.
        /// Bidirectional + idempotent. reduce==false restores the inputs so the
        /// hardware path is byte-identical.
        /// </summary>
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

        // The six parallel recipe arrays that collapse into one Recipe : ARRAY OF
        // RecipeStep. Struct field name == array name so the engine ST rewrite is
        // a clean 1:1 ("StepType[CurrentStep]" -> "Recipe[CurrentStep].StepType").
        static readonly (string Name, string Type)[] RecipeArrays = new[]
        {
            ("StepType", "INT"),
            ("CmdTargetName", "STRING[150]"),
            ("CmdStateArr", "INT"),
            ("Wait1Id", "INT"),
            ("Wait1State", "INT"),
            ("NextStep", "INT"),
        };

        // Embedded RecipeStep datatype (one recipe row as a struct; mixed INT +
        // STRING[15]). Hand-authored without nxtDataType — EAE regenerates it.
        const string RecipeStepDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"RecipeStep\" Comment=\"One recipe step as a struct: StepType/CmdTargetName/CmdStateArr/Wait1Id/Wait1State/NextStep\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"5/24/2026\" Remarks=\"array-of-struct packaging of the 6 recipe arrays\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            "    <VarDeclaration Name=\"StepType\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"CmdTargetName\" Type=\"STRING[150]\" />\r\n" +
            "    <VarDeclaration Name=\"CmdStateArr\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Wait1Id\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"Wait1State\" Type=\"INT\" />\r\n" +
            "    <VarDeclaration Name=\"NextStep\" Type=\"INT\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        /// <summary>
        /// Deploy the RecipeStep datatype (simulator path) + register it, so EAE
        /// resolves the Recipe : RecipeStep[] inputs the recipe normalizers add to
        /// Process1_Generic and ProcessRuntime_Generic_v1. Idempotent.
        /// </summary>
        static void DeployRecipeStepDatatype(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var dtDir = Path.Combine(eaeProjectDir, "IEC61499", "DataType");
                Directory.CreateDirectory(dtDir);
                var dtPath = Path.Combine(dtDir, "RecipeStep.dt");
                if (!File.Exists(dtPath)) File.WriteAllText(dtPath, RecipeStepDt);
                if (!result.DataTypesDeployed.Contains("RecipeStep"))
                    result.DataTypesDeployed.Add("RecipeStep");
                result.PatchesApplied.Add("RecipeStep.dt deployed + registered (sim Recipe struct)");
                MapperLogger.Info("[Deploy] RecipeStep.dt written + registered");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"RecipeStep.dt deploy failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator interface reduction on Process1_Generic (composite): collapse
        /// the 6 recipe arrays (face InputVar + INIT With + boundary Input +
        /// DataConnection to ProcessEngine) into one Recipe : RecipeStep[].
        /// Bidirectional + idempotent; reduce==false restores the 6 arrays.
        /// </summary>
        static void NormalizeProcess1RecipeArrays(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "Process1_Generic.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("Process1_Generic.fbt not found; recipe-struct normalize skipped.");
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
                    result.Warnings.Add("Process1_Generic.fbt: missing InterfaceList/FBNetwork; recipe normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var initEvent = iface.Element(ns + "EventInputs")?.Elements(ns + "Event")
                    .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
                var dataConns = net.Element(ns + "DataConnections");
                var size = CodeGen.Translation.Process.ProcessRecipeArrayGenerator.RecipeArraySize.ToString();

                bool changed = false;

                if (reduce)
                {
                    foreach (var (nm, _) in RecipeArrays)
                    {
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == nm);
                        changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == nm);
                        changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == nm);
                        changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == nm);
                    }
                    if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == "Recipe"))
                    {
                        inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "Recipe"),
                            new System.Xml.Linq.XAttribute("Type", "RecipeStep"),
                            new System.Xml.Linq.XAttribute("ArraySize", size),
                            new System.Xml.Linq.XAttribute("Namespace", "Main")));
                        changed = true;
                    }
                    if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Recipe"))
                    { initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "Recipe"))); changed = true; }
                    if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == "Recipe"))
                    {
                        var pin = new System.Xml.Linq.XElement(ns + "Input",
                            new System.Xml.Linq.XAttribute("Name", "Recipe"),
                            new System.Xml.Linq.XAttribute("x", "300"),
                            new System.Xml.Linq.XAttribute("y", "1300"),
                            new System.Xml.Linq.XAttribute("Type", "Data"));
                        var last = net.Elements(ns + "Input").LastOrDefault();
                        if (last != null) last.AddAfterSelf(pin); else net.Add(pin);
                        changed = true;
                    }
                    if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == "Recipe"))
                    {
                        dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                            new System.Xml.Linq.XAttribute("Source", "Recipe"),
                            new System.Xml.Linq.XAttribute("Destination", "ProcessEngine.Recipe")));
                        changed = true;
                    }
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "Recipe");
                    changed |= RemoveElems(initEvent?.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "Recipe");
                    changed |= RemoveElems(net.Elements(ns + "Input"), i => (string?)i.Attribute("Name") == "Recipe");
                    changed |= RemoveElems(dataConns?.Elements(ns + "Connection"), c => (string?)c.Attribute("Source") == "Recipe");
                    var coords = new Dictionary<string, (string X, string Y)>
                    {
                        ["StepType"] = ("300", "1300"), ["CmdTargetName"] = ("300", "1750"),
                        ["CmdStateArr"] = ("300", "2200"), ["Wait1Id"] = ("300", "2650"),
                        ["Wait1State"] = ("300", "3100"), ["NextStep"] = ("300", "3550"),
                    };
                    foreach (var (nm, ty) in RecipeArrays)
                    {
                        if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == nm))
                        {
                            inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                new System.Xml.Linq.XAttribute("Name", nm),
                                new System.Xml.Linq.XAttribute("Type", ty),
                                new System.Xml.Linq.XAttribute("ArraySize", size)));
                            changed = true;
                        }
                        if (initEvent != null && !initEvent.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == nm))
                        { initEvent.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", nm))); changed = true; }
                        if (!net.Elements(ns + "Input").Any(i => (string?)i.Attribute("Name") == nm))
                        {
                            var (x, y) = coords[nm];
                            var pin = new System.Xml.Linq.XElement(ns + "Input",
                                new System.Xml.Linq.XAttribute("Name", nm),
                                new System.Xml.Linq.XAttribute("x", x),
                                new System.Xml.Linq.XAttribute("y", y),
                                new System.Xml.Linq.XAttribute("Type", "Data"));
                            var last = net.Elements(ns + "Input").LastOrDefault();
                            if (last != null) last.AddAfterSelf(pin); else net.Add(pin);
                            changed = true;
                        }
                        if (dataConns != null && !dataConns.Elements(ns + "Connection").Any(c => (string?)c.Attribute("Source") == nm))
                        {
                            dataConns.Add(new System.Xml.Linq.XElement(ns + "Connection",
                                new System.Xml.Linq.XAttribute("Source", nm),
                                new System.Xml.Linq.XAttribute("Destination", "ProcessEngine." + nm)));
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "Process1_Generic: 6 recipe arrays -> Recipe struct (sim)"
                        : "Process1_Generic: Recipe struct -> 6 recipe arrays (hardware)");
                    MapperLogger.Info($"[Deploy] Process1_Generic recipe normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Process1_Generic recipe-struct normalize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator interface reduction on ProcessRuntime_Generic_v1 (Basic FB):
        /// collapse the 6 recipe arrays across InputVars, the INIT event Withs, AND
        /// every algorithm's ST ("StepType[CurrentStep]" ->
        /// "Recipe[CurrentStep].StepType", etc.). Bidirectional + idempotent;
        /// reduce==false restores the 6 arrays. Logic unchanged.
        /// </summary>
        static void NormalizeProcessRuntimeRecipeArrays(
            string eaeProjectDir, bool reduce, DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    "ProcessRuntime_Generic_v1.fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt not found; recipe-struct normalize skipped.");
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
                    result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: missing InterfaceList/BasicFB; recipe normalize skipped.");
                    return;
                }
                var inputVars = iface.Element(ns + "InputVars");
                var eventInputs = iface.Element(ns + "EventInputs");
                var size = CodeGen.Translation.Process.ProcessRecipeArrayGenerator.RecipeArraySize.ToString();

                bool changed = false;

                if (reduce)
                {
                    foreach (var (nm, _) in RecipeArrays)
                        changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == nm);
                    if (inputVars != null && !inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == "Recipe"))
                    {
                        inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                            new System.Xml.Linq.XAttribute("Name", "Recipe"),
                            new System.Xml.Linq.XAttribute("Type", "RecipeStep"),
                            new System.Xml.Linq.XAttribute("ArraySize", size),
                            new System.Xml.Linq.XAttribute("Namespace", "Main")));
                        changed = true;
                    }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => RecipeArrays.Any(a => a.Name == (string?)w.Attribute("Var")))) continue;
                        foreach (var (nm, _) in RecipeArrays)
                            changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == nm);
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Recipe"))
                        { ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", "Recipe"))); changed = true; }
                    }
                }
                else
                {
                    changed |= RemoveElems(inputVars?.Elements(ns + "VarDeclaration"), v => (string?)v.Attribute("Name") == "Recipe");
                    if (inputVars != null)
                        foreach (var (nm, ty) in RecipeArrays)
                            if (!inputVars.Elements(ns + "VarDeclaration").Any(v => (string?)v.Attribute("Name") == nm))
                            {
                                inputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                                    new System.Xml.Linq.XAttribute("Name", nm),
                                    new System.Xml.Linq.XAttribute("Type", ty),
                                    new System.Xml.Linq.XAttribute("ArraySize", size)));
                                changed = true;
                            }
                    foreach (var ev in eventInputs?.Elements(ns + "Event") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == "Recipe")) continue;
                        changed |= RemoveElems(ev.Elements(ns + "With"), w => (string?)w.Attribute("Var") == "Recipe");
                        foreach (var (nm, _) in RecipeArrays)
                            if (!ev.Elements(ns + "With").Any(w => (string?)w.Attribute("Var") == nm))
                            { ev.Add(new System.Xml.Linq.XElement(ns + "With", new System.Xml.Linq.XAttribute("Var", nm))); changed = true; }
                    }
                }

                // ST rewrite across every algorithm.
                foreach (var alg in basic.Elements(ns + "Algorithm"))
                {
                    var stEl = alg.Element(ns + "ST");
                    if (stEl == null) continue;
                    var st = stEl.Value;
                    var before = st;
                    foreach (var (nm, _) in RecipeArrays)
                    {
                        var arr = nm + "[CurrentStep]";
                        var str = "Recipe[CurrentStep]." + nm;
                        st = reduce ? st.Replace(arr, str) : st.Replace(str, arr);
                    }
                    if (st != before) { stEl.ReplaceNodes(new System.Xml.Linq.XCData(st)); changed = true; }
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(reduce
                        ? "ProcessRuntime_Generic_v1: 6 recipe arrays -> Recipe struct + ST rewritten (sim)"
                        : "ProcessRuntime_Generic_v1: Recipe struct -> 6 recipe arrays + ST restored (hardware)");
                    MapperLogger.Info($"[Deploy] ProcessRuntime_Generic_v1 recipe normalize: reduce={reduce}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ProcessRuntime_Generic_v1 recipe-struct normalize failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Additive deploy-time patch: fan an <c>MQTT_PUBLISH</c> off the
        /// CAT's POST-UPDATE state-change event so every transition is
        /// published the same scan it happens — before any OPC UA / WebSocket
        /// sampler runs (those sit downstream of the 200/100 ms sampling floor
        /// and alias brief states out; this does not). Nothing existing is
        /// removed: the state event keeps all its current targets and we only
        /// add one fan-out (EAE allows multi-fan-out from an event output).
        /// </summary>
        /// <param name="stateEventSource">Post-update event that carries the new
        /// state. Actuator: <c>ActuatorCore.pst_out</c> (fires on entry to all
        /// five states, after the algorithm writes current_state_to_process).
        /// Sensor: <c>FB1.CNF</c> (fires after Status is written).</param>
        /// <param name="stateDataSource">INT state value to publish
        /// (<c>ActuatorCore.current_state_to_process</c> / <c>FB1.Status</c>).</param>
        /// <param name="initSource">Existing INITO event to seed MqttFmt/MqttPub INIT.</param>
        /// <param name="topicNameSource">CAT-level STRING InputVar carrying the
        /// per-instance component name — Five_State_Actuator_CAT exposes
        /// <c>actuator_name</c>, Sensor_Bool_CAT exposes <c>name</c>. Wired as
        /// a DATA input into <c>MqttPub.Topic1</c> at runtime so each instance
        /// publishes to <c>RootPath/&lt;component_name&gt;</c> instead of every
        /// instance sharing the same literal topic. The earlier approach of
        /// <c>'smc/$${PATH}'</c> failed: EAE 24.1's MQTT_PUBLISH does NOT
        /// resolve the <c>${PATH}</c> placeholder at runtime, so every
        /// instance published to the literal string <c>smc/${PATH}/state</c>
        /// — making the bridge work but leaving every component
        /// indistinguishable on the broker.</param>
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

                // Re-patchable: remove any existing MqttPub/MqttFmt FBs + their
                // wires before re-emitting fresh. Without this, an earlier
                // patch's stale topic/parameter shape persists across deploys
                // because Test Simulator only PrepareDemonstratorForGeneration
                // (which cleans .sysres FBNetwork) and does NOT invoke
                // DemonstratorWiper.Wipe (the CAT-folder delete). So the
                // deployed Five_State_Actuator_CAT.fbt carries the OLD MqttPub
                // forever and the old idempotency-skip stopped the new wire
                // shape ever reaching the runtime. Removing first guarantees
                // each deploy reflects the latest source.
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
                // common prefix ('smc'); Topic1 is wired from each CAT's
                // per-instance name InputVar (see DataConnection below) so
                // each instance publishes to <prefix>/<lowercased_name> —
                // smc/coverpnp_hr, smc/bearing_pnp, smc/topcoversenosr, etc.
                // The earlier sim-only gate kept the rig on the literal
                // 'smc/$${PATH}' + Topic1='state' pair, but EAE 24.1 doesn't
                // resolve $${PATH} at runtime (proven in sim test), so the
                // rig publishers were all hitting the same literal topic
                // 'smc/${PATH}/state' anyway — making per-component
                // breakdown impossible. Per-instance is strictly better and
                // affects the embedded MqttPub topic only, no other CAT
                // behaviour. M262/M580 publishers still hit ReturnCode 50
                // (firmware-gated MQTT on those PLCs); only BX1 (Soft dPAC)
                // actually pushes to the broker in runtime.
                P(pubFb, "RootPath", Q(cfg.MqttTopicRoot));
                // Topic1 intentionally NOT a parameter — wired below.
                P(pubFb, "QoS1", cfg.MqttQoS.ToString());
                P(pubFb, "Retain1", cfg.MqttRetain ? "TRUE" : "FALSE");

                // Insert the two FBs after the last existing <FB>.
                var lastFb = net.Elements(ns + "FB").LastOrDefault();
                if (lastFb != null) { lastFb.AddAfterSelf(pubFb); lastFb.AddAfterSelf(fmtFb); }
                else { net.Add(fmtFb); net.Add(pubFb); }

                // VISUAL grouping only (no wiring change): a light-blue frame around MqttFmt+MqttPub.
                // They sit at x>=8000, RIGHT of every existing section frame (which end at x=7900),
                // so this never overlaps Core/Faults/Interlock/HMI-OPCUA/Mode. Idempotent re-patch.
                System.Xml.Linq.XElement Fp(string n, string v) => new System.Xml.Linq.XElement(ns + "Parameter",
                    new System.Xml.Linq.XAttribute("Name", n), new System.Xml.Linq.XAttribute("Value", v));
                net.Elements(ns + "Frame")
                   .Where(fr => (string?)fr.Attribute("Name") == "FRAME_MQTT").Remove();
                net.Add(new System.Xml.Linq.XElement(ns + "Frame",
                    new System.Xml.Linq.XAttribute("Name", "FRAME_MQTT"),
                    new System.Xml.Linq.XAttribute("X", "7920"),
                    new System.Xml.Linq.XAttribute("Y", "2360"),
                    new System.Xml.Linq.XAttribute("Width", "1840"),
                    new System.Xml.Linq.XAttribute("Height", "1080"),
                    Fp("BackgroundColor", "LightBlue"), Fp("TextColor", "Black"),
                    Fp("Font", "Microsoft Sans Serif, 10pt"), Fp("TextAlignment", "TopRight"),
                    Fp("MoveStyle", "AnyContained"), Fp("Text", "MQTT"), Fp("NxtLayerIdentifier", "")));

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
                // Per-instance topic suffix — wires the CAT's per-instance
                // name InputVar (Five_State.actuator_name / Sensor_Bool.name)
                // into MqttPub.Topic1. Runtime concatenates RootPath/Topic1,
                // so each instance publishes to <MqttTopicRoot>/<name>.
                // Applies to BOTH rig and sim — the earlier sim-only gate
                // was a safety clamp now lifted (see RootPath branch).
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

        /// <summary>
        /// Additive deploy-time patch: expose a CAT's internal post-update state
        /// at the COMPOSITE BOUNDARY (Event <c>state_out</c> + INT
        /// <c>current_state_to_process</c>) so a cross-resource consumer can read
        /// it through the syslay. Used by the BX1-side MQTT bridge: each
        /// <c>MqttPub_&lt;comp&gt;</c> on BX1 wires to <c>&lt;comp&gt;.state_out</c>
        /// (event) and <c>&lt;comp&gt;.current_state_to_process</c> (data) on the
        /// remote M262/M580 component, and EAE bridges the cross-resource events
        /// at deploy. Nothing existing is removed — the internal source event
        /// keeps all its current targets (EAE allows event multi-fan-out, and
        /// <c>current_state_to_process</c> already fans to several consumers).
        /// Idempotent: skips if <c>state_out</c> already exists in EventOutputs.
        /// </summary>
        static void PatchCatExposeState(string eaeProjectDir, string catName,
            string internalEventSource, string internalDataSource,
            DeployResult result)
        {
            var fbt = Directory.EnumerateFiles(
                    Path.Combine(eaeProjectDir, "IEC61499"),
                    catName + ".fbt", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("_HMI", StringComparison.Ordinal))
                ?? string.Empty;
            if (string.IsNullOrEmpty(fbt))
            {
                result.Warnings.Add($"{catName}.fbt not found; expose-state patch skipped.");
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
                    result.Warnings.Add($"{catName}.fbt: missing InterfaceList/FBNetwork; expose-state skipped.");
                    return;
                }

                var eventOutputs = iface.Element(ns + "EventOutputs");
                if (eventOutputs == null)
                {
                    eventOutputs = new System.Xml.Linq.XElement(ns + "EventOutputs");
                    var eventInputs = iface.Element(ns + "EventInputs");
                    if (eventInputs != null) eventInputs.AddAfterSelf(eventOutputs);
                    else iface.AddFirst(eventOutputs);
                }

                // Idempotent.
                if (eventOutputs.Elements(ns + "Event")
                        .Any(e => (string?)e.Attribute("Name") == "state_out"))
                {
                    result.PatchesApplied.Add($"{catName}: state_out already exposed at boundary (skipped)");
                    return;
                }

                // 1) EventOutput state_out WITH current_state_to_process.
                eventOutputs.Add(new System.Xml.Linq.XElement(ns + "Event",
                    new System.Xml.Linq.XAttribute("Name", "state_out"),
                    new System.Xml.Linq.XAttribute("Comment", "Post-update state for the cross-resource MQTT bridge"),
                    new System.Xml.Linq.XElement(ns + "With",
                        new System.Xml.Linq.XAttribute("Var", "current_state_to_process"))));

                // 2) OutputVar current_state_to_process : INT. Create OutputVars
                //    section if absent (DTD order: after InputVars, before Sockets).
                var outputVars = iface.Element(ns + "OutputVars");
                if (outputVars == null)
                {
                    outputVars = new System.Xml.Linq.XElement(ns + "OutputVars");
                    var inputVars = iface.Element(ns + "InputVars");
                    if (inputVars != null) inputVars.AddAfterSelf(outputVars);
                    else eventOutputs.AddAfterSelf(outputVars);
                }
                if (!outputVars.Elements(ns + "VarDeclaration")
                        .Any(v => (string?)v.Attribute("Name") == "current_state_to_process"))
                    outputVars.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                        new System.Xml.Linq.XAttribute("Name", "current_state_to_process"),
                        new System.Xml.Linq.XAttribute("Type", "INT"),
                        new System.Xml.Linq.XAttribute("Comment", "Exposed component state for cross-resource MQTT bridge")));

                // 3) Internal fan-out from the existing state sources to the
                //    boundary. Additive — the internal sources keep all current
                //    targets (EAE allows event multi-fan-out, data multi-fan-out).
                var ec = net.Element(ns + "EventConnections");
                if (ec == null) { ec = new System.Xml.Linq.XElement(ns + "EventConnections"); net.Add(ec); }
                var dc = net.Element(ns + "DataConnections");
                if (dc == null) { dc = new System.Xml.Linq.XElement(ns + "DataConnections"); net.Add(dc); }
                ec.Add(new System.Xml.Linq.XElement(ns + "Connection",
                    new System.Xml.Linq.XAttribute("Source", internalEventSource),
                    new System.Xml.Linq.XAttribute("Destination", "state_out")));
                dc.Add(new System.Xml.Linq.XElement(ns + "Connection",
                    new System.Xml.Linq.XAttribute("Source", internalDataSource),
                    new System.Xml.Linq.XAttribute("Destination", "current_state_to_process")));

                doc.Save(fbt);
                result.PatchesApplied.Add(
                    $"{catName}: state exposed at boundary ({internalEventSource} → state_out, " +
                    $"{internalDataSource} → current_state_to_process)");
                MapperLogger.Info(
                    $"[Deploy][MQTT bridge] {catName}.fbt: state_out / current_state_to_process exposed at composite boundary");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{catName} expose-state patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulator-only interface reduction for Five_State_Actuator_CAT. The
        /// InterlockManager constants TargetWork1State (=2, AtWork) and
        /// TargetHomeState (=4, AtHome end-of-cycle latch) are IDENTICAL on every
        /// actuator instance — <c>BuildActuatorParameters</c> hardcodes them. On
        /// the simulator path we delete those two boundary inputs from the CAT
        /// and bake the constants straight onto the embedded InterlockManager FB
        /// as <c>&lt;Parameter&gt;</c>, shrinking the wired instance interface
        /// from 17 to 15 WITHOUT changing any runtime value.
        ///
        /// This is a BIDIRECTIONAL NORMALIZER, not a one-way strip, because the
        /// template deployer copies artefacts only when absent
        /// (ExtractToEae/CopyDirToEae are copy-if-missing). The deployed CAT is
        /// therefore a single persistent file shared by BOTH the Test Simulator
        /// and Test Runtime buttons. <paramref name="reduce"/>==true strips and
        /// bakes; ==false restores the wired inputs and removes the baked params.
        /// Call it on every deploy with reduce=false (rig) so the CAT shape always
        /// matches the <c>&lt;Parameter&gt;</c> set BuildActuatorParameters emits.
        /// Idempotent both ways.
        /// </summary>
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
            "      <VarDeclaration Name=\"payload\" Type=\"STRING\" Comment=\"MQTT payload (state as text)\" />\r\n" +
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

        /// <summary>
        /// Same Mode=0-by-default class of bug as ProcessRuntime_Generic_v1's
        /// Mode/CycleType (fixed via InitialValue=1). The FiveStateActuator
        /// basic FB's "mode" InputVar has no InitialValue, so it powers up 0.
        /// Every AtHomeInit/AtWork exit ECTransition requires mode = 1/2/3
        /// (auto/cycle/setup); no mode_event fires at boot, so with mode=0
        /// the actuator ECC is stuck in AtHomeInit forever (rig-confirmed).
        /// Force the mode InputVar's InitialValue=1 so every actuator
        /// instance powers up in auto mode. Idempotent deploy-time guard
        /// (insert the attribute if absent, set to 1 if present). Runs every
        /// deploy so a future zip re-swap losing it is auto-fixed.
        /// FiveStateActuator basic FB only — no CAT/ECC/recipe changes.
        /// </summary>
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

        // 2026-06-04: relax the swivel core's work-arrival latches so a brief overlap of
        // the two physical work sensors (or a slightly noisy DI) no longer blocks the
        // latch. ToWork1->AtWork1 / ToWork2->AtWork2 fire on atWorkN=TRUE alone instead
        // of "atWorkN=TRUE AND atWorkOther=FALSE". Bidirectional: relax on the rig,
        // restore the strict mutually-exclusive guard on the simulator. Idempotent.
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

        /// <summary>
        /// 2026-06-08 (Alex/Jyotsna leftover-data fix). Makes the ProcessRuntime engine's
        /// WAIT step edge-triggered so a STALE <c>state_table</c> slot cannot satisfy a WAIT.
        /// The ring (<c>updateComponentState</c>) never clears <c>state_table</c>, so every
        /// slot holds the last value an actuator ever reported; the engine's <c>armed</c>
        /// guard only blocks the very first <c>check_wait</c> after a command, after which any
        /// <c>state_change</c> from any actuator re-tests the persistent slot — so a slot left
        /// at the wait target by a prior cycle is satisfied prematurely. This rewrites
        /// <c>check_wait</c> to arm a <c>leftoverSuspect</c> flag iff the slot ALREADY equals
        /// the target at arm time (a stale value: the actuator has not moved yet this step),
        /// clear it the moment the slot leaves the target, and satisfy the WAIT only when the
        /// slot equals the target AND is not suspect — i.e. only on a genuine fresh transition
        /// INTO the target. Engine-only (one BOOL InternalVar), no ring/wiring change, applies
        /// to every ProcessRuntime instance. Idempotent: skips if already patched.
        /// </summary>
        static void PatchEngineWaitFreshReport(string eaeProjectDir, DeployResult result)
        {
            var fbt = Path.Combine(eaeProjectDir, "IEC61499", "ProcessRuntime_Generic_v1.fbt");
            if (!File.Exists(fbt))
            {
                fbt = Directory.EnumerateFiles(
                        Path.Combine(eaeProjectDir, "IEC61499"),
                        "ProcessRuntime_Generic_v1.fbt", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrEmpty(fbt)) return;
            }
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(fbt, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                var root = doc.Root;
                if (root == null) return;
                System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

                var checkWait = root.Descendants(ns + "Algorithm")
                    .FirstOrDefault(a => (string?)a.Attribute("Name") == "check_wait");
                if (checkWait == null)
                {
                    result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: no check_wait algorithm; leftover-WAIT fix skipped.");
                    return;
                }

                var st = checkWait.Element(ns + "ST");
                // Idempotent: the patched body is the only place 'leftoverSuspect' appears.
                if (st != null && st.Value.Contains("leftoverSuspect"))
                    return;

                const string newBody =
                    "IF NOT armed THEN\r\n" +
                    "\tarmed := TRUE;\r\n" +
                    "\tWaitSatisfied := FALSE;\r\n" +
                    "\t(* Anti-leftover: if the target slot ALREADY equals the wait target at arm\r\n" +
                    "\t   time it is a stale value from a prior cycle (the actuator has not moved\r\n" +
                    "\t   yet this step). Mark it suspect so a stale slot cannot satisfy the WAIT. *)\r\n" +
                    "\tleftoverSuspect := (state_table[Recipe[CurrentStep].Wait1Id].state = Recipe[CurrentStep].Wait1State);\r\n" +
                    "ELSE\r\n" +
                    "\tIF state_table[Recipe[CurrentStep].Wait1Id].state <> Recipe[CurrentStep].Wait1State THEN\r\n" +
                    "\t\tleftoverSuspect := FALSE;\r\n" +
                    "\tEND_IF;\r\n" +
                    "\tWaitSatisfied := (state_table[Recipe[CurrentStep].Wait1Id].state = Recipe[CurrentStep].Wait1State) AND (NOT leftoverSuspect);\r\n" +
                    "END_IF;\r\n" +
                    "\r\n" +
                    "PreviousStepText := ThisStepText;\r\n" +
                    "ThisStepText := 'Waiting for target state';\r\n" +
                    "NextStepText := 'Advance when satisfied';";

                bool changed = false;
                if (st != null)
                {
                    st.ReplaceAll(new System.Xml.Linq.XCData(newBody));
                    changed = true;
                }

                var basic = root.Descendants(ns + "BasicFB").FirstOrDefault();
                var internals = basic?.Element(ns + "InternalVars");
                if (internals != null &&
                    !internals.Elements(ns + "VarDeclaration")
                        .Any(v => (string?)v.Attribute("Name") == "leftoverSuspect"))
                {
                    internals.Add(new System.Xml.Linq.XElement(ns + "VarDeclaration",
                        new System.Xml.Linq.XAttribute("Name", "leftoverSuspect"),
                        new System.Xml.Linq.XAttribute("Type", "BOOL"),
                        new System.Xml.Linq.XAttribute("Comment",
                            "Anti-leftover: TRUE when the WAIT target slot already held the target at arm time (stale); blocks a stale slot from satisfying the WAIT until a fresh transition into the target.")));
                    changed = true;
                }

                if (changed)
                {
                    doc.Save(fbt);
                    result.PatchesApplied.Add(
                        "ProcessRuntime_Generic_v1.fbt: WAIT made edge-triggered (anti-leftover) -- a stale state_table slot can no longer satisfy a WAIT; only a fresh transition into the target.");
                    MapperLogger.Info("[Deploy] ProcessRuntime_Generic_v1.fbt: check_wait edge-triggered (leftover-data fix)");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ProcessRuntime_Generic_v1.fbt WAIT anti-leftover patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 2026-06-08 ROOT-CAUSE fix (confirmed live: the swivel's component_state_in read
        /// (src_id:=1, source_name:='BearingSensor', dest_name:='bearing_pnp', state:=0)).
        /// The shared ring relay <c>updateComponentState.REQ</c> (a component reporting its
        /// OWN state) sets src_id/source_name/state but never clears <c>dest_name</c>.
        /// <c>Component_State_Msg</c> is a reused struct, so once a command
        /// (dest_name=&lt;target&gt;) has been relayed, the next REPORT inherits that stale
        /// dest_name and spuriously satisfies the target actuator's BREQ match
        /// (dest_name==name), overwriting its <c>state_cmd</c> with the reporting
        /// component's state. This clobbers Bearing_PnP's Place command (state_cmd:=3)
        /// back to 0 on the next BearingSensor report. Fix: REQ clears
        /// component_state_out.dest_name so a report carries no command target. Idempotent;
        /// applies to every updateComponentState instance (the shared relay).
        /// </summary>
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

        /// <summary>
        /// The ring relay must always pass messages forward with BCNF, but it must only
        /// fire CNF into the actuator core when the message is truly addressed to this
        /// actuator. Otherwise any unrelated state report replays the last retained
        /// state_cmd through ActuatorCore.pst_event.
        /// </summary>
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

        // (PatchSwivelAtHomeTimerWiring removed 2026-06-04: atHome is intentionally the
        // real DI02 on the rig, wired by NormalizeSwivelSimSensorSource -- the timer
        // output can fire before the arm reaches centre and stop Home short or overshoot,
        // so it must NOT drive the core's atHome when the physical centre sensor works.)

        // 2026-06-03: see the detailed rationale at the call site in
        // DeployUniversalArchitecture. Adds (rig) or strips (sim) two
        // sensor-recovery ECTransitions on the Centre-Home swivel core so a
        // swivel that powered up before its IO went live (frozen in AtHomeInit
        // while physically at a work position) re-syncs to AtWork1/AtWork2 and
        // can then accept the engine's Home command. Identified by
        // Source=AtHomeInit AND Destination in {AtWork1,AtWork2}; the stock
        // AtHomeInit arcs go to ToWork1/ToWork2 so this never collides with them.
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

                // SELF-HOME ON POWER-UP (2026-06-03, supersedes the earlier "re-sync to
                // AtWork" recovery). Jyotsna's core latches whatever work position it
                // physically booted at: INIT -> AtWork1 (atWork1 TRUE) or INIT -> ToWork2
                // (atWork2 TRUE). The swivel has NO spring-centre (both coils off => it
                // holds position -- confirmed on the rig), so the ONLY way to make HOME
                // its initial state is to DRIVE it home at power-up. On the rig (addArc)
                // redirect every "booted at a work position" boot path to ToHome, so the
                // actuator swings itself home the instant it powers up, before the engine
                // even starts -- home becomes its permanent initial positioning state,
                // independent of the recipe. Covers both the IO-ready boot (INIT -> work)
                // and the IO-late boot (INIT -> AtHomeInit, then a work sensor comes TRUE
                // -> AtHomeInit -> ToHome). On the sim (!addArc) restore INIT -> work
                // states (inert there: coils are FALSE at sim boot so atwork is FALSE and
                // INIT -> AtHomeInit fires) and strip the self-home arcs, leaving the
                // proven sim core unchanged. Idempotent + bidirectional. NOTE: the arm
                // physically moves (swings home) at power-up -- safe direction (toward
                // centre), but the rig swing path must be clear before a cold download.
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

                // RIG: drive home on power-up via INIT ONLY. (2026-06-03 -- the
                // AtHomeInit -> ToHome "self-home" arc is now REMOVED, not added.)
                // That arc re-fired whenever the swivel sat in AtHomeInit and a work
                // sensor momentarily read TRUE -- and the rig DI00/DI01 are noisy /
                // bad-quality, so it re-homed over and over: observed as
                // current_state=5 (ToHome) with state_val=0 and the arm cycling
                // atWork1<->atWork2, never settling. INIT -> ToHome below already homes
                // the swivel on every power-up; once it reaches AtHomeInit it MUST stay
                // there until the recipe commands Pick/Place, so AtHomeInit must have NO
                // self-driving exit. So: redirect INIT to ToHome, and strip every
                // AtHomeInit -> {ToHome, AtWork1, AtWork2} arc we ever added. The stock
                // AtHomeInit -> ToWork1/ToWork2 (state_val Pick/Place) arcs stay intact.
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

        // 2026-06-03: see the detailed rationale at the call site. Wires the AtHome
        // ECState to the coil-clearing 'atHome' algorithm and makes AtHome publish
        // output_event, so the Output SYMLINKMULTIVARSRC writes both work coils FALSE.
        // Both algorithms already exist in the core; this only swaps which one the
        // AtHome state runs and whether the coil-clear event is emitted.
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

        // Seven_State_Actuator_CAT data-driven patch removed 2026-05-21.

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

        /// <summary>
        /// Phase 1 recipe-arrays-as-InputVars patch. Mutates the deployed
        /// ProcessRuntime_Generic_v1.fbt and Process1_Generic.fbt so the recipe arrays
        /// (StepType, CmdTargetName, CmdStateArr, Wait1Id, Wait1State, NextStep) are
        /// exposed as InputVars on both the engine FB and its outer composite, and the
        /// engine's initialize algorithm no longer hardcodes the 3-step Pusher demo
        /// recipe. The Mapper then writes the per-Process recipe as syslay Parameter
        /// values on the Process1 instance via SystemLayoutInjector.BuildProcessFbParameters.
        ///
        /// Idempotent: re-deploying skips files that already declare the array InputVars.
        /// </summary>
        static void PatchProcessFbsForRecipeAsInputVars(string eaeProjectDir, DeployResult result)
        {
            var iec = Path.Combine(eaeProjectDir, "IEC61499");
            var enginePath = Path.Combine(iec, "ProcessRuntime_Generic_v1.fbt");
            var compositePath = Path.Combine(iec, "Process1_Generic", "Process1_Generic.fbt");

            try
            {
                if (File.Exists(enginePath))
                {
                    PatchProcessRuntimeEngine(enginePath, result);
                    PatchProcessRuntimeEccDeadEnd(enginePath, result);
                    PatchProcessRuntimeStartBypass(enginePath, result);
                    PatchProcessRuntimeEndSequenceNoOp(enginePath, result);
                }
                else
                    result.Warnings.Add("ProcessRuntime_Generic_v1.fbt not deployed; recipe-as-input patch skipped.");

                if (File.Exists(compositePath))
                    PatchProcess1GenericComposite(compositePath, result);
                else
                    result.Warnings.Add("Process1_Generic.fbt not deployed; recipe-as-input patch skipped.");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Recipe-as-input FBT patch failed: {ex.Message}");
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
                PatchProcessRuntimeEccDeadEnd(enginePath, result);
                PatchProcessRuntimeStartBypass(enginePath, result);
                PatchProcessRuntimeEndSequenceNoOp(enginePath, result);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"ProcessRuntime_Generic_v1 compatibility patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Silence EAE's WRN_ECC_DEAD_END on the <c>END</c> state of
        /// ProcessRuntime_Generic_v1 by adding a self-transition
        /// <c>END → END</c> with <c>Condition="1"</c> — the same shape EAE
        /// itself uses for the existing <c>WAIT_STEP → WAIT_STEP</c> loop.
        /// Runtime behaviour is unchanged (END already terminates the
        /// sequence; this just makes the dead-end explicit so the compiler
        /// stops warning). Idempotent: skips if an END→END transition is
        /// already present. Runs outside PatchProcessRuntimeEngine's
        /// recipe-array idempotency gate so it applies even on FBTs that
        /// were already recipe-patched.
        /// </summary>
        static void PatchProcessRuntimeEccDeadEnd(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var ecc = root.Descendants(ns + "ECC").FirstOrDefault();
            if (ecc == null)
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: <ECC> not found; END dead-end patch skipped.");
                return;
            }

            bool hasEndState = ecc.Elements(ns + "ECState")
                .Any(s => (string?)s.Attribute("Name") == "END");
            if (!hasEndState)
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: ECState END not found; dead-end patch skipped.");
                return;
            }

            // Idempotency: already has an END self-transition?
            bool alreadyLooped = ecc.Elements(ns + "ECTransition").Any(t =>
                (string?)t.Attribute("Source") == "END" &&
                (string?)t.Attribute("Destination") == "END");
            if (alreadyLooped) return;

            // Append after the last ECTransition, mirroring the self-closed
            // WAIT_STEP→WAIT_STEP shape (no Attribute child, has x/y).
            var lastTrans = ecc.Elements(ns + "ECTransition").LastOrDefault();
            var endState = ecc.Elements(ns + "ECState")
                .First(s => (string?)s.Attribute("Name") == "END");
            var ex = (string?)endState.Attribute("x") ?? "1983.655";
            var ey = (string?)endState.Attribute("y") ?? "968.8892";
            var loop = new System.Xml.Linq.XElement(ns + "ECTransition",
                new System.Xml.Linq.XAttribute("Source", "END"),
                new System.Xml.Linq.XAttribute("Destination", "END"),
                new System.Xml.Linq.XAttribute("Condition", "1"),
                new System.Xml.Linq.XAttribute("x", ex),
                new System.Xml.Linq.XAttribute("y", ey));
            if (lastTrans != null) lastTrans.AddAfterSelf(loop);
            else ecc.Add(loop);

            doc.Save(fbtPath);
            result.PatchesApplied.Add("ProcessRuntime_Generic_v1: added END->END self-transition (WRN_ECC_DEAD_END fix)");
            MapperLogger.Info("[Deploy] Patched ProcessRuntime_Generic_v1.fbt END dead-end (added END->END Condition=1)");
        }

        /// <summary>
        /// Remove the START -> IDLE1 bypass transition whose Condition is
        /// "Mode = 1 AND CycleType &lt;&gt; 0". Mode/CycleType both default
        /// to InitialValue=1, so that guard is TRUE at power-up and the ECC
        /// walks START -> IDLE1 directly, skipping the INIT state — so
        /// initializeinit never runs and the WITH-sampled recipe arrays are
        /// never sampled before LoadStep executes (Audit 1 root cause).
        /// After this patch the only outgoing transition from START is
        /// START -> INIT (Condition "INIT"). Idempotent: matches the
        /// Destination + Condition rather than a fixed element, and is a
        /// no-op once the transition is gone. Runs outside
        /// PatchProcessRuntimeEngine's recipe-array idempotency gate so it
        /// applies even to already-recipe-patched FBTs.
        /// </summary>
        static void PatchProcessRuntimeStartBypass(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var ecc = root.Descendants(ns + "ECC").FirstOrDefault();
            if (ecc == null)
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: <ECC> not found; START-bypass patch skipped.");
                return;
            }

            // Normalise whitespace/entity differences: EAE writes the XML
            // text as Condition="Mode = 1 AND CycleType &lt;&gt; 0"; once
            // parsed the attribute value is the literal
            //   Mode = 1 AND CycleType <> 0
            // Match on Source=START, Destination=IDLE1 and a Condition that,
            // with all spaces stripped, contains "CycleType<>0" — robust to
            // spacing variants.
            var bypass = ecc.Elements(ns + "ECTransition").Where(t =>
                (string?)t.Attribute("Source") == "START" &&
                (string?)t.Attribute("Destination") == "IDLE1" &&
                ((string?)t.Attribute("Condition") ?? string.Empty)
                    .Replace(" ", string.Empty)
                    .Contains("CycleType<>0", StringComparison.Ordinal)).ToList();

            int startOutgoing = ecc.Elements(ns + "ECTransition")
                .Count(t => (string?)t.Attribute("Source") == "START");

            if (bypass.Count == 0)
            {
                // Already removed (idempotent no-op). Still emit the
                // verification line so a regression is visible at deploy.
                result.PatchesApplied.Add(
                    $"ProcessRuntime_Generic_v1: START-bypass already absent; " +
                    $"START has {startOutgoing} outgoing transition(s)");
                MapperLogger.Info(
                    $"[Deploy] ProcessRuntime_Generic_v1.fbt: START-bypass not present; " +
                    $"START outgoing transitions = {startOutgoing}");
                return;
            }

            foreach (var t in bypass) t.Remove();
            doc.Save(fbtPath);

            int startOutgoingAfter = ecc.Elements(ns + "ECTransition")
                .Count(t => (string?)t.Attribute("Source") == "START");
            var remaining = ecc.Elements(ns + "ECTransition")
                .Where(t => (string?)t.Attribute("Source") == "START")
                .Select(t => $"START->{(string?)t.Attribute("Destination")} [{(string?)t.Attribute("Condition")}]")
                .ToList();

            result.PatchesApplied.Add(
                $"ProcessRuntime_Generic_v1: removed {bypass.Count} START->IDLE1 bypass " +
                $"transition(s); START now has {startOutgoingAfter} outgoing: " +
                string.Join(" ; ", remaining));
            MapperLogger.Info(
                $"[Deploy] ProcessRuntime_Generic_v1.fbt: removed START->IDLE1 'Mode=1 AND CycleType<>0' " +
                $"bypass; START outgoing transitions now = {startOutgoingAfter} ({string.Join(" ; ", remaining)})");
        }

        /// <summary>
        /// Pin CurrentStep at the END row's index by making EndSequence a
        /// no-op on the step pointer. The shipped EndSequence is implemented
        /// identically to AdvanceStep — it executes
        ///   CurrentStep := NextStep[CurrentStep];
        ///   CurrentStepType := StepType[CurrentStep];
        /// which, combined with the END -> END dead-end self-loop we add in
        /// PatchProcessRuntimeEccDeadEnd, walks CurrentStep through the
        /// recipe forever once END is reached (the END row's NextStep is 0,
        /// so the first re-entry wraps CurrentStep back to 0 and every
        /// subsequent state_change tick advances it again). The Watch then
        /// shows CurrentStep cycling and CurrentStepType flipping between
        /// recipe values even though no command is reissued (END has no
        /// transition back to ISSUE_CMD). Replacing the EndSequence body
        /// with a no-op preserves CurrentStep and CurrentStepType so END
        /// renders as a stable resting state in the Watch. Idempotent via
        /// a marker comment; emits the patch as a CDATA section so it
        /// matches the surrounding Algorithm bodies in the FBT.
        /// </summary>
        static void PatchProcessRuntimeEndSequenceNoOp(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var alg = root.Descendants(ns + "Algorithm")
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "EndSequence");
            if (alg == null)
            {
                result.Warnings.Add(
                    "ProcessRuntime_Generic_v1.fbt: Algorithm EndSequence not found; END no-op patch skipped.");
                return;
            }

            var st = alg.Element(ns + "ST");
            if (st == null)
            {
                result.Warnings.Add(
                    "ProcessRuntime_Generic_v1.fbt: EndSequence has no <ST>; END no-op patch skipped.");
                return;
            }

            const string noOpMarker = "(* END no-op: CurrentStep pinned *)";
            if ((st.Value ?? string.Empty).Contains(noOpMarker, StringComparison.Ordinal))
            {
                // Already patched — idempotent no-op.
                result.PatchesApplied.Add(
                    "ProcessRuntime_Generic_v1: EndSequence no-op already in place (CurrentStep pinned)");
                return;
            }

            string noOpBody =
                noOpMarker + "\r\n" +
                "PreviousStepText := ThisStepText;\r\n" +
                "ThisStepText := 'Resting in END';\r\n" +
                "NextStepText := 'Recipe complete';";

            st.ReplaceNodes(new System.Xml.Linq.XCData(noOpBody));

            doc.Save(fbtPath);
            result.PatchesApplied.Add(
                "ProcessRuntime_Generic_v1: EndSequence replaced with no-op (CurrentStep pinned at END row, stops Watch cycling)");
            MapperLogger.Info(
                "[Deploy] Patched ProcessRuntime_Generic_v1.fbt EndSequence (no-op so CurrentStep stays at the END row once reached)");
        }

        static void PatchProcessRuntimeEngine(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var initEvent = root.Descendants(ns + "Event")
                .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
            var inputVars = root.Descendants(ns + "InputVars").FirstOrDefault();
            var internalVars = root.Descendants(ns + "InternalVars").FirstOrDefault();
            var initAlgo = root.Descendants(ns + "Algorithm")
                .FirstOrDefault(a => (string?)a.Attribute("Name") == "initializeinit");

            if (initEvent == null || inputVars == null || initAlgo == null)
            {
                result.Warnings.Add("ProcessRuntime_Generic_v1.fbt: expected INIT event / InputVars / initializeinit not found; recipe-as-input patch skipped.");
                return;
            }

            var existingInputVarNames = new HashSet<string>(
                inputVars.Elements(ns + "VarDeclaration")
                    .Select(v => (string?)v.Attribute("Name") ?? string.Empty),
                StringComparer.Ordinal);

            // Idempotency check: if every recipe array is already an InputVar, this FBT was patched before.
            if (RecipeArrayDecls.All(d => existingInputVarNames.Contains(d.Name)))
                return;

            // 1. Promote recipe arrays from InternalVars to InputVars (move if present, else add fresh).
            foreach (var decl in RecipeArrayDecls)
            {
                var existingInternal = internalVars?
                    .Elements(ns + "VarDeclaration")
                    .FirstOrDefault(v => (string?)v.Attribute("Name") == decl.Name);
                existingInternal?.Remove();

                if (!existingInputVarNames.Contains(decl.Name))
                {
                    var newDecl = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                        new System.Xml.Linq.XAttribute("Name", decl.Name),
                        new System.Xml.Linq.XAttribute("Type", decl.Type),
                        new System.Xml.Linq.XAttribute("ArraySize", decl.ArraySize));
                    if (decl.Comment != null)
                        newDecl.Add(new System.Xml.Linq.XAttribute("Comment", decl.Comment));
                    inputVars.Add(newDecl);
                }
            }

            // 2. Add <With Var=...> entries on INIT for the new arrays (skip duplicates).
            var existingWithVars = new HashSet<string>(
                initEvent.Elements(ns + "With")
                    .Select(w => (string?)w.Attribute("Var") ?? string.Empty),
                StringComparer.Ordinal);
            foreach (var decl in RecipeArrayDecls)
            {
                if (existingWithVars.Contains(decl.Name)) continue;
                initEvent.Add(new System.Xml.Linq.XElement(ns + "With",
                    new System.Xml.Linq.XAttribute("Var", decl.Name)));
            }

            // 3. Strip the recipe-population block from initializeinit. The replacement only
            //    initialises engine state — recipe values now arrive via InputVars.
            var stElement = initAlgo.Descendants(ns + "ST").FirstOrDefault();
            if (stElement != null)
            {
                stElement.RemoveNodes();
                stElement.Add(new System.Xml.Linq.XCData(
                    "CurrentStep := 0;\n" +
                    "CurrentStepType := 0;\n" +
                    "WaitSatisfied := FALSE;\n\n" +
                    // PusherID := 0; removed 2026-05-26 — leftover from the
                    // pre-recipe Pusher-id scheme. The variable was never (or no
                    // longer) declared on ProcessRuntime_Generic_v1, so EAE
                    // raised ERR_NO_SUCH_VAR in initializeinit and the project
                    // failed to compile, leaving the runtime offline (Pusher
                    // would not actuate). Per-actuator state lookups now go
                    // through Wait1Id / state_table indexing, not a single
                    // PusherID register, so the assignment is redundant.
                    "cmd_target_name := '';\n" +
                    "cmd_state := 0;\n\n" +
                    "PreviousStepText := '';\n" +
                    "ThisStepText := 'Initialised';\n" +
                    "NextStepText := '';"));
            }

            doc.Save(fbtPath);
            result.PatchesApplied.Add("ProcessRuntime_Generic_v1: recipe arrays InternalVars->InputVars; INIT With clauses added; initializeinit stripped of recipe ST");
            MapperLogger.Info("[Deploy] Patched ProcessRuntime_Generic_v1.fbt for recipe-as-input architecture");
        }

        static void PatchProcess1GenericComposite(string fbtPath, DeployResult result)
        {
            var doc = System.Xml.Linq.XDocument.Load(fbtPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var root = doc.Root!;
            System.Xml.Linq.XNamespace ns = root.GetDefaultNamespace();

            var initEvent = root.Descendants(ns + "Event")
                .FirstOrDefault(e => (string?)e.Attribute("Name") == "INIT");
            var inputVars = root.Descendants(ns + "InputVars").FirstOrDefault();
            var fbNetwork = root.Descendants(ns + "FBNetwork").FirstOrDefault();
            var dataConnections = fbNetwork?.Descendants(ns + "DataConnections").FirstOrDefault();

            if (initEvent == null || inputVars == null || fbNetwork == null || dataConnections == null)
            {
                result.Warnings.Add("Process1_Generic.fbt: expected INIT event / InputVars / FBNetwork / DataConnections not found; recipe-as-input patch skipped.");
                return;
            }

            var existingInputVarNames = new HashSet<string>(
                inputVars.Elements(ns + "VarDeclaration")
                    .Select(v => (string?)v.Attribute("Name") ?? string.Empty),
                StringComparer.Ordinal);

            // Idempotency check.
            if (RecipeArrayDecls.All(d => existingInputVarNames.Contains(d.Name)))
                return;

            // 1. Add 6 InputVars on the outer composite.
            foreach (var decl in RecipeArrayDecls)
            {
                if (existingInputVarNames.Contains(decl.Name)) continue;
                var newDecl = new System.Xml.Linq.XElement(ns + "VarDeclaration",
                    new System.Xml.Linq.XAttribute("Name", decl.Name),
                    new System.Xml.Linq.XAttribute("Type", decl.Type),
                    new System.Xml.Linq.XAttribute("ArraySize", decl.ArraySize));
                if (decl.Comment != null)
                    newDecl.Add(new System.Xml.Linq.XAttribute("Comment", decl.Comment));
                inputVars.Add(newDecl);
            }

            // 2. <With Var=...> on INIT.
            var existingWithVars = new HashSet<string>(
                initEvent.Elements(ns + "With")
                    .Select(w => (string?)w.Attribute("Var") ?? string.Empty),
                StringComparer.Ordinal);
            foreach (var decl in RecipeArrayDecls)
            {
                if (existingWithVars.Contains(decl.Name)) continue;
                initEvent.Add(new System.Xml.Linq.XElement(ns + "With",
                    new System.Xml.Linq.XAttribute("Var", decl.Name)));
            }

            // 3. Graphic <Input Name="..." Type="Data"/> stubs inside FBNetwork (sibling of FBs and connections).
            var existingInputStubs = new HashSet<string>(
                fbNetwork.Elements(ns + "Input")
                    .Select(i => (string?)i.Attribute("Name") ?? string.Empty),
                StringComparer.Ordinal);
            int stubY = 3200;
            foreach (var decl in RecipeArrayDecls)
            {
                if (existingInputStubs.Contains(decl.Name)) { stubY += 100; continue; }
                fbNetwork.Add(new System.Xml.Linq.XElement(ns + "Input",
                    new System.Xml.Linq.XAttribute("Name", decl.Name),
                    new System.Xml.Linq.XAttribute("x", "200"),
                    new System.Xml.Linq.XAttribute("y", stubY.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new System.Xml.Linq.XAttribute("Type", "Data")));
                stubY += 100;
            }

            // 4. DataConnections: outer-input -> ProcessEngine.<input>.
            var existingDataConns = new HashSet<(string, string)>(
                dataConnections.Elements(ns + "Connection")
                    .Select(c => ((string?)c.Attribute("Source") ?? string.Empty,
                                  (string?)c.Attribute("Destination") ?? string.Empty)));
            foreach (var decl in RecipeArrayDecls)
            {
                var src = decl.Name;
                var dst = "ProcessEngine." + decl.Name;
                if (existingDataConns.Contains((src, dst))) continue;
                dataConnections.Add(new System.Xml.Linq.XElement(ns + "Connection",
                    new System.Xml.Linq.XAttribute("Source", src),
                    new System.Xml.Linq.XAttribute("Destination", dst)));
            }

            doc.Save(fbtPath);
            result.PatchesApplied.Add("Process1_Generic: 6 array InputVars + INIT With clauses + Input stubs + DataConnections to ProcessEngine");
            MapperLogger.Info("[Deploy] Patched Process1_Generic.fbt for recipe-as-input architecture");
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

        // Retire an FB type we no longer ship (e.g. the simulator-only SimCentreHomeSensor_7SCH):
        // delete its top-level .fbt/.doc.xml/.meta.xml/.Basic.export and strip its dfbproj entries so
        // EAE shows no dangling Missing Project Files. Idempotent — a no-op once the type is gone.
        static void SweepRetiredType(string eaeProjectDir, string typeName, DeployResult result)
        {
            try
            {
                var iec = Path.Combine(eaeProjectDir, "IEC61499");
                int filesGone = 0;
                foreach (var p in new[]
                {
                    Path.Combine(iec, typeName + ".fbt"),
                    Path.Combine(iec, typeName + ".doc.xml"),
                    Path.Combine(iec, typeName + ".meta.xml"),
                    Path.Combine(eaeProjectDir, typeName + ".Basic.export"),
                })
                    if (File.Exists(p)) { File.Delete(p); filesGone++; }

                var dfbproj = Path.Combine(iec, "IEC61499.dfbproj");
                int entriesGone = 0;
                if (File.Exists(dfbproj))
                {
                    var doc = System.Xml.Linq.XDocument.Load(dfbproj, System.Xml.Linq.LoadOptions.PreserveWhitespace);
                    foreach (var el in doc.Descendants()
                        .Where(e => (e.Name.LocalName == "Compile" || e.Name.LocalName == "None")
                            && ((string?)e.Attribute("Include"))?.StartsWith(typeName + ".", StringComparison.Ordinal) == true)
                        .ToList())
                    { el.Remove(); entriesGone++; }
                    if (entriesGone > 0) doc.Save(dfbproj);
                }
                if (filesGone > 0 || entriesGone > 0)
                    result.PatchesApplied.Add($"retired {typeName}: {filesGone} file(s) + {entriesGone} dfbproj entry(ies) removed");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"retire {typeName} failed: {ex.Message}");
            }
        }

        static void DeployArtifact(string libPath, string subfolder, string name,
            string eaeProjectDir, DeployResult result, bool isBasic, bool isCat = false)
        {
            var folder = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(folder))
            {
                result.Warnings.Add($"Library subfolder missing: {subfolder}");
                return;
            }

            var zipPath = FindArtifactZip(folder, name);
            if (zipPath != null)
            {
                ExtractToEae(zipPath, eaeProjectDir, result);
            }
            else
            {
                var dirPath = FindArtifactDir(folder, name);
                if (dirPath != null)
                {
                    CopyDirToEae(dirPath, eaeProjectDir, result);
                }
                else
                {
                    result.Warnings.Add($"Artifact not found: {subfolder}/{name}");
                    return;
                }
            }

            if (isCat) result.CATsDeployed.Add(name);
            else if (string.Equals(subfolder, "Adapter", StringComparison.OrdinalIgnoreCase))
                result.AdaptersDeployed.Add(name);
            else if (string.Equals(subfolder, "Composite", StringComparison.OrdinalIgnoreCase))
                result.CompositesDeployed.Add(name);
            else if (isBasic) result.BasicFBsDeployed.Add(name);
        }

        static string? FindArtifactZip(string folder, string name)
        {
            // ".subcats.zip" files are wrapper bundles (a zip whose only entry
            // is the real .cat.zip) — NEVER a directly-deployable artifact.
            // Extracting one leaves the CAT folder uncreated while the dfbproj
            // still registers <name>\<name>.cfg, so EAE dies with
            // "Could not find a part of the path …\<name>.cfg". Exclude them.
            //
            // Then order candidates by filename DESCENDING so that when
            // several dated versions coexist (e.g. right after a template
            // library update) the NEWEST is chosen deterministically instead
            // of relying on Directory.GetFiles' filesystem/alphabetical order
            // (which silently picked the wrong zip after the old .cat.zip was
            // moved aside).
            var zips = Directory.GetFiles(folder, "*.zip")
                .Where(f => !Path.GetFileName(f)
                    .Contains(".subcats.", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var f in zips)
            {
                var fn = Path.GetFileName(f);
                if (fn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    fn.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fn, name + ".zip", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            foreach (var f in zips)
            {
                if (Path.GetFileName(f).Contains(name + ".", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            return null;
        }

        static string? FindArtifactDir(string folder, string name)
        {
            foreach (var d in Directory.GetDirectories(folder))
            {
                var dn = Path.GetFileName(d);
                if (dn.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dn, name, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        static void CopyDirToEae(string sourceDir, string eaeProjectDir, DeployResult result)
        {
            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var parts = rel.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    rel = string.Join("/", parts.Skip(1));

                var targetPath = Path.Combine(eaeProjectDir, rel);
                var targetDir = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                if (!File.Exists(targetPath))
                {
                    File.Copy(file, targetPath);
                    result.FilesExtracted++;
                }
                else
                {
                    result.FilesSkipped++;
                }
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

        static void ExtractToEae(string zipPath, string eaeProjectDir, DeployResult result)
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };
            string? prefixToStrip = null;

            var firstFile = zip.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
            if (firstFile != null)
            {
                var parts = firstFile.FullName.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    prefixToStrip = parts[0] + "/";
            }

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var relativePath = entry.FullName;
                if (prefixToStrip != null && relativePath.StartsWith(prefixToStrip, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(prefixToStrip.Length);

                var targetPath = Path.Combine(eaeProjectDir, relativePath);
                var targetDir = Path.GetDirectoryName(targetPath)!;

                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                if (!File.Exists(targetPath))
                {
                    entry.ExtractToFile(targetPath);
                    result.FilesExtracted++;
                }
                else
                {
                    result.FilesSkipped++;
                }
            }
        }

        static void GenerateCfgFiles(string eaeProjectDir, DeployResult result)
        {
            var iec61499Dir = Path.Combine(eaeProjectDir, "IEC61499");
            foreach (var cat in result.CATsDeployed)
            {
                var catDir = Path.Combine(iec61499Dir, cat);
                var cfgPath = Path.Combine(catDir, $"{cat}.cfg");
                if (File.Exists(cfgPath)) continue;
                if (!Directory.Exists(catDir)) continue;

                var hmi = cat + "_HMI";
                var cfg = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<CAT xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" Name=""{cat}"" CATFile=""{cat}\{cat}.fbt"" SymbolDefFile=""..\HMI\{cat}\{cat}.def.cs"" SymbolEventFile=""..\HMI\{cat}\{cat}.event.cs"" DesignFile=""..\HMI\{cat}\{cat}.Design.resx"" xmlns=""http://www.nxtcontrol.com/IEC61499.xsd"">
  <HMIInterface Name=""IThis"" FileName=""{cat}\{hmi}.fbt"" UsedInCAT=""true"" Usage=""Private"">
    <Symbol Name=""sDefault"" FileName=""..\HMI\{cat}\{cat}_sDefault.cnv.cs"">
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.Designer.cs</DependentFiles>
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.resx</DependentFiles>
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.xml</DependentFiles>
    </Symbol>
  </HMIInterface>
  <Plugin Name=""Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{cat}_CAT.offline.xml"" />
  <Plugin Name=""Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{cat}_CAT.opcua.xml"" />
  <Plugin Name=""Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{hmi}.offline.xml"" />
  <Plugin Name=""Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{hmi}.opcua.xml"" />
  <HWConfiguration xsi:nil=""true"" />
</CAT>";
                File.WriteAllText(cfgPath, cfg);
                result.FilesExtracted++;
                MapperLogger.Info($"[Deploy] Generated {cat}.cfg");

                var metaPath = Path.Combine(catDir, $"{hmi}.meta.xml");
                if (!File.Exists(metaPath))
                {
                    File.WriteAllBytes(metaPath, Array.Empty<byte>());
                    result.FilesExtracted++;
                    MapperLogger.Info($"[Deploy] Created empty {hmi}.meta.xml placeholder");
                }
            }
        }

        static void RegisterInDfbproj(string eaeProjectDir, DeployResult result)
        {
            var iec61499Dir = Path.Combine(eaeProjectDir, "IEC61499");
            if (!Directory.Exists(iec61499Dir)) return;

            var dfbproj = Directory.GetFiles(iec61499Dir, "*.dfbproj").FirstOrDefault();
            if (dfbproj == null) return;

            int changed = 0;
            foreach (var cat in result.CATsDeployed)
                changed += DfbprojRegistrar.RegisterCat(dfbproj, cat);

            foreach (var basic in result.BasicFBsDeployed)
                changed += DfbprojRegistrar.RegisterBasicFb(dfbproj, basic + ".fbt", "Basic");

            foreach (var adapter in result.AdaptersDeployed)
                changed += DfbprojRegistrar.RegisterBasicFb(dfbproj, adapter + ".adp", "Adapter");

            foreach (var composite in result.CompositesDeployed)
                changed += DfbprojRegistrar.RegisterBasicFb(dfbproj, composite + ".fbt", "Composite");

            foreach (var dt in result.DataTypesDeployed)
                changed += DfbprojRegistrar.RegisterDataType(dfbproj, $@"DataType\{dt}.dt");

            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.DPAC",   "24.1.0.33");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppBase", "24.1.0.21");
            // SE.IoTMx — TM3 module type library used by the M262's .hcf
            //   (BMTM3 + BMTM3DDM16025_DI8_DO16 + TM3DXxxx). Without this
            //   reference EAE shows the M262 Hardware Configurator empty.
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoTMx",   "24.1.0.19");
            // SE.IoX80 — X80 module type library used by the M580's .hcf
            //   (BMXBUS + BMEXBP0400 rack + BMXCPS2010 PSU + BMED581020 CPU +
            //   BMXDDM16025 DI8/DO8 mixed-IO modules). Without this reference
            //   EAE refuses to import the M580 .hcf with the error:
            //     "Unable to import selected HW configuration: The following
            //      reference(s) is missing: 1. SE.IoX80"
            //   and the M580_RES Hardware Configurator stays empty. The
            //   library ships with EAE 24.1 under
            //   C:\ProgramData\Schneider Electric\Libraries\SE.IoX80-24.1.0.19;
            //   we just have to declare the reference here so EAE wires it
            //   into the .dfbproj's compile path.
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoX80",   "24.1.0.19");

            // BX1 PHYSICAL-DEVICE LIBRARIES (2026-06-08, the actual topology-import fix).
            // EAE's topology server resolves every Equipment's catalogReference
            // (HMIB1X_V01.00_01.00, GenericEthernetIPFieldDevice_V01.00_01.00, …)
            // against the solution's <Reference> libraries. The Demonstrator declared
            // only 6 libraries; the reference SMC_Rig_Expo_withClamp declares 22 —
            // and the BX1 HMIB1X + EtherNet/IP catalog types live in the MISSING ones
            // (SE.HwCommon / SE.FieldDevice / SE.IoNet / Standard.IoEtherNetIP). With
            // the library unreferenced, importing an HMIB1X equipment fails the WHOLE
            // topology with "Unable to import topology / verify file format / Internal
            // Server Error" (the Workstation form imported only because its catalog WAS
            // in a referenced library). Declaring the full reference set here makes the
            // Demonstrator's library references identical to the working reference, so
            // every catalog type resolves. Versions match the reference's exactly; all
            // ship with EAE 24.1 (the reference uses them). Idempotent — existing refs
            // are left alone. Also clears the log's "SE.HwCommon.SchneiderGreen not
            // found" and the "SeqHeads/PhaseConnects not found" (SE.AppSequence) noise.
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.HwCommon",                  "24.1.0.19");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.FieldDevice",               "24.1.0.31");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoNet",                     "24.1.0.11");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "Standard.IoEtherNetIP",        "24.1.0.27");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.IoATV",                     "24.1.0.26");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.ModbusGateway",             "24.1.0.17");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "Standard.IoModbus",            "24.1.0.32");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "Standard.IoModbusSlave",       "24.1.0.25");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "Standard.OPCUAClient",         "24.1.0.8");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppCommonProcess",          "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppConveying",              "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppSequence",               "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppStateManagement",        "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppLiquidFood",             "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppSingleLinePowerMonitoring", "24.1.0.21");
            changed += DfbprojRegistrar.RegisterReference(dfbproj, "SE.AppWWW",                    "24.1.0.21");

            changed += DfbprojRegistrar.SweepIec61499Folder(dfbproj, iec61499Dir);

            // Only bump the mtime if registration actually changed the project.
            // Each Register* now no-ops its save when nothing was added, so on an
            // idempotent re-run this whole method writes NOTHING and EAE gets no
            // spurious "Reload Solution" prompt from the deploy phase. The single
            // intended reload trigger is MainForm.TouchDfbprojToTriggerEaeReload,
            // fired once at the very end of the run.
            if (changed > 0)
            {
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Info($"[Deploy] dfbproj updated ({changed} entr(y/ies)): {Path.GetFileName(dfbproj)}");
            }
            else
            {
                MapperLogger.Info($"[Deploy] dfbproj already up to date; no write: {Path.GetFileName(dfbproj)}");
            }
        }

        static string ReadSysdevId(string sysdevPath)
        {
            if (string.IsNullOrEmpty(sysdevPath) || !File.Exists(sysdevPath)) return string.Empty;
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(sysdevPath);
                return (string?)doc.Root?.Attribute("ID") ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        static string? DeriveEaeProjectDir(MapperConfig cfg)
        {
            var syslayPath = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(syslayPath)) return null;

            var dir = Path.GetDirectoryName(syslayPath);
            while (dir != null)
            {
                var parent = Path.GetDirectoryName(dir);
                if (parent != null && Directory.Exists(Path.Combine(dir, "..")))
                {
                    var iec = Path.Combine(dir);
                    var checkDir = dir;
                    while (checkDir != null)
                    {
                        if (Directory.GetFiles(checkDir, "*.dfbproj").Any())
                            return Path.GetDirectoryName(checkDir);
                        checkDir = Path.GetDirectoryName(checkDir);
                    }
                }
                dir = Path.GetDirectoryName(dir);
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
