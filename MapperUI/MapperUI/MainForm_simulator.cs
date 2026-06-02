// MainForm_simulator.cs  --  SIMULATOR ("Test Simulator" button) ONLY.
// Split out of MainForm.cs so the simulator pipeline cannot be confused with
// the hardware "Test Runtime" (old "Test Feed Station") path, which stays in
// MainForm.cs and is intentionally left untouched.
//
// SENSOR MODEL (read before "fixing" this). The actuator position sensors are
// held NOT fitted (WorkSensorFitted=FALSE / HomeSensorFitted=FALSE) on
// purpose. Forcing the sensors permanently TRUE deadlocks the
// FiveStateActuator ECC: its INIT state only leaves via (atwork = FALSE) to
// AtHomeInit or (atwork = TRUE AND athome = FALSE) to AtWork, so both sensors
// TRUE strands every actuator in INIT forever, and any static single sensor
// force either deadlocks ToWork to AtWork (which needs atwork TRUE arriving
// in sequence) or skips the motion command entirely. With the sensors not
// fitted the embedded No_Sensor_Handler synthesises atwork / athome from
// toWorkTime / toHomeTime in the correct mutually exclusive sequence, while
// toWorkInterlock=FALSE / toHomeInterlock=FALSE still gate the START of
// motion, so the interlock rule arrays are still fully exercised in
// simulation. This is the only model that both walks and proves the
// interlocks. PartInHopper is held TRUE for the whole run via the injected
// SimHopperForce publisher.
using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Models;
using CodeGen.Devices.M262;
using CodeGen.Devices.M580;
using CodeGen.Translation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MapperUI
{
    public partial class MainForm
    {
        // ── Test Station 1 Pusher-Simulator ─────────────────────────────────
        // Identical to btnTestStation1_Click (Button 2): SAME Demonstrator
        // project, SAME artefacts, SAME pipeline. The ONLY difference is one
        // post-step — flip the deployed .sysres Resource Type
        // EMB_RES_ECO -> SIM_RES so EAE runs it on the software simulator
        // instead of the M262 hardware runtime. No separate DemonstratorSim
        // folder, no path swap, no .sln launching. Re-run Button 2 to return
        // the project to hardware mode.
        async void btnGenerateFullSystemSimulator_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_loadedControlXmlPath) || !File.Exists(_loadedControlXmlPath))
                { ShowError("Load a Control.xml first via Browse."); return; }
                if (!TryResolveDemonstratorPath(out var syslayPath)) return;
                if (!EnsureM262SysdevExistsOrAbort()) return;

                // Flip the cfg into simulator-full-system mode so the
                // downstream pipeline (SystemLayoutInjector / Station2DeviceEmitter
                // / ProcessRecipeArrayGenerator) collapses every Control.xml
                // Process into one SIM resource. The hardware path (Button 2 /
                // btnTestStation1) leaves this FALSE so Feed Station emits
                // byte-identical to today's working output.
                Cfg().SimulatorFullSystem = true;

                lblStatus.Text = "Generating (Simulator)...";
                AppendActivity($"[Test Full-System Simulator] Generating into Demonstrator at {syslayPath} — SIMULATOR FULL-SYSTEM mode...");
                AppendActivity(
                    "[Simulator] Full-system collapse: Feed_Station + Assembly_Station + Disassembly " +
                    "all emit as Process_Generic FBs in one syslay SubAppNetwork. M262, M580 RES0 and " +
                    "BX1_RES are each emitted as full per-PLC EMB_RES_ECO resources (sysdev + sysres + " +
                    "HCF + wiring), with the syslay FBs bucketed by PLC into the right .sysres. EAE's " +
                    "\"Local Test\" Active Network Profile then simulates each PLC locally instead of " +
                    "downloading to hardware. Cross-process handshakes wired Process[i].state_update → " +
                    "Process[j].state_change directly (no PLC_RW gateway, no SIFB).");
                AppendActivity(
                    "[Simulator][KnownLimitation] Per-Process scoped ID registry (today): each Process's " +
                    "Wait1Id values reference its own actuator/sensor ordering. Cross-process Wait1Id " +
                    "references (e.g. Assembly waiting on Feed_Station/TransferReturned) get logged as " +
                    "'out of scope' by ProcessRecipeArrayGenerator.ClassifyState and dropped. The direct " +
                    "Process state_update→state_change event wires fire at the canvas level, but " +
                    "downstream gating still depends on intra-Process conditions surviving. If the " +
                    "simulator stalls at a Process boundary, the next iteration is a global sensors-first " +
                    "ID scheme so cross-process actuator/sensor references resolve.");

                await DeployUniversalTemplatesAsync();

                // NOTE: CompileCachePurger.Purge intentionally NOT called here.
                // Adding it forced EAE to recompile from current source, and
                // the fresh compile applied STRICT TLS validation on the
                // MQTT_CONNECTION FB (ValidateCert='Server certificate and
                // hostname' default → ReturnCode=100, IsConnected=FALSE
                // against a plain mosquitto on port 1883). The pre-purge
                // cached compile had looser validation and the broker
                // connection opened (with WARNING but functional). Until
                // mosquitto is set up with a self-signed cert or the right
                // ValidateCert-skip enum value is found, the cache must stay
                // so the MQTT runtime keeps connecting. The rig path
                // (MainForm.btnTestStation1_Click ~line 513) does call
                // CompileCachePurger.Purge — leave that alone, rig is parked.

                var injector = new SystemInjector();
                var cleanup = await Task.Run(() => injector.PrepareDemonstratorForGeneration(Cfg()));
                LogCleanup(cleanup);

                var bindings = TryLoadBindings();
                SystemInjector.BindingApplicationReport report = null!;
                var path = await Task.Run(() =>
                    injector.GenerateStation1TestSyslay(Cfg(), _loadedControlXmlPath, bindings, out report));
                LogBindingsReport(report);
                await FinalizeM262StackAsync();

                int wireCountBefore = report.Missing.Count;
                await Task.Run(() => M262SysresWireEmitter.Emit(Cfg(), report));
                for (int i = wireCountBefore; i < report.Missing.Count; i++)
                {
                    var line = report.Missing[i];
                    if (line.StartsWith("[Wire]") || line.StartsWith("[Sysres"))
                        AppendActivity(line);
                }

                // Station 2 (M580 + BX1) wire emit — the equivalent of
                // M262SysresWireEmitter for the per-PLC resources. Without
                // this, FinalizeM262StackAsync mirrors the Station 2 FBs onto
                // M580 RES0 and BX1_RES via Station2SysresMirror but never
                // wires them, so each resource ships with FBs and zero
                // Connections. EAE then deploys both resources as half-empty
                // shells — exactly the "All wiring has gone for M580 RES0"
                // failure mode. The rig path (MainForm.btnTestStation1_Click
                // ~line 903) has always called this; the sim path was missing
                // it. In sim mode the three resources still exist independently
                // — collapsing into a single SIM resource is just a syslay
                // convenience; EAE deploys per-PLC with the "Local Test"
                // Active Network Profile picking the simulator runtime.
                int s2WireCountBefore = report.Missing.Count;
                await Task.Run(() =>
                    CodeGen.Devices.Core.Station2WireEmitter.EmitStation2Resources(Cfg(), report));
                for (int i = s2WireCountBefore; i < report.Missing.Count; i++)
                {
                    var line = report.Missing[i];
                    if (line.StartsWith("[Wire][M580]") || line.StartsWith("[M580]") ||
                        line.StartsWith("[Wire][BX1]") || line.StartsWith("[BX1]") ||
                        line.StartsWith("[Wire][Stn2]"))
                        AppendActivity(line);
                }

                // SIMULATOR: BYPASS the hardware-config (.hcf) entirely. In "Local Test"
                // the PLCs are simulated — there is no physical IO — and the M580/M262/BX1
                // .hcf (physical racks/modules + channel bindings) makes EAE's hardware-
                // config build FAIL in sim mode. The simulator never reads physical IO
                // (every sensor is synthesized via SimHopperForce / SimSwivelForce /
                // No_Sensor_Handler timers), so the .hcf is dead weight here. Strip every
                // deployed .hcf instead of patching it (was: HcfPatchService.PatchDeployed,
                // which is correct only for the rig). Rig path keeps its HCF flow.
                int strippedHcf = await Task.Run(() =>
                    CodeGen.Services.SimulatorPostProcessor.StripHardwareConfigForSimulator(path));
                AppendActivity($"[Simulator] Hardware-config bypass: removed {strippedHcf} .hcf file(s) " +
                    "(M580/M262/BX1) — sim has no physical IO; sensors are synthesized via " +
                    "SimHopperForce / SimSwivelForce / no-sensor timers.");

                // Topology self-consistency guard. The sim path does NOT run
                // Station2DeviceEmitter / BroadcastDomainEmitter (those are
                // rig-only), so the Topology folder is whatever's left on disk
                // — including any DeviceNetwork the user created in EAE's
                // Logical Networks Editor that got a UUID but was never saved
                // as a BroadcastDomain file. An Equipment that binds to such a
                // dangling domain UUID makes EAE reject the whole topology on
                // import ("Unable to import topology / Internal Server Error").
                // EnsureReferencedDomains scans the Equipment JSONs and creates
                // any referenced-but-missing BroadcastDomain at 192.168.1.0/24.
                // Writes ONLY broadcast-domain JSON — never devices/sysres/
                // trust — so it cannot disturb the rig binding.
                try
                {
                    var bd = await Task.Run(() =>
                        CodeGen.Devices.Core.BroadcastDomainEmitter.EnsureReferencedDomains(Cfg()));
                    foreach (var f in bd.FilesWritten) AppendActivity($"[Topology] {f}");
                    foreach (var w in bd.Warnings) AppendActivity($"[Topology] {w}");
                }
                catch (Exception ex)
                {
                    AppendActivity($"[Topology][Error] domain consistency: {ex.Message}");
                }

                // No .sysres Resource Type flip. EAE has no SIM_RES type
                // (verified: ERR_NO_SUCH_TYPE on Runtime.Management.SIM_RES).
                // Resource stays EMB_RES_ECO; simulator mode = the
                // "Local Test" Active Network Profile picked in EAE.
                //
                // ORDER IS LOAD-BEARING (Defect 1 fix). InjectSimHopperForce
                // re-loads and re-saves BOTH the syslay and the sysres to add
                // the SimHopperForce FB + wires. When the no-sensor override
                // ran BEFORE it, that second save round-tripped the file and
                // EAE Watch came up with WorkSensorFitted=TRUE again (the
                // on-disk syslay still read FALSE, but the sysres EAE compiles
                // had been re-emitted from a pre-override state). The override
                // MUST be the LAST writer of the syslay + sysres so nothing
                // can overwrite it. So: SimHopperForce first, no-sensor
                // override last, then a post-write re-read assertion.

                // Simulator-only post-process: inject a SimHopperForce
                // SYMLINKMULTIVARSRC that publishes 'PartInHopper.Input' =
                // TRUE on Area.INITO, so the hopper sensor reads TRUE at
                // startup and the recipe ring advances without physical I/O.
                // syslay + sysres only, simulator button only.
                int simFb = InjectSimHopperForce(path, Cfg());
                if (simFb > 0)
                {
                    AppendActivity("Simulator hopper forced TRUE via SimHopperForce SYMLINKMULTIVARSRC");
                    AppendActivity("[Simulator][InitSeq] FB1.INITO -> SimHopperForce.INIT -> Area.INIT -> Station1 " +
                        "-> PartInHopper.INIT (FB2 subscribes) -> PartInHopper.INITO -> { Feeder.INIT (chain) + " +
                        "SimHopperForce.REQ (one-shot publish) } — publish lands AFTER FB2 subscribed; FB2.CNF fires, " +
                        "state_table[0]=PartInHopper");
                }
                else
                    AppendActivity("[Simulator][Warn] SimHopperForce not injected (already present or canvas not resolvable).");

                // Simulator-only post-process, LAST writer: in pure sim there
                // is no physical cylinder/sensor, so force WorkSensorFitted=
                // FALSE and HomeSensorFitted=FALSE on every Five_State_
                // Actuator_CAT instance — the actuator's internal
                // No_Sensor_Handler timer path then self-advances the ECC
                // (1->2 after toWorkTime, 3->4 after toHomeTime) with no
                // external sensor. Button 2 (hardware) NEVER calls this; the
                // rig's real sensors close the loop with the Control.xml-
                // derived TRUE values. Runs AFTER InjectSimHopperForce so it
                // is the final write of the syslay + sysres.
                int simNoSensor = OverrideSimActuatorsNoSensor(path, Cfg());
                AppendActivity(simNoSensor > 0
                    ? $"[Simulator] No-sensor mode: WorkSensorFitted/HomeSensorFitted forced FALSE on {simNoSensor} " +
                      "Five_State_Actuator_CAT instance(s) (Feeder/Checker/Transfer) — ECC self-advances via timer"
                    : "[Simulator][Warn] No-sensor override touched 0 actuators (none found or sim syslay missing).");

                // Simulator-only sensor synthesis for the 3-position Seven_State swivel.
                // No-op when StubSevenStateActuatorsAsFiveState=true (no Seven_State
                // instances in the SIM syslay then). When the stub is off and Bearing_PnP
                // deploys as Seven_State_Actuator_CAT, this injects one
                // SimSwivelForce_<name> SYMLINKMULTIVARSRC per Seven instance publishing
                // atwork1/atwork2 from the actuator's own coil-drive outputs — so the
                // ECC's ToPick → AtPick / ToPlace → AtPlace gates close in sim without
                // physical sensors. See SimulatorPostProcessor.InjectSimSwivelForce
                // header for the wiring contract.
                int swivelFb = CodeGen.Services.SimulatorPostProcessor.InjectSimSwivelForce(
                    path, Cfg(), AppendActivity);
                if (swivelFb > 0)
                    AppendActivity($"[Simulator] Seven_State swivel sensor synthesis: {swivelFb} SimSwivelForce SYMLINKMULTIVARSRC(s) wired across syslay+sysres");

                // Defensive post-write check — mirrors VerifySimActuatorsNoSensorOrAbort
                // for Seven_State. Re-reads syslay + sysres and asserts every
                // Seven_State_Actuator_CAT instance has its SimSwivelForce companion
                // present and correctly wired. Throws on any violation so a future
                // pipeline step that silently drops or mis-wires a SimSwivelForce
                // can't ship a sim build that stalls at ToPick. No-op-green when
                // there are no Seven instances (stub on).
                CodeGen.Services.SimulatorPostProcessor.VerifySimSwivelForceOrAbort(
                    path, Cfg(), AppendActivity);

                // Post-write verification (Defect 1). Re-read the on-disk
                // syslay AND sysres AFTER every generator step has run and
                // assert WorkSensorFitted="FALSE" / HomeSensorFitted="FALSE"
                // on every Five_State_Actuator_CAT instance. The sysres is the
                // artefact EAE actually compiles, so a TRUE there is exactly
                // the "syslay shows FALSE but EAE Watch shows TRUE" symptom.
                // Any violation throws — the deploy aborts loudly rather than
                // shipping a sim build that stalls on a sensor that never
                // closes.
                VerifySimActuatorsNoSensorOrAbort(path, Cfg());
                DumpSimRecipeAndInterlockArrays(path);

                // EAE Solution Integrity needs an opcua.xml stub next to BOTH
                // the syslay AND the deployed sysres. GenerateFeedStationSyslayToPath
                // already emits one next to the syslay; this mirrors it next
                // to the sysres EAE actually compiles so the integrity dialog
                // (the one Alper hit with "Missing Project Files: opcua.xml")
                // doesn't fire on Reload Solution.
                try
                {
                    var sysresDirOpcua = Path.GetDirectoryName(Cfg().SysresPath2);
                    if (!string.IsNullOrEmpty(sysresDirOpcua) && Directory.Exists(sysresDirOpcua))
                    {
                        var sysresFileOpcua = Directory
                            .EnumerateFiles(sysresDirOpcua, "*.sysres", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault();
                        if (sysresFileOpcua != null)
                        {
                            SystemInjector
                                .EnsureOpcuaXmlBesideArtefact(sysresFileOpcua);
                            AppendActivity(
                                $"[Simulator][OPCUA] Emitted opcua.xml stubs beside syslay + sysres " +
                                $"({Path.GetFileName(sysresFileOpcua)}) — Solution Integrity check satisfied.");
                        }
                    }
                }
                catch (Exception opcuaEx)
                {
                    AppendActivity($"[Simulator][OPCUA][Warn] opcua.xml stub emit failed: {opcuaEx.Message}");
                }

                TouchDfbprojToTriggerEaeReload();

                AppendActivity($"Generated (Simulator): {path}");
                AppendActivity("[Simulator] Resource stays EMB_RES_ECO — EAE has no SIM_RES type. " +
                    "Simulator mode = the \"Local Test\" Active Network Profile, selected in EAE.");
                AppendActivity("[Simulator] In EAE: Reload Solution, set Active Network Profile to \"Local Test\", Deploy. " +
                    "After deploy, login and Watch Feed_Station.ProcessEngine CurrentStep / cmd_target_name / cmd_state / CMDREQ. " +
                    "Hopper is forced TRUE on INIT → PartInHopper fires CNF → StateHandling publishes state=1 id=0 on the ring " +
                    "→ ProcessHandler writes state_table[0]=1 → ProcessEngine leaves WAIT_STEP and fires CMDREQ " +
                    "cmd_target_name='feeder' cmd_state=1, proving the recipe arrays end to end.");
                lblStatus.Text = $"Ready (Simulator)  |  {path}  |  {report.Bound.Count} bound, {report.Missing.Count} unbound";
                MessageBox.Show(
                    $"Generated Test Feed Station Simulator into Demonstrator:\n{path}\n\n" +
                    $"{report.Bound.Count} bound, {report.Missing.Count} unbound.\n\n" +
                    "Hopper input is forced TRUE at startup via the injected SimHopperForce " +
                    "SYMLINKMULTIVARSRC (syslay + sysres). Resource stays EMB_RES_ECO — " +
                    "simulator mode is the \"Local Test\" Active Network Profile you pick in EAE.\n\n" +
                    "In EAE: Reload Solution, set Active Network Profile to \"Local Test\", Deploy. " +
                    "After deploy, login and Watch Feed_Station.ProcessEngine CurrentStep / " +
                    "cmd_target_name / cmd_state / CMDREQ — expect cmd_target_name='feeder', " +
                    "cmd_state=1 once the ring advances.",
                    "Test Feed Station Simulator", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendActivity($"[Simulator][Error] {ex}");
                lblStatus.Text = "Ready";
                ShowError(ex.Message);
            }
        }

        // -- Simulator post-processors --
        //
        // Extracted into CodeGen.Services.SimulatorPostProcessor (2026-05-29) so
        // MapperTests/SimulatorEndToEndHarness can call exactly the same code
        // MainForm calls — no replication drift between the GUI button and the
        // headless harness. The four wrappers below preserve the existing
        // private signatures so btnGenerateFullSystemSimulator_Click is byte-
        // identical to before, and route progress lines through AppendActivity.
        // SimSymlinkSrcType moved to SimulatorPostProcessor as a public const.

        int InjectSimHopperForce(string syslayPath, MapperConfig cfg) =>
            CodeGen.Services.SimulatorPostProcessor.InjectSimHopperForce(
                syslayPath, cfg, AppendActivity);

        int OverrideSimActuatorsNoSensor(string syslayPath, MapperConfig cfg) =>
            CodeGen.Services.SimulatorPostProcessor.OverrideSimActuatorsNoSensor(
                syslayPath, cfg, AppendActivity);

        void VerifySimActuatorsNoSensorOrAbort(string syslayPath, MapperConfig cfg) =>
            CodeGen.Services.SimulatorPostProcessor.VerifySimActuatorsNoSensorOrAbort(
                syslayPath, cfg, AppendActivity);

        void DumpSimRecipeAndInterlockArrays(string syslayPath) =>
            CodeGen.Services.SimulatorPostProcessor.DumpSimRecipeAndInterlockArrays(
                syslayPath, AppendActivity);

    }
}