// Generic VueOne rig-follow ingestion tests.
//
// Drives the REAL compiled VueOne2Cliients.dll: UnsOrderingGate (topic filter + ordering)
// and UnsStateMessage (envelope parsing) exactly as VcMqttClient uses them. No mocks, no
// model-specific code - every case is expressed in terms of the wire contract, so it holds
// for any model and any UNS prefix.
//
// Build+run:  see tests/run_vueone_tests.ps1
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;

public static class VueOneFollowTests
{
    static int _pass, _fail;
    static Type _gate, _msg;
    static object _g;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine("{0}  {1,-56} {2}", ok ? "PASS" : "FAIL", name, detail);
    }

    static bool IsObs(string topic)
    {
        return (bool)_gate.GetMethod("IsObservationTopic", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, new object[] { topic });
    }

    // returns verdict name; epochChanged via out
    static string Admit(string vcId, string epoch, long seq, bool retained, out bool epochChanged)
    {
        var m = _gate.GetMethod("Admit");
        object[] args = { vcId, epoch, seq, retained, false };
        object v = m.Invoke(_g, args);
        epochChanged = (bool)args[4];
        return v.ToString();
    }

    static string Admit(string vcId, string epoch, long seq, bool retained = false)
    {
        bool e; return Admit(vcId, epoch, seq, retained, out e);
    }

    static object Parse(string json)
    {
        var mi = typeof(JsonSerializer).GetMethod("Deserialize", new[] { typeof(string), typeof(Type), typeof(JsonSerializerOptions) });
        return mi.Invoke(null, new object[] { json, _msg, null });
    }

    static object Prop(object o, string name)
    {
        return o.GetType().GetProperty(name).GetValue(o);
    }

    public static int Main(string[] argv)
    {
        string dll = argv.Length > 0 ? argv[0]
            : @"C:\V-Dev\VueOneVcVersion\VueOneVcVersion\vueone_vc\Published\VueOne2Cliients.dll";
        string probe = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dll));
        AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e2)
        {
            string p = System.IO.Path.Combine(probe, new AssemblyName(e2.Name).Name + ".dll");
            return System.IO.File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Assembly asm = Assembly.LoadFrom(dll);
        _gate = asm.GetType("VueOne2Clients.UnsOrderingGate", true);
        _msg = asm.GetType("VueOne2Clients.UnsStateMessage", true);
        _g = Activator.CreateInstance(_gate);

        Console.WriteLine(new string('=', 78));
        Console.WriteLine("VueOne rig-follow ingestion - generic tests against " + System.IO.Path.GetFileName(dll));
        Console.WriteLine(new string('=', 78));

        // ---------------------------------------------------------------- topic filter
        Console.WriteLine("\n-- topic filter (must reject command/event/bridge) ---------------------");
        Check("observation topic accepted",
            IsObs("uns/wmg/smc_rig/v1/assembly_station/bearing_pnp/state"));
        Check("vc/command REJECTED", !IsObs("uns/wmg/smc_rig/v1/vc/command"));
        Check("vc/event REJECTED", !IsObs("uns/wmg/smc_rig/v1/vc/event"));
        Check("_bridge/status REJECTED", !IsObs("uns/wmg/smc_rig/v1/_bridge/status"));
        Check("non-state topic REJECTED", !IsObs("uns/wmg/smc_rig/v1/assembly_station/clamp"));
        Check("arbitrary prefix still works (model-agnostic)",
            IsObs("uns/acme/line7/v3/pressing/ram/state"));
        Check("empty/null topic REJECTED", !IsObs("") && !IsObs(null));

        // a vc/command payload really does carry vcId + stateName - prove it is filtered
        string cmd = "{\"schemaVersion\":2,\"epoch\":\"E1\",\"commandId\":\"E1#7\",\"seq\":7," +
                     "\"vcId\":\"SviwelArmComp\",\"state\":1,\"stateName\":\"AtPick\",\"lane\":\"bearing\"," +
                     "\"execution\":\"axis\",\"target\":181.0,\"durationMs\":441}";
        object cmdParsed = Parse(cmd);
        Check("a vc/command payload WOULD parse as an observation",
            (string)Prop(cmdParsed, "VcId") == "SviwelArmComp" &&
            (string)Prop(cmdParsed, "StateName") == "AtPick",
            "-> it is the topic filter that must stop it");
        Check("...and the topic filter is what stops it",
            !IsObs("uns/wmg/smc_rig/v1/vc/command"));

        string evt = "{\"schemaVersion\":2,\"commandId\":\"E1#7\",\"epoch\":\"E1\"," +
                     "\"vcId\":\"SviwelArmComp\",\"lane\":\"bearing\",\"execution\":\"axis\"," +
                     "\"stateName\":\"AtPick\",\"status\":\"completed\"}";
        object evtParsed = Parse(evt);
        Check("a vc/event payload WOULD parse as an observation",
            (string)Prop(evtParsed, "VcId") == "SviwelArmComp");
        Check("...and the topic filter is what stops it",
            !IsObs("uns/wmg/smc_rig/v1/vc/event"));

        // ---------------------------------------------------------------- envelope
        Console.WriteLine("\n-- full envelope parse -------------------------------------------------");
        string obs = "{\"seq\":1234,\"epoch\":\"2026-07-27T09:30:26Z\",\"ts\":\"2026-07-27T09:30:27.001Z\"," +
                     "\"sourceTopic\":\"smc/bearing_pnp\",\"station\":\"assembly_station\"," +
                     "\"component\":\"bearing_pnp\",\"twinName\":\"Bearing_PnP\",\"vcId\":\"SviwelArmComp\"," +
                     "\"state\":2,\"stateName\":\"AtPick\",\"quality\":\"GOOD\"}";
        object o = Parse(obs);
        Check("seq/epoch/state/quality/sourceTopic all retained",
            (long)Prop(o, "Seq") == 1234 &&
            (string)Prop(o, "Epoch") == "2026-07-27T09:30:26Z" &&
            (int)Prop(o, "State") == 2 &&
            (string)Prop(o, "Quality") == "GOOD" &&
            (string)Prop(o, "SourceTopic") == "smc/bearing_pnp" &&
            (string)Prop(o, "Station") == "assembly_station");
        string sensor = "{\"seq\":9,\"epoch\":\"E\",\"vcId\":\"BearingSensor\",\"state\":0," +
                        "\"stateName\":\"Off\",\"quality\":\"GOOD\",\"present\":true}";
        object so = Parse(sensor);
        Check("sensor 'present' carried (active-low polarity preserved)",
            (bool)((bool?)Prop(so, "Present")).Value);

        // ---------------------------------------------------------------- ordering
        Console.WriteLine("\n-- ordering: retained bootstrap, duplicates, out-of-order --------------");
        _gate.GetMethod("Reset").Invoke(_g, null);

        // a full retained snapshot: every component carries its OWN historical seq
        var snapshot = new Dictionary<string, long> {
            {"Pusher", 500}, {"CheckerComp", 502}, {"TransferComp", 337}, {"Clamp", 214},
            {"SviwelArmComp", 639}, {"pnp_vertical", 897}, {"UR3e", 104}
        };
        int boot = 0;
        foreach (var kv in snapshot)
            if (Admit(kv.Key, "E1", kv.Value, true) == "RetainedBootstrap") boot++;
        Check("full retained snapshot accepted for every component",
            boot == snapshot.Count, boot + "/" + snapshot.Count);
        Check("retained NOT rejected for differing historical sequences", boot == snapshot.Count);

        Check("live ordered transition accepted", Admit("SviwelArmComp", "E1", 640) == "Accept");
        Check("QoS1 duplicate dropped", Admit("SviwelArmComp", "E1", 640) == "Duplicate");
        Check("out-of-order dropped", Admit("SviwelArmComp", "E1", 639) == "OutOfOrder");
        Check("next in-order still accepted", Admit("SviwelArmComp", "E1", 641) == "Accept");
        Check("a different component is tracked independently",
            Admit("Pusher", "E1", 501) == "Accept");

        bool changed;
        string v = Admit("SviwelArmComp", "E2", 1, false, out changed);
        Check("epoch change clears sequence history", changed && v == "Accept",
            "verdict=" + v + " changed=" + changed);
        Check("low sequence accepted after epoch change (StateSync restart)",
            Admit("Pusher", "E2", 2) == "Accept");

        // ---------------------------------------------------------------- full sequence
        Console.WriteLine("\n-- a busy component follows its COMPLETE sequence ----------------------");
        _gate.GetMethod("Reset").Invoke(_g, null);
        // the swivel's real cycle, including the phase-dependent alternate names
        string[] names = { "ToWork1", "AtPick", "TurningPlace", "Place", "ToHome", "AtHome",
                           "ReturnedHome", "ToWork1", "AtPlace2", "TurningPlace", "AtPick2",
                           "ToHome", "AtHome", "ReturnedHome" };
        int applied = 0;
        long seq = 100;
        var seen = new List<string>();
        foreach (string n in names)
        {
            if (Admit("SviwelArmComp", "E3", seq++) == "Accept") { applied++; seen.Add(n); }
            // the command and the event for the same transition never reach the gate:
            // they are filtered by topic, which is the point.
        }
        Check("every state in the sequence applied exactly once",
            applied == names.Length, applied + "/" + names.Length);
        Check("no state applied twice from state+command+event",
            seen.Count == names.Length);

        // interleave the SAME transition arriving 3x (state, command echo, event echo)
        _gate.GetMethod("Reset").Invoke(_g, null);
        int accepted = 0;
        foreach (long s in new long[] { 200, 200, 200, 201, 201, 202 })
            if (Admit("SviwelArmComp", "E4", s) == "Accept") accepted++;
        Check("triple-delivery of the same seq applied once each",
            accepted == 3, accepted + " of 6 deliveries (3 distinct)");

        Console.WriteLine("\n" + new string('=', 78));
        Console.WriteLine("{0}/{1} passed", _pass, _pass + _fail);
        Console.WriteLine(new string('=', 78));
        return _fail == 0 ? 0 : 1;
    }
}
