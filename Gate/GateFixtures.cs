using System.Text.Json;
using System.Text.Json.Serialization;
using CodeGen.Configuration;
using CodeGen.Devices.Core;

namespace VueOneMapper.Gate;

// What the gate is asked to prove, as data. Which twins, which per-component target selections and
// which EAE project to start every run from are FIXTURE facts, not compiler facts, so they live in a
// checked-in manifest beside the gate rather than as literals in it. That is what makes a gate run
// reproducible on a machine that is not the one it was written on.
internal sealed class GateFixtures
{
    // Where the twins live. Relative resolves against the manifest, so the checked-in default is the
    // repository's own fixture folder; VUEONE_MODELS points a run at authored twins elsewhere.
    public string ModelsRoot { get; set; } = string.Empty;

    // How a twin's Control.xml is found under the models root, with {model} substituted.
    public string ControlPath { get; set; } = "{model}/Control.xml";

    public List<GateModel> Models { get; set; } = new();

    // The target-selection axis. RevPi lists the components a run relocates, which is a property of
    // the twin being gated, so it is declared here rather than named in code.
    public List<GateSelection> Selections { get; set; } = new();

    // The EAE project a run starts from and must never write. Empty means "derive it from the
    // configured output root", which is the answer for anyone running against their own project.
    public string LiveProject { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static GateFixtures Load()
    {
        var path = Environment.GetEnvironmentVariable("VUEONE_GATE_FIXTURES");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(AppContext.BaseDirectory, "gate.fixtures.json");
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"the gate fixture manifest is missing: {path}. It declares which twins and which " +
                "target selections a run gates; set VUEONE_GATE_FIXTURES to point at one.");

        var fixtures = JsonSerializer.Deserialize<GateFixtures>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"the gate fixture manifest is empty: {path}");
        fixtures.Resolve(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return fixtures;
    }

    private void Resolve(string manifestDir)
    {
        var external = Environment.GetEnvironmentVariable("VUEONE_MODELS");
        if (!string.IsNullOrWhiteSpace(external)) ModelsRoot = external;
        if (string.IsNullOrWhiteSpace(ModelsRoot))
            throw new InvalidOperationException(
                "the gate fixture manifest declares no modelsRoot; set one, or set VUEONE_MODELS.");
        if (!Path.IsPathRooted(ModelsRoot))
            ModelsRoot = Path.GetFullPath(Path.Combine(manifestDir, ModelsRoot));

        if (Models.Count == 0)
            throw new InvalidOperationException("the gate fixture manifest declares no models to gate.");
        if (Selections.Count == 0)
            throw new InvalidOperationException("the gate fixture manifest declares no target selections.");

        var missing = Models.Where(m => !File.Exists(ControlFor(m))).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "these gate fixtures have no Control.xml: " +
                string.Join(", ", missing.Select(m => ControlFor(m))) +
                ". Put the twins under the manifest's modelsRoot, or set VUEONE_MODELS to where they live.");

        var live = Environment.GetEnvironmentVariable("VUEONE_LIVE_PROJECT");
        if (!string.IsNullOrWhiteSpace(live)) LiveProject = live;
        if (string.IsNullOrWhiteSpace(LiveProject)) LiveProject = DeriveLiveProject();
        LiveProject = Path.GetFullPath(LiveProject).TrimEnd(Path.DirectorySeparatorChar);
    }

    public string ControlFor(GateModel model) =>
        Path.GetFullPath(Path.Combine(ModelsRoot, ControlPath.Replace("{model}", model.Name)));

    // The project the configured output root belongs to. Everything the gate protects and everything
    // it retargets is derived from this one value, so protection holds for whatever project a machine
    // is actually configured against.
    private static string DeriveLiveProject()
    {
        var cfg = MapperConfig.Load();
        var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
        if (string.IsNullOrEmpty(eaeRoot))
            throw new InvalidOperationException(
                "the gate cannot derive the live project from mapper_config.json, so it cannot tell " +
                "which tree to protect. Set liveProject in the fixture manifest, or VUEONE_LIVE_PROJECT.");
        return Directory.GetParent(eaeRoot)?.FullName ?? eaeRoot;
    }
}

internal sealed class GateModel
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class GateSelection
{
    public string Name { get; set; } = string.Empty;
    public List<string> RevPi { get; set; } = new();
}
