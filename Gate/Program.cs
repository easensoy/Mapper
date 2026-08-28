using System.Security.Cryptography;
using System.Text;
using CodeGen.Application;
using CodeGen.Configuration;
using CodeGen.Devices.RevPi;
using CodeGen.Mapping;
using CodeGen.Translation;

namespace VueOneMapper.Gate;

// The behaviour gate, versioned with the compiler it gates. It owns NO generation logic: every
// combination runs GenerateProject.Execute, the same entry point the VueOne Generate button and
// MapperUI both call, so what it proves is the production path rather than a stand-in.
//
// It writes only beneath the gate root, and reads the live EAE project at most once to seed a fixed
// baseline tree; that project is never written.
internal static class Program
{
    // What to gate is fixture data, declared beside the gate. Resolved once: a run that cannot resolve
    // its fixtures has nothing to prove, and says so rather than silently gating less.
    private static GateFixtures Fixtures => _fixtures ??= GateFixtures.Load();

    // What the gate itself needs to ask about a target, read from the same shipped bundle a run
    // reads - so the harness and the compiler cannot disagree about which targets exist.
    private static CodeGen.Configuration.CompilerConfiguration Declarations =>
        _declarations ??= CodeGen.Configuration.CompilerConfiguration.Load(MapperConfig.Load());
    private static CodeGen.Configuration.CompilerConfiguration? _declarations;
    private static GateFixtures? _fixtures;

    // The live EAE project the fixtures name. The gate reads it to seed a baseline and writes it
    // never, so a gate root that overlaps it is refused rather than trusted: the whole point of the
    // root is that everything below it is disposable.
    private static string LiveProject => Fixtures.LiveProject;

