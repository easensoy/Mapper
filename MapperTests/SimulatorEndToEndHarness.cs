// SimulatorEndToEndHarness.cs — headless harness driving the SAME simulator
// generation pipeline as MainForm_simulator.btnGenerateFullSystemSimulator_Click,
// minus the GUI / EAE deploy. This is the autonomous loop's verification surface.
//
// See C:\VueOneMapper\CLAUDE.md "Finish line" for the checklist this asserts.
// Failures are collected into a list and reported all-at-once at the end so the
// loop sees every red/green item in one run instead of stopping at the first
// failure.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.Core;
using CodeGen.Devices.M262;
using CodeGen.Services;
using CodeGen.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MapperTests
{
    public class SimulatorEndToEndHarness
    {
        private readonly ITestOutputHelper _out;
        public SimulatorEndToEndHarness(ITestOutputHelper output) => _out = output;

        static readonly XNamespace Ns = "https://www.se.com/LibraryElements";

        // Full SMC system Control.xml — copied from
        // C:\VueOne\system\SMC_Vue2VC_With_Processes\Control.xml. Contains all 34
        // components including the M580 Assembly actuators (Bearing_PnP 13 states,
        // Shaft_Hr/Vr 5 states, Robot grippers, etc.) needed for end-to-end Assembly.
        // The original Feed_Station_Fixture.xml only had the 3 Processes + Feed
        // Station actuators and was unusable for Assembly verification.
        static string FixturePath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "Full_System_Fixture.xml");

        static string BindingsPath() =>
            Path.Combine(AppContext.BaseDirectory, "TestData", "SMC_Rig_IO_Bindings.xlsx");

        // Required Assembly_Station actuators (matched against Control.xml VueOne names,
        // case-sensitive as Control.xml emits them). All deploy as Five_State_Actuator_CAT
        // under StubSevenStateActuatorsAsFiveState=true.
        //
        // Clamp is intentionally NOT in this list — Iteration 3 audit (2026-05-29):
        //   • The fixture (Full_System_Fixture.xml ← SMC_Vue2VC_With_Processes/Control.xml)
        //     defines 0 Clamp Components.
        //   • The legacy C:\VueOne\system\Control.xml defines 1 Clamp Component and is
        //     what the live rig deploys from — that's why the deployed M580 sysres has
        //     a Clamp FB.
        //   • The Mapper does NOT inject Clamp from a hardcoded path. SystemLayoutInjector
        //     ~line 920-946 has Clamp in an `allowedActuators` ALLOW-list, not a force-
        //     emit list; HcfSymbolIndex.NameBasedPlcGuess only buckets Clamp to M580 if
        //     it shows up in Control.xml. So whether Clamp is in the syslay is a fixture
        //     decision, not a Mapper bug.
        // To exercise Clamp end-to-end in sim, swap the fixture to the legacy
        // C:\VueOne\system\Control.xml (after fixing its encoding declaration) and add
        // "Clamp" back to this array.
        static readonly string[] AssemblyActuators = {
            "Bearing_PnP", "Bearing_Gripper", "Shaft_Hr",
            "Shaft_Vr", "Shaft_Gripper",
        };

        [Fact]
        public async Task AssemblyStationGeneratesEndToEndInSimulator()
        {
            var failures = new List<string>();
            void Fail(string id, string msg) { var line = $"[{id}] {msg}"; failures.Add(line); _out.WriteLine("  ✗ " + line); }
            void Pass(string id, string msg) => _out.WriteLine($"  ✓ [{id}] {msg}");
            void Note(string msg) => _out.WriteLine("  · " + msg);

            // ── Setup ────────────────────────────────────────────────────────
            var tempRoot = Path.Combine(Path.GetTempPath(),
                "SimE2E_" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempRoot);
            _out.WriteLine($"[Harness] temp root: {tempRoot}");
            _out.WriteLine($"[Harness] fixture:   {FixturePath()}");
            _out.WriteLine($"[Harness] bindings:  {BindingsPath()}");

            var syslay = Path.Combine(tempRoot, "sim.syslay");
            var sysres = Path.Combine(tempRoot, "sim.sysres");
            File.WriteAllText(syslay,
                "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");
            File.WriteAllText(sysres,
                "<Layer xmlns=\"https://www.se.com/LibraryElements\"><FBNetwork/></Layer>");

            var cfg = new MapperConfig
            {
                SyslayPath2 = syslay,
                SysresPath2 = sysres,
                SimulatorFullSystem = true,
            };

            IoBindingsLoader.InvalidateCache();
            var bindings = IoBindingsLoader.LoadBindings(BindingsPath());

            // ── A: generation succeeds end-to-end ────────────────────────────
            string generatedPath = string.Empty;
            SystemInjector.BindingApplicationReport report = null!;
            try
            {
                var injector = new SystemInjector();
                var cleanup = await Task.Run(() =>
                    injector.PrepareDemonstratorForGeneration(cfg));
                Note($"cleanup: {cleanup}");

                generatedPath = await Task.Run(() =>
                    injector.GenerateStation1TestSyslay(cfg, FixturePath(), bindings, out report));
                Pass("A1", $"GenerateStation1TestSyslay returned: {generatedPath}");

                // Mirror MainForm_simulator's post-gen steps. As of iteration 2 the
                // sim post-processors live in CodeGen.Services.SimulatorPostProcessor
                // as public statics — MainForm and this harness call the same code, no
                // replication drift.
                // Mirror every top-level syslay FB into the sysres FBNetwork. On the
                // live rig this happens inside FinalizeM262StackAsync via M262SysdevEmitter
                // .Emit, which needs a full EAE project tree on disk. The harness has no
                // sysdev/.system/.dfbproj, so we call the lower-level public mirror
                // directly. In SimulatorFullSystem mode every FB belongs in the single SIM
                // resource — no PLC-bucket filtering needed (sim collapses M262/M580/BX1).
                try
                {
                    var toMirror = SysresFbMirror.ReadSyslayTopLevelFbs(syslay);
                    int mirrored = SysresFbMirror.MirrorFbsIntoSysres(sysres, toMirror);
                    Pass("A2a", $"SysresFbMirror.MirrorFbsIntoSysres mirrored {mirrored} FB(s) of {toMirror.Count} syslay FB(s)");
                }
                catch (Exception ex)
                {
                    Fail("A2a", $"SysresFbMirror.MirrorFbsIntoSysres threw: {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    await Task.Run(() => M262SysresWireEmitter.Emit(cfg, report));
                    Pass("A2b", "M262SysresWireEmitter.Emit completed");
                }
                catch (Exception ex)
                {
                    Fail("A2b", $"M262SysresWireEmitter.Emit threw: {ex.GetType().Name}: {ex.Message}");
                }

                int hopper = SimulatorPostProcessor.InjectSimHopperForce(syslay, cfg, Note);
                Note($"InjectSimHopperForce → {hopper} (1=injected, 0=skipped/unresolvable)");

                int overrides = SimulatorPostProcessor.OverrideSimActuatorsNoSensor(syslay, cfg, Note);
                Note($"no-sensor override touched {overrides} Five_State_Actuator_CAT instance(s) across syslay+sysres");

                int swivelInjected = SimulatorPostProcessor.InjectSimSwivelForce(syslay, cfg, Note);
                Note($"InjectSimSwivelForce → {swivelInjected} new SimSwivelForce SYMLINKMULTIVARSRC(s) (0 when StubSevenStateActuatorsAsFiveState=true)");
            }
            catch (Exception ex)
            {
                Fail("A1", $"generation pipeline threw: {ex.GetType().Name}: {ex.Message}");
                // Without a syslay we can't run the rest — surface the partial failure and bail.
                Assert.Fail("[A1] generation aborted — see harness output");
                return;
            }

            // ── Read generated artefacts ─────────────────────────────────────
            XDocument syslayDoc, sysresDoc;
            try
            {
                syslayDoc = XDocument.Load(syslay);
                sysresDoc = XDocument.Load(sysres);
            }
            catch (Exception ex)
            {
                Fail("A2", $"could not re-read generated artefacts: {ex.Message}");
                Assert.Fail("[A2] re-read aborted — see harness output");
                return;
            }

            var syslayNet = syslayDoc.Root?.Element(Ns + "SubAppNetwork")
                            ?? syslayDoc.Root?.Element(Ns + "FBNetwork");
            var sysresNet = sysresDoc.Root?.Element(Ns + "FBNetwork");
            if (syslayNet == null) { Fail("A2", "syslay has no SubAppNetwork/FBNetwork"); }
            if (sysresNet == null) { Fail("A2", "sysres has no FBNetwork"); }
            if (syslayNet == null || sysresNet == null)
            {
                Assert.Fail("[A2] artefact networks missing — see harness output");
                return;
            }

            var syslayFbs = syslayNet.Elements(Ns + "FB").ToList();
            var sysresFbs = sysresNet.Elements(Ns + "FB").ToList();
            Note($"syslay top-level FBs: {syslayFbs.Count}; sysres top-level FBs: {sysresFbs.Count}");

            // ── B: shape ─────────────────────────────────────────────────────
            void CheckProcessPresent(string id, string procName)
            {
                var fb = syslayFbs.FirstOrDefault(f =>
                    string.Equals((string?)f.Attribute("Name"), procName, StringComparison.Ordinal));
                if (fb == null)
                {
                    Fail(id, $"Process FB '{procName}' missing from SIM syslay");
                    return;
                }
                var type = (string?)fb.Attribute("Type");
                if (!string.Equals(type, "Process1_Generic", StringComparison.Ordinal))
                {
                    Fail(id, $"FB '{procName}' is Type={type ?? "(null)"}, expected Process1_Generic");
                    return;
                }
                Pass(id, $"Process FB '{procName}' present and typed Process1_Generic");
            }
            CheckProcessPresent("B1", "Assembly_Station");
            CheckProcessPresent("B2", "Disassembly");
            CheckProcessPresent("B3", "Feed_Station");

            // B4: every Assembly actuator present and typed correctly.
            // Under StubSevenStateActuatorsAsFiveState=true, ALL Assembly actuators
            // including Bearing_PnP deploy as Five_State_Actuator_CAT. With the stub
            // off, the 13-state branched Bearing_PnP deploys as Seven_State_Actuator_CAT
            // (SystemLayoutInjector.cs:1601-1603 routing); the other Assembly actuators
            // stay Five_State. So the expected type is computed per-actuator here.
            bool stubOn = MapperConfig.StubSevenStateActuatorsAsFiveState;
            foreach (var name in AssemblyActuators)
            {
                var fb = syslayFbs.FirstOrDefault(f =>
                    string.Equals((string?)f.Attribute("Name"), name, StringComparison.Ordinal));
                if (fb == null)
                {
                    Fail("B4", $"Assembly actuator '{name}' missing from SIM syslay");
                    continue;
                }
                var type = (string?)fb.Attribute("Type") ?? string.Empty;
                bool isBearing = string.Equals(name, "Bearing_PnP", StringComparison.Ordinal);
                string expected = (!stubOn && isBearing)
                    ? "Seven_State_Actuator_CAT"
                    : "Five_State_Actuator_CAT";
                if (!string.Equals(type, expected, StringComparison.Ordinal))
                {
                    Fail("B4", $"Assembly actuator '{name}' is Type={type}, expected {expected} " +
                              $"(stub flag = {stubOn})");
                    continue;
                }
                Pass("B4", $"{name} present, {expected}");
            }

            // B5: every Five_State has no-sensor params in BOTH syslay AND sysres.
            // Will RED until the harness mirrors InjectSimHopperForce + the no-sensor
            // override or until that pipeline is exposed as public API.
            CheckNoSensorOverride(syslayNet, "syslay", Fail, Pass);
            CheckNoSensorOverride(sysresNet, "sysres", Fail, Pass);

            // B6: no Process source pin in any Connection is a phantom (Process1_Generic
            // has no data/event outputs other than INITO + adapter plugs).
            CheckNoPhantomSources(syslayNet, "syslay", Fail, Pass);

            // ── C: Assembly recipe coverage ──────────────────────────────────
            var assemblyFb = syslayFbs.FirstOrDefault(f =>
                string.Equals((string?)f.Attribute("Name"), "Assembly_Station", StringComparison.Ordinal));
            if (assemblyFb != null)
                CheckRecipeReferencesActuators(assemblyFb, AssemblyActuators, Fail, Pass, Note);

            // ── D: SimHopperForce ────────────────────────────────────────────
            CheckSimHopperForce(syslayNet, "syslay", Fail, Pass);
            CheckSimHopperForce(sysresNet, "sysres", Fail, Pass);

            // ── D2: SimSwivelForce — per Seven_State instance, sim sensor
            // synthesis must be wired so atwork1/atwork2 close in sim. Only
            // exercised when the stub is OFF; under stub=true there are no
            // Seven instances and this is a no-op-green.
            CheckSimSwivelForce(syslayNet, "syslay", Fail, Pass, Note);
            CheckSimSwivelForce(sysresNet, "sysres", Fail, Pass, Note);

            // ── D3 (cross-PLC MQTT bridge) REMOVED 2026-06-01 with the bridge.
            // Only MqttConn + embedded MqttPub remain; no standalone bridge
            // pairs to assert. CheckMqttBridge retained but uncalled.

            // ── E: Disassembly parked ────────────────────────────────────────
            var disassyFb = syslayFbs.FirstOrDefault(f =>
                string.Equals((string?)f.Attribute("Name"), "Disassembly", StringComparison.Ordinal));
            if (disassyFb != null)
                CheckDisassemblyParked(disassyFb, Fail, Pass);

            // ── F1: Assembly recipe terminates in an END row (StepType=9) ────
            if (assemblyFb != null)
                CheckRecipeHasEndRow(assemblyFb, Fail, Pass, Note);

            // ── F2: INIT chain reaches Assembly_Station ──────────────────────
            CheckInitChainReaches(syslayNet, "Assembly_Station", Fail, Pass, Note);

            // ── F3: Repeatability — second run is byte-identical ─────────────
            await CheckRepeatability(syslay, sysres, cfg, bindings, Fail, Pass, Note);

            // ── F4: Every non-zero Wait1Id resolves to a sysres id ───────────
            if (assemblyFb != null)
                CheckWait1IdResolution(assemblyFb, sysresNet, Fail, Pass, Note);

            // ── G1: Interlock STRUCT (RuleTable) collapse landed ─────────────
            // Under SimulatorFullSystem, BuildActuatorParameters' dropInterlockConstants
            // branch swaps the 4 parallel Rule{From,To,Source,Blocked}State arrays for
            // one RuleTable InterlockRule[10] STRUCT literal (SystemLayoutInjector.cs
            // ~1843-1848) and TemplateLibraryDeployer.NormalizeFiveStateRuleArrays /
            // NormalizeCommonInterlockEvaluatorRules reshapes the CAT + basic FB to
            // match. G1 asserts the swap actually lands on disk so a regression that
            // flips reduce=false on the sim path gets caught.
            CheckInterlockStructCollapse(syslayNet, "syslay", Fail, Pass, Note);
            CheckInterlockStructCollapse(sysresNet, "sysres", Fail, Pass, Note);

            // ── D4: MQTT injection — per-resource MqttConn (BX1 + M262 only) ──
            // The main pipeline above runs with MqttPublishEnabled=false (default),
            // so MQTT is never exercised there. This self-contained block re-runs
            // generation with MqttPublishEnabled=true and asserts the per-resource
            // MQTT_CONNECTION layout the live "Test Simulator" button produces:
            //   • MqttConn       — BX1 (Cover P&P / Soft dPAC, the rig MQTT host)
            //   • MqttConn_M262  — M262 (Feed_Station) so its embedded MqttPub
            //                      binds locally and Feed data reaches the broker
            //   • NO MqttConn_M580 — Assembly is out of scope; M580 has no MqttConn
            // plus the Feed_Station bring-up wires that drive MqttConn_M262 to
            // IsConnected=TRUE in sim (Area.INITO→INIT, INITO→CONNECT self-loop).
            // This is the headless proof that MQTT is actually generated.
            try
            {
                var mqttRoot = Path.Combine(Path.GetTempPath(), "SimHarnessMqtt_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(mqttRoot);
                var mqttSyslay = Path.Combine(mqttRoot, "sim.syslay");
                var mqttSysres = Path.Combine(mqttRoot, "sim.sysres");
                File.WriteAllText(mqttSyslay, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");
                File.WriteAllText(mqttSysres, "<Layer xmlns=\"https://www.se.com/LibraryElements\"><FBNetwork/></Layer>");

                var mqttCfg = new MapperConfig
                {
                    SyslayPath2 = mqttSyslay,
                    SysresPath2 = mqttSysres,
                    SimulatorFullSystem = true,
                    MqttPublishEnabled = true,
                    MqttClientId = "SMC_BX1",
                    MqttBrokerUrl = "mqtt://192.168.1.50:1883",
                };

                SystemInjector.BindingApplicationReport mqttRep = null!;
                var mqttInjector = new SystemInjector();
                await Task.Run(() => mqttInjector.GenerateStation1TestSyslay(mqttCfg, FixturePath(), bindings, out mqttRep));

                var mqttFbs = SysresFbMirror.ReadSyslayTopLevelFbs(mqttSyslay);
                var mqttConns = mqttFbs.Where(f => f.Type == "MQTT_CONNECTION")
                    .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

                bool hasBx1  = mqttConns.Contains("MqttConn");
                bool hasM262 = mqttConns.Contains("MqttConn_M262");
                bool hasM580 = mqttConns.Contains("MqttConn_M580");

                if (hasBx1 && hasM262 && !hasM580)
                    Pass("D4", $"MQTT: per-resource MqttConn present — BX1 (MqttConn) + M262 (MqttConn_M262), no M580 [{string.Join(", ", mqttConns)}]");
                else
                    Fail("D4", $"MQTT: expected MqttConn + MqttConn_M262 and NO MqttConn_M580; got [{string.Join(", ", mqttConns)}]");

                // ConnectionID must be the embedded MqttPub bind string (config.MqttClientId,
                // 'SMC_BX1') on BOTH connections — that string is what binds a local MqttPub
                // to the MqttConn on its own resource. Informational (FormatString form may
                // wrap it in quotes), so it never fails the run.
                var connIds = mqttFbs.Where(f => f.Type == "MQTT_CONNECTION")
                    .Select(f => f.Parameters.FirstOrDefault(p => p.Name == "ConnectionID")?.Value ?? "(none)")
                    .ToList();
                if (connIds.All(v => v.Contains("SMC_BX1", StringComparison.Ordinal)))
                    Pass("D4b", "MQTT: both MqttConn carry ConnectionID 'SMC_BX1' (embedded MqttPub bind string)");
                else
                    Note($"D4b: ConnectionID values = [{string.Join(", ", connIds)}]");

                // Feed_Station bring-up wires for MqttConn_M262 (drives IsConnected=TRUE in sim).
                var mqttDoc = XDocument.Load(mqttSyslay);
                var mqttNet = mqttDoc.Root?.Element(Ns + "SubAppNetwork") ?? mqttDoc.Root?.Element(Ns + "FBNetwork");
                bool HasEv(string s, string d) =>
                    (mqttNet?.Element(Ns + "EventConnections")?.Elements(Ns + "Connection") ?? Enumerable.Empty<XElement>())
                    .Any(c => (string?)c.Attribute("Source") == s && (string?)c.Attribute("Destination") == d);
                var wireMissing = new List<string>();
                if (!HasEv("Area.INITO", "MqttConn_M262.INIT")) wireMissing.Add("Area.INITO → MqttConn_M262.INIT");
                if (!HasEv("MqttConn_M262.INITO", "MqttConn_M262.CONNECT")) wireMissing.Add("MqttConn_M262.INITO → MqttConn_M262.CONNECT");
                if (wireMissing.Count == 0)
                    Pass("D4c", "MQTT: MqttConn_M262 wired into Feed_Station boot (Area.INITO→INIT) + INITO→CONNECT self-loop");
                else
                    Fail("D4c", "MQTT: MqttConn_M262 bring-up wires missing: " + string.Join("; ", wireMissing));

                try { Directory.Delete(mqttRoot, true); } catch { /* best-effort temp cleanup */ }
            }
            catch (Exception ex)
            {
                Fail("D4", $"MQTT injection check threw: {ex.GetType().Name}: {ex.Message}");
            }

            // ── Summary ──────────────────────────────────────────────────────
            _out.WriteLine("");
            _out.WriteLine($"[Harness] {failures.Count} checklist failure(s).");
            if (failures.Count > 0)
            {
                Assert.Fail(
                    $"{failures.Count} checklist failure(s) — see harness output:\n  - "
                    + string.Join("\n  - ", failures));
            }
        }

        // ── Assertion helpers ──────────────────────────────────────────────────

        static void CheckNoSensorOverride(XElement net, string label,
            Action<string, string> Fail, Action<string, string> Pass)
        {
            // 2026-05-30: SimSensorConfig POC reverted. Five_State CAT retains its
            // two original scalar BOOL parameters; the sim override sets both
            // FALSE so No_Sensor_Handler's timer path advances the ECC. Assert
            // every Five_State_Actuator_CAT instance has WorkSensorFitted=FALSE
            // and HomeSensorFitted=FALSE. (The connection-level field-access
            // approach the POC tried — Source="SensorConfig.Fitted_Work" — is
            // unproven in EAE 24.1 and broke the deployed CAT body.)
            int actuators = 0, ok = 0;
            foreach (var fb in net.Elements(Ns + "FB")
                .Where(f => (string?)f.Attribute("Type") == "Five_State_Actuator_CAT"))
            {
                actuators++;
                var fbName = (string?)fb.Attribute("Name") ?? "(unnamed)";
                bool fbOk = true;

                foreach (var pname in new[] { "WorkSensorFitted", "HomeSensorFitted" })
                {
                    var ps = fb.Elements(Ns + "Parameter")
                        .Where(p => (string?)p.Attribute("Name") == pname).ToList();
                    if (ps.Count == 0)
                    {
                        Fail("B5", $"{label}: {fbName}.{pname} parameter MISSING (override should have inserted FALSE)");
                        fbOk = false;
                        continue;
                    }
                    if (ps.Count > 1)
                    {
                        Fail("B5", $"{label}: {fbName}.{pname} has {ps.Count} duplicate entries");
                        fbOk = false;
                    }
                    var v = ((string?)ps[0].Attribute("Value") ?? "").Trim();
                    if (!string.Equals(v, "FALSE", StringComparison.OrdinalIgnoreCase))
                    {
                        Fail("B5", $"{label}: {fbName}.{pname}=\"{v}\" (expected FALSE)");
                        fbOk = false;
                    }
                }
                if (fbOk) ok++;
            }
            if (actuators == 0)
                Fail("B5", $"{label}: zero Five_State_Actuator_CAT instances (expected ≥6 Assembly + Feed)");
            else if (ok == actuators)
                Pass("B5", $"{label}: WorkSensorFitted=FALSE & HomeSensorFitted=FALSE on all {actuators} Five_State actuator(s)");
        }

        static void CheckNoPhantomSources(XElement net, string label,
            Action<string, string> Fail, Action<string, string> Pass)
        {
            var procNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "Assembly_Station", "Disassembly", "Feed_Station",
            };
            var phantomPorts = new[] { ".state_update", ".actuator_name", ".state_val" };

            var bad = new List<string>();
            foreach (var c in net.Descendants(Ns + "Connection"))
            {
                var src = (string?)c.Attribute("Source") ?? "";
                var dot = src.IndexOf('.');
                if (dot <= 0) continue;
                var fbName = src.Substring(0, dot);
                if (!procNames.Contains(fbName)) continue;
                if (phantomPorts.Any(p => src.EndsWith(p, StringComparison.Ordinal)))
                    bad.Add($"{src} -> {(string?)c.Attribute("Destination") ?? "(null)"}");
            }
            if (bad.Count == 0)
                Pass("B6", $"{label}: no phantom Process source-pin connections");
            else
            {
                var sample = string.Join("; ", bad.Take(3));
                Fail("B6", $"{label}: {bad.Count} phantom Process source pin(s) e.g. {sample}");
            }
        }

        static void CheckRecipeReferencesActuators(XElement procFb, string[] actuators,
            Action<string, string> Fail, Action<string, string> Pass,
            Action<string>? Note = null)
        {
            // Per-row assertion: parse the Recipe parameter into rows and require at
            // least one CMD row (StepType=1) per Assembly actuator. Substring matches
            // on the whole parameter blob are coincidence-friendly (an actuator name
            // could appear in a Wait1Id symbol or a comment) — this version proves the
            // recipe will actually COMMAND each actuator, not just mention it.
            //
            // Recipe serialization under (SimulatorFullSystem || UseRecipeStruct) is a
            // single Parameter Name="Recipe" Value="[ row, row, ... ]" where each row is
            // a struct literal like "(StepType:=1, CmdTargetName:='bearing_pnp', ...)".
            // We tolerate both that form and the parallel-arrays form just in case.
            var recipeParam = procFb.Elements(Ns + "Parameter")
                .FirstOrDefault(p => string.Equals((string?)p.Attribute("Name"), "Recipe", StringComparison.Ordinal));

            var cmdTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (recipeParam != null)
            {
                var blob = (string?)recipeParam.Attribute("Value") ?? string.Empty;
                Note?.Invoke($"Recipe parameter length = {blob.Length} chars");
                // Split into rows by '(' boundaries — each row begins with '(StepType:=…)'.
                // Then look for StepType:=1 with a CmdTargetName:='name' field in the same row.
                var rowRx = new System.Text.RegularExpressions.Regex(
                    @"\(\s*StepType\s*:=\s*(?<step>\d+).*?CmdTargetName\s*:=\s*'(?<tgt>[^']*)'",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                foreach (System.Text.RegularExpressions.Match m in rowRx.Matches(blob))
                {
                    if (m.Groups["step"].Value == "1")
                    {
                        var tgt = m.Groups["tgt"].Value.Trim();
                        if (tgt.Length > 0) cmdTargets.Add(tgt);
                    }
                }
            }
            else
            {
                // Fallback to parallel arrays: <Parameter Name="CmdTargetName[N]" ... />
                // alongside <Parameter Name="StepType[N]" ... />. Map row index -> StepType.
                var stepTypes = new Dictionary<int, string>();
                var targets   = new Dictionary<int, string>();
                var idxRx = new System.Text.RegularExpressions.Regex(@"\[(\d+)\]");
                foreach (var p in procFb.Elements(Ns + "Parameter"))
                {
                    var n = (string?)p.Attribute("Name") ?? string.Empty;
                    var v = ((string?)p.Attribute("Value") ?? string.Empty).Trim();
                    var idxMatch = idxRx.Match(n);
                    if (!idxMatch.Success) continue;
                    var idx = int.Parse(idxMatch.Groups[1].Value);
                    if (n.StartsWith("StepType", StringComparison.Ordinal)) stepTypes[idx] = v;
                    else if (n.StartsWith("CmdTargetName", StringComparison.Ordinal))
                        targets[idx] = v.Trim('\'', '"');
                }
                foreach (var (idx, step) in stepTypes)
                {
                    if (step == "1" && targets.TryGetValue(idx, out var tgt) && tgt.Length > 0)
                        cmdTargets.Add(tgt);
                }
            }

            Note?.Invoke($"Assembly recipe distinct CMD targets: [{string.Join(", ", cmdTargets.OrderBy(s => s))}]");

            var missing = actuators
                .Where(a => !cmdTargets.Contains(a) && !cmdTargets.Contains(a.ToLowerInvariant()))
                .ToList();
            if (missing.Count == 0)
                Pass("C", $"Assembly_Station.Recipe has CMD row(s) for all {actuators.Length} required actuator(s)");
            else
                Fail("C", "Assembly_Station.Recipe has NO CMD row (StepType=1) for: "
                          + string.Join(", ", missing)
                          + " — actuator(s) silently parked. CMD targets actually found: ["
                          + string.Join(", ", cmdTargets.OrderBy(s => s)) + "]");
        }

        static void CheckSimHopperForce(XElement net, string label,
            Action<string, string> Fail, Action<string, string> Pass)
        {
            var hopper = net.Elements(Ns + "FB")
                .FirstOrDefault(f => (string?)f.Attribute("Name") == "SimHopperForce");
            if (hopper != null) Pass("D", $"{label}: SimHopperForce FB present");
            else
                Fail("D", $"{label}: SimHopperForce FB missing — the post-gen InjectSimHopperForce did not run "
                          + "(harness needs to mirror MainForm_simulator's call or that pipeline must be exposed)");
        }

        /// <summary>G1 — for every Five_State_Actuator_CAT instance in the SIM
        /// syslay/sysres, assert the interlock STRUCT collapse has landed: the 4
        /// parallel RuleFromState/RuleToState/RuleSourceID/RuleBlockedState arrays
        /// MUST have been dropped and replaced by one RuleTable parameter holding
        /// the InterlockRule[10] STRUCT literal. Catches a regression that flips
        /// dropInterlockConstants=false on the sim path (would re-emit the 4 arrays
        /// against a CAT whose normalised body no longer declares them).</summary>
        static void CheckInterlockStructCollapse(XElement net, string label,
            Action<string, string> Fail, Action<string, string> Pass, Action<string> Note)
        {
            var legacy = new[] { "RuleFromState", "RuleToState", "RuleSourceID", "RuleBlockedState" };
            int collapsed = 0, total = 0;
            foreach (var fb in net.Elements(Ns + "FB")
                .Where(f => (string?)f.Attribute("Type") == "Five_State_Actuator_CAT"))
            {
                total++;
                var fbName = (string?)fb.Attribute("Name") ?? "(unnamed)";
                var paramNames = fb.Elements(Ns + "Parameter")
                    .Select(p => (string?)p.Attribute("Name") ?? string.Empty)
                    .ToHashSet(StringComparer.Ordinal);

                var legacyStill = legacy.Where(n => paramNames.Contains(n)).ToList();
                if (legacyStill.Count > 0)
                {
                    Fail("G1", $"{label}: {fbName} still carries the legacy parallel rule array(s): "
                              + string.Join(", ", legacyStill)
                              + " — interlock STRUCT collapse did not run (dropInterlockConstants flag off?).");
                    continue;
                }
                if (!paramNames.Contains("RuleTable"))
                {
                    Fail("G1", $"{label}: {fbName} has no RuleTable parameter — interlock STRUCT collapse did not emit the InterlockRule[10] struct array.");
                    continue;
                }
                collapsed++;
            }
            if (total == 0)
            {
                Note($"G1 {label}: no Five_State_Actuator_CAT instances to check (skipped).");
                return;
            }
            if (collapsed == total)
                Pass("G1", $"{label}: interlock STRUCT collapse landed on all {total} Five_State actuator(s) (RuleTable replaces the 4 parallel arrays).");
        }

        /// <summary>D2 — for every Seven_State_Actuator_CAT instance, the matching
        /// SimSwivelForce_&lt;name&gt; SYMLINKMULTIVARSRC must be present AND wired
        /// (INIT ← actuator.INITO, REQ ← actuator.plc_out, VALUE1/2 ← actuator's
        /// current_state{1,2}_to_plc). Without this the Seven_State ECC stalls at
        /// ToPick/ToPlace forever because atwork1/atwork2 never close in sim.
        /// Under StubSevenStateActuatorsAsFiveState=true there are no Seven instances
        /// and this is a no-op-green.</summary>
        static void CheckSimSwivelForce(XElement net, string label,
            Action<string, string> Fail, Action<string, string> Pass, Action<string> Note)
        {
            var sevens = net.Elements(Ns + "FB")
                .Where(f => (string?)f.Attribute("Type") == "Seven_State_Actuator_CAT")
                .Select(f => (string?)f.Attribute("Name") ?? string.Empty)
                .Where(n => n.Length > 0)
                .ToList();
            if (sevens.Count == 0)
            {
                Pass("D2", $"{label}: no Seven_State instances to wire (stub on or no swivel)");
                return;
            }
            int ok = 0;
            foreach (var name in sevens)
            {
                var fbName = "SimSwivelForce_" + name;
                var fb = net.Elements(Ns + "FB")
                    .FirstOrDefault(f => (string?)f.Attribute("Name") == fbName);
                if (fb == null)
                {
                    Fail("D2", $"{label}: {fbName} FB missing (Seven instance '{name}' has no sim sensor synthesis)");
                    continue;
                }
                if ((string?)fb.Attribute("Type") != SimulatorPostProcessor.SimSymlinkSrcType)
                {
                    Fail("D2", $"{label}: {fbName} has wrong Type (expected {SimulatorPostProcessor.SimSymlinkSrcType})");
                    continue;
                }

                bool HasEv(string s, string d) =>
                    (net.Element(Ns + "EventConnections")?.Elements(Ns + "Connection") ?? Enumerable.Empty<XElement>())
                    .Any(c => (string?)c.Attribute("Source") == s && (string?)c.Attribute("Destination") == d);
                bool HasDt(string s, string d) =>
                    (net.Element(Ns + "DataConnections")?.Elements(Ns + "Connection") ?? Enumerable.Empty<XElement>())
                    .Any(c => (string?)c.Attribute("Source") == s && (string?)c.Attribute("Destination") == d);

                var missing = new List<string>();
                if (!HasEv($"{name}.INITO",                     $"{fbName}.INIT"))    missing.Add($"event {name}.INITO → {fbName}.INIT");
                if (!HasEv($"{name}.plc_out",                   $"{fbName}.REQ"))     missing.Add($"event {name}.plc_out → {fbName}.REQ");
                if (!HasDt($"{name}.current_state1_to_plc",     $"{fbName}.VALUE1"))  missing.Add($"data {name}.current_state1_to_plc → {fbName}.VALUE1");
                if (!HasDt($"{name}.current_state2_to_plc",     $"{fbName}.VALUE2"))  missing.Add($"data {name}.current_state2_to_plc → {fbName}.VALUE2");
                if (missing.Count > 0)
                {
                    Fail("D2", $"{label}: {fbName} present but missing wires: " + string.Join("; ", missing));
                    continue;
                }
                ok++;
            }
            if (ok == sevens.Count)
                Pass("D2", $"{label}: SimSwivelForce wired for all {sevens.Count} Seven_State instance(s)");
            else
                Note($"D2 {label}: {ok}/{sevens.Count} Seven instances fully wired");
        }

        /// <summary>
        /// D3 — Cross-PLC MQTT bridge. Every MqttPub_&lt;comp&gt; on BX1 must
        /// have a matching MqttFmt_&lt;comp&gt;, the local Fmt→Pub chain, INIT
        /// off MqttConn.INITO, and a cross-resource feed into MqttFmt.REQ +
        /// MqttFmt.state. BX1's own components (CoverPNP*/TopCoverSenosr) must
        /// NOT be bridged — they publish via their embedded MqttPub.
        /// </summary>
        static void CheckMqttBridge(XElement net, string label,
            Action<string, string> Fail, Action<string, string> Pass, Action<string> Note)
        {
            var fbs = net.Elements(Ns + "FB").ToList();
            var pubs = fbs.Where(f => ((string?)f.Attribute("Name"))?.StartsWith("MqttPub_", StringComparison.Ordinal) == true)
                          .Select(f => (string)f.Attribute("Name")!).ToList();
            var fmts = fbs.Where(f => ((string?)f.Attribute("Name"))?.StartsWith("MqttFmt_", StringComparison.Ordinal) == true)
                          .Select(f => (string)f.Attribute("Name")!).ToHashSet(StringComparer.Ordinal);

            if (pubs.Count == 0)
            {
                Fail("D3", $"{label}: no MqttPub_<comp> bridge publishers found — the cross-PLC bridge emitted nothing");
                return;
            }

            // BX1's own components must NOT be bridged (they use embedded MqttPub).
            string[] bx1Own = { "CoverPNP_Hr", "CoverPNP_Vr", "CoverPnp_Gripper", "TopCoverSenosr" };
            foreach (var own in bx1Own)
            {
                if (pubs.Contains("MqttPub_" + own))
                    Fail("D3", $"{label}: BX1-own component '{own}' was bridged (MqttPub_{own}) — it must publish via its embedded MqttPub, not the bridge");
            }

            var ev = net.Element(Ns + "EventConnections");
            var dc = net.Element(Ns + "DataConnections");
            bool HasEvent(string src, string dst) => ev?.Elements(Ns + "Connection").Any(c =>
                (string?)c.Attribute("Source") == src && (string?)c.Attribute("Destination") == dst) == true;
            bool HasData(string src, string dst) => dc?.Elements(Ns + "Connection").Any(c =>
                (string?)c.Attribute("Source") == src && (string?)c.Attribute("Destination") == dst) == true;
            bool HasEventInto(string dst) => ev?.Elements(Ns + "Connection").Any(c =>
                (string?)c.Attribute("Destination") == dst) == true;
            bool HasDataInto(string dst) => dc?.Elements(Ns + "Connection").Any(c =>
                (string?)c.Attribute("Destination") == dst) == true;

            int ok = 0;
            foreach (var pub in pubs)
            {
                var comp = pub.Substring("MqttPub_".Length);
                var fmt = "MqttFmt_" + comp;
                var reasons = new List<string>();

                if (!fmts.Contains(fmt)) reasons.Add($"missing {fmt}");
                // Local Fmt → Pub chain.
                if (!HasEvent($"{fmt}.CNF", $"{pub}.PUBLISH1")) reasons.Add("no Fmt.CNF→Pub.PUBLISH1");
                if (!HasData($"{fmt}.payload", $"{pub}.Payload1")) reasons.Add("no Fmt.payload→Pub.Payload1");
                // INIT off MqttConn bring-up.
                if (!HasEvent("MqttConn.INITO", $"{fmt}.INIT")) reasons.Add("no MqttConn.INITO→Fmt.INIT");
                if (!HasEvent("MqttConn.INITO", $"{pub}.INIT")) reasons.Add("no MqttConn.INITO→Pub.INIT");
                // Cross-resource feed from the remote component into the formatter.
                if (!HasEventInto($"{fmt}.REQ")) reasons.Add("no cross-resource event → Fmt.REQ");
                if (!HasDataInto($"{fmt}.state")) reasons.Add("no cross-resource data → Fmt.state");

                if (reasons.Count == 0) ok++;
                else Fail("D3", $"{label}: bridge for '{comp}' incomplete — {string.Join(", ", reasons)}");
            }

            if (ok == pubs.Count)
                Pass("D3", $"{label}: cross-PLC MQTT bridge fully wired for all {pubs.Count} M262/M580 component(s)");
            else
                Note($"D3 {label}: {ok}/{pubs.Count} bridge publishers fully wired");
        }

        // ApplyNoSensorOverride: deleted in Iteration 6. Was a local replica added
        // in Iteration 1 to clear B5 syslay before SimulatorPostProcessor existed.
        // Iteration 2 extracted the real implementation to
        // CodeGen.Services.SimulatorPostProcessor.OverrideSimActuatorsNoSensor —
        // the harness now calls that public static directly.

        /// <summary>
        /// F1 — Assembly's recipe must terminate in an END row (StepType=9). Without
        /// it the ProcessRuntime engine never enters its idle-after-END state and the
        /// recipe loop is undefined.
        /// </summary>
        static void CheckRecipeHasEndRow(XElement procFb,
            Action<string, string> Fail, Action<string, string> Pass,
            Action<string>? Note = null)
        {
            var blob = (string?)procFb.Elements(Ns + "Parameter")
                .FirstOrDefault(p => string.Equals((string?)p.Attribute("Name"), "Recipe", StringComparison.Ordinal))
                ?.Attribute("Value") ?? string.Empty;
            // Count StepType:=9 rows in the Recipe blob (StepType=9 is the END opcode).
            var endRowRx = new System.Text.RegularExpressions.Regex(
                @"StepType\s*:=\s*9\b",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            var matches = endRowRx.Matches(blob);
            Note?.Invoke($"Assembly recipe END (StepType=9) rows: {matches.Count}");
            if (matches.Count == 0)
                Fail("F1", "Assembly_Station.Recipe has NO StepType=9 (END) row — recipe never terminates");
            else
                Pass("F1", $"Assembly_Station.Recipe terminates with {matches.Count} END row(s)");
        }

        /// <summary>
        /// F2 — the syslay INIT chain (Station.INITO → … → next.INIT events) must
        /// reach <paramref name="targetFbName"/>.INIT. If it doesn't, the named FB
        /// is never initialised and its ECC sits in its boot state forever.
        /// Walks <c>EventConnections</c> as a directed graph and BFS from any
        /// source that ends in <c>.INITO</c> (typically <c>FB1.INITO</c> or
        /// <c>Station1.INITO</c>) until it reaches <c>{targetFbName}.INIT</c> or
        /// exhausts the graph.
        /// </summary>
        static void CheckInitChainReaches(XElement net, string targetFbName,
            Action<string, string> Fail, Action<string, string> Pass,
            Action<string>? Note = null)
        {
            // Index event connections by Source for O(1) successor lookup.
            var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var c in net.Descendants(Ns + "Connection")
                .Where(c => c.Parent != null && c.Parent.Name.LocalName == "EventConnections"))
            {
                var src = (string?)c.Attribute("Source") ?? "";
                var dst = (string?)c.Attribute("Destination") ?? "";
                if (src.Length == 0 || dst.Length == 0) continue;
                if (!edges.TryGetValue(src, out var list)) edges[src] = list = new List<string>();
                list.Add(dst);
            }

            var target = $"{targetFbName}.INIT";
            // BFS from every plausible root: any source pin ending in .INITO that has
            // no INITO predecessor (i.e., a root of the INIT graph).
            var roots = edges.Keys
                .Where(s => s.EndsWith(".INITO", StringComparison.Ordinal))
                .Where(s => !edges.Values.Any(succs => succs.Contains(s)))
                .ToList();
            if (roots.Count == 0)
            {
                // Fall back to any .INITO source (cyclic graph or no clear root).
                roots = edges.Keys.Where(s => s.EndsWith(".INITO", StringComparison.Ordinal)).ToList();
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (var r in roots) { queue.Enqueue(r); visited.Add(r); }
            bool reached = false;
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (string.Equals(cur, target, StringComparison.Ordinal)) { reached = true; break; }
                if (!edges.TryGetValue(cur, out var succs)) continue;
                foreach (var s in succs)
                {
                    if (visited.Add(s)) queue.Enqueue(s);
                    if (string.Equals(s, target, StringComparison.Ordinal)) { reached = true; break; }
                }
                if (reached) break;
            }

            Note?.Invoke($"INIT graph: {roots.Count} root(s), {edges.Count} source pin(s), {visited.Count} reached");
            if (reached) Pass("F2", $"INIT chain reaches {target}");
            else Fail("F2", $"INIT chain does NOT reach {target} — Assembly_Station will never initialise");
        }

        /// <summary>
        /// F3 — same fixture + same MapperConfig should produce byte-identical syslay
        /// + sysres. A second run into a fresh temp dir must match the first byte-for-
        /// byte (modulo whitespace and the absolute paths embedded by GenerateStation1
        /// TestSyslay, neither of which we expect to vary). If the generator is non-
        /// deterministic, downstream EAE compile churn becomes hard to attribute.
        /// </summary>
        static async Task CheckRepeatability(string firstSyslay, string firstSysres,
            MapperConfig firstCfg, IoBindings? bindings,
            Action<string, string> Fail, Action<string, string> Pass,
            Action<string>? Note = null)
        {
            try
            {
                var t2 = Path.Combine(Path.GetTempPath(),
                    "SimE2E_repeat_" + Path.GetRandomFileName());
                Directory.CreateDirectory(t2);
                var s2syslay = Path.Combine(t2, "sim.syslay");
                var s2sysres = Path.Combine(t2, "sim.sysres");
                File.WriteAllText(s2syslay,
                    "<Layer xmlns=\"https://www.se.com/LibraryElements\"><SubAppNetwork/></Layer>");
                File.WriteAllText(s2sysres,
                    "<Layer xmlns=\"https://www.se.com/LibraryElements\"><FBNetwork/></Layer>");

                var cfg2 = new MapperConfig
                {
                    SyslayPath2 = s2syslay,
                    SysresPath2 = s2sysres,
                    SimulatorFullSystem = true,
                };
                var injector2 = new SystemInjector();
                await Task.Run(() => injector2.PrepareDemonstratorForGeneration(cfg2));
                SystemInjector.BindingApplicationReport rep2 = null!;
                await Task.Run(() => injector2.GenerateStation1TestSyslay(cfg2, FixturePath(), bindings, out rep2));
                var toMirror2 = SysresFbMirror.ReadSyslayTopLevelFbs(s2syslay);
                SysresFbMirror.MirrorFbsIntoSysres(s2sysres, toMirror2);
                await Task.Run(() => M262SysresWireEmitter.Emit(cfg2, rep2));
                SimulatorPostProcessor.InjectSimHopperForce(s2syslay, cfg2);
                SimulatorPostProcessor.OverrideSimActuatorsNoSensor(s2syslay, cfg2);
                // Mirror the main-run pipeline order: SimSwivelForce after the no-sensor
                // override. Without this, F3 sees a "missing FB" diff for every Seven
                // instance when the stub is OFF and incorrectly flags non-determinism.
                SimulatorPostProcessor.InjectSimSwivelForce(s2syslay, cfg2);

                // Strip whitespace and the absolute temp-dir prefix that bleeds into
                // a few attributes before comparing — those are environmental, not
                // generator non-determinism.
                static string Normalise(string content, string temp1, string temp2)
                {
                    var no_ws = new System.Text.StringBuilder(content.Length);
                    foreach (var ch in content)
                        if (!char.IsWhiteSpace(ch)) no_ws.Append(ch);
                    return no_ws.ToString()
                        .Replace(temp1, "<TEMP>", StringComparison.OrdinalIgnoreCase)
                        .Replace(temp2, "<TEMP>", StringComparison.OrdinalIgnoreCase);
                }

                var temp1 = Path.GetDirectoryName(firstSyslay) ?? "";
                var temp2 = Path.GetDirectoryName(s2syslay)    ?? "";

                bool SameFile(string a, string b)
                {
                    var na = Normalise(File.ReadAllText(a), temp1, temp2);
                    var nb = Normalise(File.ReadAllText(b), temp1, temp2);
                    return string.Equals(na, nb, StringComparison.Ordinal);
                }

                bool syslayEqual = SameFile(firstSyslay, s2syslay);
                bool sysresEqual = SameFile(firstSysres, s2sysres);
                Note?.Invoke($"repeatability: syslay equal = {syslayEqual}; sysres equal = {sysresEqual}");

                if (!syslayEqual)
                {
                    var a = Normalise(File.ReadAllText(firstSyslay), temp1, temp2);
                    var b = Normalise(File.ReadAllText(s2syslay), temp1, temp2);
                    var firstDiff = FirstDiffWindow(a, b, 120);
                    Note?.Invoke($"  syslay first diff @char {firstDiff.idx}: '{firstDiff.aWin}' vs '{firstDiff.bWin}'");
                }
                if (!sysresEqual)
                {
                    var a = Normalise(File.ReadAllText(firstSysres), temp1, temp2);
                    var b = Normalise(File.ReadAllText(s2sysres), temp1, temp2);
                    var firstDiff = FirstDiffWindow(a, b, 120);
                    Note?.Invoke($"  sysres first diff @char {firstDiff.idx}: '{firstDiff.aWin}' vs '{firstDiff.bWin}'");
                }

                if (syslayEqual && sysresEqual)
                    Pass("F3", "Two runs produce byte-identical syslay + sysres (after temp-dir + whitespace normalisation)");
                else
                    Fail("F3", $"Generator is NON-DETERMINISTIC: syslay equal = {syslayEqual}, sysres equal = {sysresEqual}");
            }
            catch (Exception ex)
            {
                Fail("F3", $"repeatability check threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// F4 — every non-zero Wait1Id in Assembly_Station.Recipe must resolve to an
        /// `actuator_id`/`id` parameter present on some FB in the SIM sysres. If a
        /// Wait1Id has no matching id, the runtime waits forever at that step.
        /// Pulls Wait1Id values out of the Recipe blob, gathers every `id`/`actuator_id`
        /// parameter value from sysres FBs, and asserts the subset relation.
        /// </summary>
        static void CheckWait1IdResolution(XElement procFb, XElement sysresNet,
            Action<string, string> Fail, Action<string, string> Pass,
            Action<string>? Note = null)
        {
            var blob = (string?)procFb.Elements(Ns + "Parameter")
                .FirstOrDefault(p => string.Equals((string?)p.Attribute("Name"), "Recipe", StringComparison.Ordinal))
                ?.Attribute("Value") ?? string.Empty;
            if (blob.Length == 0)
            {
                Fail("F4", "Assembly_Station.Recipe parameter is empty — cannot check Wait1Id resolution");
                return;
            }

            // Pull every Wait1Id:=N integer out of the blob.
            var waitIds = new HashSet<int>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                blob, @"Wait1Id\s*:=\s*(-?\d+)"))
            {
                if (int.TryParse(m.Groups[1].Value, out var v) && v > 0) waitIds.Add(v);
            }

            // Gather every actuator_id / id parameter from sysres FBs.
            var availableIds = new HashSet<int>();
            foreach (var fb in sysresNet.Elements(Ns + "FB"))
            {
                foreach (var p in fb.Elements(Ns + "Parameter"))
                {
                    var pname = (string?)p.Attribute("Name") ?? "";
                    if (pname != "actuator_id" && pname != "id") continue;
                    var pval = ((string?)p.Attribute("Value") ?? "").Trim();
                    if (int.TryParse(pval, out var v) && v >= 0) availableIds.Add(v);
                }
            }

            // Some sims emit ids on the syslay too (and sysres only mirrors structurally,
            // not always the params). If sysres has no ids, fall back to the syslay (the
            // FB instances are the same — the id is a design-time parameter).
            if (availableIds.Count == 0)
            {
                Note?.Invoke("F4: sysres carries no actuator_id/id parameters; falling back to syslay scan");
                var syslayNetFallback = procFb.Document?.Root;
                if (syslayNetFallback != null)
                {
                    foreach (var fb in syslayNetFallback.Descendants(Ns + "FB"))
                    {
                        foreach (var p in fb.Elements(Ns + "Parameter"))
                        {
                            var pname = (string?)p.Attribute("Name") ?? "";
                            if (pname != "actuator_id" && pname != "id") continue;
                            var pval = ((string?)p.Attribute("Value") ?? "").Trim();
                            if (int.TryParse(pval, out var v) && v >= 0) availableIds.Add(v);
                        }
                    }
                }
            }

            Note?.Invoke($"F4: distinct non-zero Wait1Ids = {waitIds.Count}, available ids = {availableIds.Count}");

            var unresolved = waitIds.Where(w => !availableIds.Contains(w)).OrderBy(w => w).ToList();
            if (unresolved.Count == 0)
                Pass("F4", $"All {waitIds.Count} distinct non-zero Wait1Id(s) resolve to an FB id in the SIM resource");
            else
                Fail("F4", $"{unresolved.Count} Wait1Id(s) do NOT resolve to any FB id: [" +
                          string.Join(", ", unresolved) +
                          $"]. Available ids = [{string.Join(", ", availableIds.OrderBy(i => i))}]. " +
                          "Recipe will stall at the unresolved waits.");
        }

        static (int idx, string aWin, string bWin) FirstDiffWindow(string a, string b, int window)
        {
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                if (a[i] != b[i])
                {
                    int start = Math.Max(0, i - 20);
                    int aEnd = Math.Min(a.Length, i + window);
                    int bEnd = Math.Min(b.Length, i + window);
                    return (i, a.Substring(start, aEnd - start), b.Substring(start, bEnd - start));
                }
            }
            return (len, a.Length > len ? a.Substring(len, Math.Min(window, a.Length - len)) : "",
                          b.Length > len ? b.Substring(len, Math.Min(window, b.Length - len)) : "");
        }

        static void CheckDisassemblyParked(XElement disFb,
            Action<string, string> Fail, Action<string, string> Pass)
        {
            // Disassembly is parked = its recipe is a single END row. Stable proxy under
            // the RecipeStep struct: search the Recipe parameter value for "9" (END StepType)
            // and confirm that meaningful CmdTargetName tokens are absent.
            var concat = string.Concat(disFb.Elements(Ns + "Parameter")
                .Select(p => ((string?)p.Attribute("Value") ?? string.Empty)))
                .ToLowerInvariant();
            if (concat.Length == 0)
            {
                Fail("E", "Disassembly has no Recipe-related parameters at all (park guard didn't run)");
                return;
            }
            var hasActuator = AssemblyActuators
                .Any(a => concat.Contains(a.ToLowerInvariant()));
            if (hasActuator)
                Fail("E", "Disassembly.Recipe references an Assembly actuator — park guard didn't fire");
            else
                Pass("E", "Disassembly parked (recipe references no Assembly actuator)");
        }
    }
}