    private static string GateRoot
    {
        get
        {
            var root = Path.GetFullPath(
                Environment.GetEnvironmentVariable("VUEONE_GATE_ROOT") ?? @"C:\_gate")
                .TrimEnd(Path.DirectorySeparatorChar);
            var live = Path.GetFullPath(LiveProject).TrimEnd(Path.DirectorySeparatorChar);
            if (root.Equals(live, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(live + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                live.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"the gate root '{root}' overlaps the live project '{live}'. Everything below the " +
                    "root is deleted and rewritten, so it must be somewhere disposable.");
            return root;
        }
    }

    private static int Main(string[] args)
    {
        // Config resolves beside the gate, so a run cannot depend on the directory it was launched from.
        MapperConfig.ConfigurationRoot = AppContext.BaseDirectory;

        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        try
        {
            return verb switch
            {
                "snapshot" => Snapshot(Arg(args, 1, "label")),
                "compare" => Compare(Arg(args, 1, "labelA"), Arg(args, 2, "labelB"),
                                     args.Contains("--core", StringComparer.OrdinalIgnoreCase)),
                "determinism" => Determinism(),
                "placement" => Placement(),
                "aba" => AbaClosure(),
                "all" => All(),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gate] FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine(
            "usage: gate [all | snapshot <label> | compare <a> <b> [--core] | determinism | aba | placement]");
        return 2;
    }

    // The console column, not an identity: the manifest's name is what a baseline is filed under.
    // Models that share a prefix differ only in what follows it, so that is what a reader needs.
    private static string Label(string model)
    {
        var names = Fixtures.Models.Select(m => m.Name).ToList();
        int shared = names.Count < 2 ? 0 : names[0].Length;
        foreach (var other in names.Skip(1))
        {
            int i = 0;
            while (i < shared && i < other.Length && names[0][i] == other[i]) i++;
            shared = i;
        }
        var tail = model[shared..];
        return tail.Length == 0 ? model : tail;
    }

    private static string Arg(string[] args, int i, string name) =>
        i < args.Length ? args[i] : throw new ArgumentException($"missing argument <{name}>");

    // What a full check consists of. Each stage reports its own verdict and the exit code is non-zero
    // if ANY of them failed, so a green line cannot hide a red one.
    private static int All()
    {
        int failures = 0;
        Console.WriteLine("== generate every supported combination ==");
        failures += Snapshot("gate_a");
        Console.WriteLine("== determinism: the same input twice ==");
        failures += Determinism();
        Console.WriteLine("== A->B->A: a generation carries nothing into the next ==");
        failures += AbaClosure();
        Console.WriteLine("== placement: one plant, every target, only a roster row moving ==");
        failures += Placement();
        Console.WriteLine(failures == 0 ? "[gate] PASS" : $"[gate] FAIL: {failures} stage(s) failed");
        return failures == 0 ? 0 : 1;
    }

    // ---- generation ------------------------------------------------------------------------------

    private static int Snapshot(string label)
    {
        var outDir = Under("baselines", label);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);

        int failures = 0;
        // EVERY model, including the negative fixtures: this is the stage that proves a twin the
        // compiler must refuse is in fact refused, and refused for the declared reason.
        foreach (var model in Fixtures.Models)
            foreach (var selection in Fixtures.Selections)
            {
                var (name, controller) = (model.Name, selection.Name);
                Console.Write($"  {Label(name),-16} {controller,-6} ");
                var work = Under("work", label, name, controller);
                var error = Generate(model, selection.RevPi, work, seed: true);

                // A negative fixture PASSES by being refused, and only for the declared reason: an
                // invalid twin that suddenly compiles is exactly as much a regression as a valid one
                // that stops compiling.
                if (model.IsNegativeFixture)
                {
                    if (error == null)
                    {
                        Console.WriteLine("REFUSAL EXPECTED BUT THE MODEL COMPILED — " + model.RefusalReason);
                        failures++;
                    }
                    else if (!error.Contains(model.ExpectRefusal, StringComparison.Ordinal))
                    {
                        Console.WriteLine($"REFUSED FOR THE WRONG REASON: expected '{model.ExpectRefusal}', got: {error}");
                        failures++;
                    }
                    else Console.WriteLine($"REFUSED as declared  ({model.ExpectRefusal})");
                    continue;
                }

                if (error != null) { Console.WriteLine($"GENERATION FAILED: {error}"); failures++; continue; }

                var hashes = Hash(work);
                Directory.CreateDirectory(Path.Combine(outDir, name));
                File.WriteAllLines(Path.Combine(outDir, name, controller + ".sha256"),
                    hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                          .Select(kv => $"{kv.Value}  {kv.Key}"));
                Console.WriteLine($"OK   {hashes.Count,5} artefacts");
            }
        return failures;
    }

    // One combination, through the production entry point. Returns null on success.
    // A->B->A reuses one tree on purpose, which is the only case that must not be re-seeded.
    private static string? Generate(
        GateModel model, IReadOnlyCollection<string> revPi, string work, bool seed)
    {
        try
        {
            if (seed) Seed(work);
            var control = Fixtures.ControlFor(model);
            if (!File.Exists(control)) return "model Control.xml missing: " + control;

            // An isolated copy, so a generation never reads the authored model directly.
            var inputs = Under("inputs");
            Directory.CreateDirectory(inputs);
            var input = Path.Combine(inputs, model.Name + ".Control.xml");
            File.Copy(control, input, true);
            return Run(input, revPi, work);
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    // The same run, from a Control.xml the caller already owns.
    private static string? GenerateControl(
        string input, IReadOnlyCollection<string> revPi, string work, bool seed)
    {
        try
        {
            if (seed) Seed(work);
            return Run(input, revPi, work);
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    // The production entry point, and nothing else: what the gate proves is this call.
    private static string? Run(string input, IReadOnlyCollection<string> revPi, string work)
    {
        var cfg = MapperConfig.Load();
        Retarget(cfg, work);

        // Kept beside the work tree: a diagnostic is only useful if it survives the run that
        // produced it, and a failed fixture is read from here.
        var log = new List<string>();
        try
        {
            GenerateProject.Execute(
                new GenerationRequest(input, cfg,
                    new HashSet<string>(revPi, StringComparer.OrdinalIgnoreCase)),
                log.Add);
        }
        finally { File.WriteAllLines(work + ".log", log); }
        return null;
    }

    // The two configured artefact roots are the only paths a generation writes through, so retargeting
    // them is what confines the whole pipeline to the gate.
    private static void Retarget(MapperConfig cfg, string work)
    {
        var live = Path.GetFullPath(LiveProject).TrimEnd(Path.DirectorySeparatorChar);
        string Move(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            var full = Path.GetFullPath(path);
            return full.StartsWith(live + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(work).TrimEnd(Path.DirectorySeparatorChar) + full[live.Length..]
                : path;
        }
        cfg.SyslayPath2 = Move(cfg.SyslayPath2);
        cfg.SysresPath2 = Move(cfg.SysresPath2);
        if (string.IsNullOrEmpty(cfg.SyslayPath2))
            throw new InvalidOperationException("mapper_config.json declares no syslay path to retarget.");
    }

    // ---- the fixtures the compiler must survive --------------------------------------------------

    private static int Determinism()
    {
        int failures = 0;
        // A refused twin produces no tree; its refusal is proved in the generation stage.
        foreach (var model in Fixtures.Models.Where(m => !m.IsNegativeFixture))
            foreach (var selection in Fixtures.Selections)
            {
                var (name, controller) = (model.Name, selection.Name);
                Console.Write($"  {Label(name),-16} {controller,-6} ");
                var first = Under("work", "det1", name, controller);
                var second = Under("work", "det2", name, controller);
                var e1 = Generate(model, selection.RevPi, first, seed: true);
                var e2 = Generate(model, selection.RevPi, second, seed: true);
                if (e1 != null || e2 != null)
                {
                    Console.WriteLine($"GENERATION FAILED: {e1 ?? e2}");
                    failures++;
                    continue;
                }
                int diff = Diff(Hash(first), Hash(second));
                Console.WriteLine(diff == 0 ? "IDENTICAL" : $"*** {diff} ARTEFACT(S) DIFFER ***");
                if (diff != 0) failures++;
            }
        return failures;
    }

    // Generate A, then B, then A again into the SAME tree. The third result must equal the first, or a
    // generation is carrying state from the one before it.
    private static int AbaClosure()
    {
        var work = Under("work", "aba");
        Seed(work);
        // Two DIFFERENT combinations, so the middle run can leave something behind for the third to
        // find. A manifest with one of either still closes the loop; it just proves less.
        // Two DIFFERENT COMPILABLE combinations: a refused twin never writes a tree, so it can
        // neither leave residue behind nor prove that the third run found none.
        var usable = Fixtures.Models.Where(m => !m.IsNegativeFixture).ToList();
        if (usable.Count == 0)
        {
            Console.WriteLine("  NO COMPILABLE MODEL: every twin in the manifest is a negative fixture.");
            return 1;
        }
        var a = (usable[0], Fixtures.Selections[0].RevPi);
        var b = (usable[^1], Fixtures.Selections[^1].RevPi);
        var order = new[] { a, b, a };
        var results = new List<IReadOnlyDictionary<string, string>>();
        foreach (var (model, revPi) in order)
        {
            var error = Generate(model, (IReadOnlyCollection<string>)revPi, work, seed: false);
            if (error != null) { Console.WriteLine($"  GENERATION FAILED: {error}"); return 1; }
            results.Add(Hash(work));
        }
        int diff = Diff(results[0], results[2]);
        Console.WriteLine(diff == 0
            ? "  CLOSED: the third run is byte-identical to the first"
            : $"  *** {diff} ARTEFACT(S) CARRIED STATE ***");
        return diff == 0 ? 0 : 1;
    }

    // A process runs on whichever target the roster gives it, and that has to be true of the EMITTED
    // project rather than of the plan alone. One synthetic plant is compiled once per target with only
    // its roster row moving; the emitted resource must carry the process FB, its recipe, its place in
    // the init chain, its place on the report ring and its resource wiring - and no other resource may
    // carry any of it. Nothing here names a target: the list comes from the layout the compiler reads.
    private static int Placement()
    {
        var layoutPath = Path.Combine(AppContext.BaseDirectory, "Config", "layout.yml");
        var original = File.ReadAllBytes(layoutPath);
        int failures = 0;
        try
        {
            Directory.CreateDirectory(Under("inputs"));
            var control = Path.Combine(Under("inputs"), "placement.Control.xml");

            foreach (var target in LayoutTargets(Encoding.UTF8.GetString(original)))
            {
                Console.Write($"  {target,-16} ");

                // A target that only exists when work is RELOCATED onto it is reached the way it is
                // reached in production: the roster places the plant on the feed target and the run
                // selects it. And where the target's hardware declares which components it can serve,
                // the plant's two components take those names - the plant itself is the same either
                // way. Both are declared capabilities, so nothing here names a controller.
                var descriptor = Declarations.Targets.Of(PlcAssignment.Named(target));
                bool relocated = descriptor.StandsInFor != null;
                var names = relocated
                    ? PlacementFixture.Write(control,
                        RevPiIoBrokerInjector.CoveredActuators(Declarations).FirstOrDefault(),
                        RevPiIoBrokerInjector.CoveredSensors(Declarations).FirstOrDefault())
                    : PlacementFixture.Write(control);
                var rostered = relocated ? descriptor.StandsInFor!.Value.ToString() : target;
                var selection = relocated ? names : Array.Empty<string>();

                File.WriteAllText(layoutPath, WithFixtureOn(Encoding.UTF8.GetString(original), rostered, names));
                var work = Under("work", "placement", target);
                var error = GenerateControl(control, selection, work, seed: true);
                if (error != null) { Console.WriteLine($"GENERATION FAILED: {error}"); failures++; continue; }

                var problems = Placed(work, names);
                Console.WriteLine(problems.Count == 0
                    ? "ON ITS OWN RESOURCE"
                    : "*** " + string.Join("; ", problems) + " ***");
                if (problems.Count > 0) failures++;
            }
        }
        finally { File.WriteAllBytes(layoutPath, original); }
        return failures;
    }

    // The resources the layout declares, read from the layout itself so a new target is gated the day
    // it is declared rather than the day someone remembers to add it here.
    private static IReadOnlyList<string> LayoutTargets(string layout)
    {
        var targets = new List<string>();
        bool inResources = false;
        foreach (var raw in layout.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("resources:", StringComparison.Ordinal)) { inResources = true; continue; }
            if (inResources && line.Length > 0 && !char.IsWhiteSpace(line[0])) break;
            if (!inResources) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*-\s*plc:\s*([A-Za-z0-9_]+)");
            if (m.Success) targets.Add(m.Groups[1].Value);
        }
        return targets;
    }

    // The fixture's two roster rows, on the target under test. A roster row is DATA: this is the whole
    // difference between one run and the next.
    private static string WithFixtureOn(string layout, string target, IReadOnlyList<string> names)
    {
        var kinds = new[] { "Process", "Actuator", "Sensor" };
        var rows = string.Concat(names.Select((name, i) =>
            // A name the roster already declares is already placed, and a second row for it would be
            // two answers to the same question.
            layout.Contains($"name: {name},", StringComparison.Ordinal)
                ? string.Empty
                : $"  - {{ name: {name}, plc: {target}, column: 9, row: {kinds[i]} }}" + "\n"));
        int at = layout.IndexOf("components:", StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("layout.yml declares no components roster.");
        int eol = layout.IndexOf('\n', at) + 1;
        return layout[..eol] + rows + layout[eol..];
    }

    // What the emitted project has to say about the process, resource by resource.
    private static List<string> Placed(string work, IReadOnlyList<string> names)
    {
        var (process, actuator) = (names[0], names[1]);
        var problems = new List<string>();
        var eae = Directory.EnumerateDirectories(work).Select(d => Path.Combine(d, "IEC61499")).FirstOrDefault(Directory.Exists);
        if (eae == null) return new List<string> { "no IEC61499 tree was written" };

        var syslay = Directory.EnumerateFiles(eae, "*.syslay", SearchOption.AllDirectories).ToList();
        if (syslay.Count == 0) return new List<string> { "no application layout was written" };
        var application = string.Join("\n", syslay.Select(File.ReadAllText));
        if (!application.Contains($"Name=\"{process}\"", StringComparison.Ordinal))
            problems.Add("the application carries no process FB");

        string? owning = null;
        var claimed = new List<string>();
        foreach (var sysres in Directory.EnumerateFiles(eae, "*.sysres", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(sysres);
            if (!text.Contains($"Name=\"{process}\"", StringComparison.Ordinal)) continue;
            claimed.Add(Path.GetFileNameWithoutExtension(sysres));
            owning = text;
        }
        if (claimed.Count == 0) problems.Add("no resource carries the process");
        if (claimed.Count > 1) problems.Add($"{claimed.Count} resources claim the process: {string.Join(", ", claimed)}");
        if (owning == null) return problems;

        // The four things a process needs to actually run, on the resource that owns it.
        if (!owning.Contains("Type=\"Process1_Generic\"", StringComparison.Ordinal))
            problems.Add("the process FB is not the process runtime type");
        if (!owning.Contains("Name=\"Recipe\"", StringComparison.Ordinal))
            problems.Add("the process carries no recipe");
        if (!owning.Contains($"Destination=\"{process}.INIT\"", StringComparison.Ordinal))
            problems.Add("nothing initialises the process");
        if (!owning.Contains($"{process}.stateRptCmdAdptr", StringComparison.Ordinal))
            problems.Add("the process is not on the report ring");
        if (!owning.Contains($"Name=\"{actuator}\"", StringComparison.Ordinal))
            problems.Add("the actuator it drives is on another resource");

        return problems;
    }

    // ---- comparison ------------------------------------------------------------------------------

    private static int Compare(string a, string b, bool coreOnly)
    {
        int diverged = 0;
        // A refused twin produces no tree; its refusal is proved in the generation stage.
        foreach (var model in Fixtures.Models.Where(m => !m.IsNegativeFixture))
            foreach (var selection in Fixtures.Selections)
            {
                var (name, controller) = (model.Name, selection.Name);
                var fa = Path.Combine(Under("baselines", a), name, controller + ".sha256");
                var fb = Path.Combine(Under("baselines", b), name, controller + ".sha256");
                Console.Write($"  {Label(name),-16} {controller,-6} ");
                if (!File.Exists(fa) || !File.Exists(fb))
                {
                    Console.WriteLine("MISSING SNAPSHOT");
                    diverged++;
                    continue;
                }
                var ha = Read(fa, coreOnly);
                var hb = Read(fb, coreOnly);
                int diff = Diff(ha, hb);
                Console.WriteLine(diff == 0 ? $"IDENTICAL ({ha.Count} artefacts)" : $"*** {diff} DIFFERING ***");
                if (diff != 0) diverged++;
            }
        Console.WriteLine(diverged == 0
            ? "  [gate] BYTE-IDENTICAL across all combinations"
            : $"  [gate] {diverged} combination(s) DIVERGED");
        return diverged;
    }

    // The HMI project and the per-CAT canvas list it writes are owned elsewhere, so a core comparison
    // leaves them out rather than reporting another module's work as a regression.
    private static IReadOnlyDictionary<string, string> Read(string path, bool coreOnly)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            int sep = line.IndexOf("  ", StringComparison.Ordinal);
            if (sep <= 0) continue;
            var rel = line[(sep + 2)..];
            if (coreOnly && IsOwnedElsewhere(rel)) continue;
            map[rel] = line[..sep];
        }
        return map;
    }

    private static bool IsOwnedElsewhere(string rel) =>
        rel.Contains("/HMI/", StringComparison.OrdinalIgnoreCase) ||
        rel.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);

    private static int Diff(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        int n = 0;
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            a.TryGetValue(key, out var x);
            b.TryGetValue(key, out var y);
            if (x == y) continue;
            if (n < 8) Console.WriteLine($"       {key}  {x ?? "(absent)"} != {y ?? "(absent)"}");
            n++;
        }
        return n;
    }

    // ---- artefacts -------------------------------------------------------------------------------

    // Every control and deployment artefact. EAE's own build output is the only exclusion, because the
    // toolchain regenerates it rather than this compiler.
    private static readonly HashSet<string> Artefacts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".syslay", ".sysres", ".sysdev", ".sysapp", ".hcf", ".fbt", ".dt", ".adp",
        ".json", ".xml", ".dfbproj", ".topologyproj", ".hwconfigproj",
        ".cs", ".csproj", ".sln", ".cfg", ".prj", ".solutionData",
    };

    private static IReadOnlyDictionary<string, string> Hash(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return map;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) ||
                rel.Contains("/obj/", StringComparison.Ordinal) ||
                rel.Contains("SnapshotCompiles/", StringComparison.Ordinal) ||
                rel.Contains("RuntimeData/", StringComparison.Ordinal)) continue;
            if (!Artefacts.Contains(Path.GetExtension(file))) continue;
            using var stream = File.OpenRead(file);
            map[rel] = Convert.ToHexString(SHA256.HashData(stream));
        }
        return map;
    }

    // ---- filesystem ------------------------------------------------------------------------------

    // Every destination is asserted to live under the gate root, so a mistake here cannot reach the
    // live EAE project.
    private static string Under(params string[] parts)
    {
        var full = Path.GetFullPath(Path.Combine(new[] { GateRoot }.Concat(parts).ToArray()));
        var root = Path.GetFullPath(GateRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refusing to write outside the gate root: {full}");
        return full;
    }

    // A fixed baseline tree, taken from the live project ONCE and never written back, so every run
    // starts from the same EAE project rather than from whatever the last one left behind.
    private static void Seed(string work)
    {
        var seed = Under("base");
        if (!Directory.Exists(seed) || !Directory.EnumerateFileSystemEntries(seed).Any())
        {
            var live = LiveProject;
            if (!Directory.Exists(live))
                throw new InvalidOperationException(
                    $"no baseline at {seed}, and no live project at {live} to take one from.");
            Console.WriteLine($"  [gate] seeding the baseline tree from {live} (read-only)");
            CopyTree(live, seed);
        }
        if (Directory.Exists(work)) Directory.Delete(work, true);
        CopyTree(seed, work);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
