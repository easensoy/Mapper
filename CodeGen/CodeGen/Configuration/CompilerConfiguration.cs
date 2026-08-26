using System;

namespace CodeGen.Configuration
{
    // Every validated declaration one generation compiles against, resolved ONCE at the composition
    // root and never re-read below it.
    //
    // The catalogs behind this are mtime-cached files. Asking them again mid-run is what lets two
    // stages of the same generation see two different configurations - a file saved while a run is in
    // flight would change the recipe after the interlocks were planned against the old one. A run
    // takes this snapshot at the start and every stage reads it, so that cannot happen.
    //
    // It carries no decisions of its own: it is the declarations, joined, so that one fact has one
    // owner and no planner, emitter, patcher or validator has to know which file it came from.
    public sealed class CompilerConfiguration
    {
        // Machine-local paths: where THIS installation keeps its library, its authored hardware
        // configs and the project it writes. Deployment facts live in the YAML below, not here.
        public MapperConfig Paths { get; }

        public DeviceConfig Devices { get; }                     // Config/device.yml
        public GenerationConfig Generation { get; }              // Config/config.yaml
        public TelemetrySettings Telemetry { get; }              // Config/telemetry.yml
        public RigCatalog Rig { get; }                           // Config/smc-rig.yml
        public Translation.Interlocks.InterlockConfig Interlocks { get; }  // Config/interlock.yaml
        public TemplateCatalog Templates { get; }                // Config/templates.yml
        public LayoutCatalog Layout { get; }                     // Config/layout.yml

        private CompilerConfiguration(MapperConfig paths, DeviceConfig devices,
            GenerationConfig generation, TelemetrySettings telemetry, RigCatalog rig,
            Translation.Interlocks.InterlockConfig interlocks, TemplateCatalog templates,
            LayoutCatalog layout)
        {
            Paths = paths;
            Devices = devices;
            Generation = generation;
            Telemetry = telemetry;
            Rig = rig;
            Interlocks = interlocks;
            Templates = templates;
            Layout = layout;
        }

        // The same declarations against a different roster. Returns a NEW snapshot rather than
        // mutating this one, so a caller that overrides the layout cannot change what another
        // caller is compiling against.
        public CompilerConfiguration With(LayoutCatalog layout) =>
            new(Paths, Devices, Generation, Telemetry, Rig, Interlocks, Templates,
                layout ?? throw new ArgumentNullException(nameof(layout)));

        // THE ONE PLACE the declaration files are read for a run. Each loader validates its own file
        // as it loads, so an invalid declaration stops the run here - before a plan exists, and so
        // before anything is written.
        public static CompilerConfiguration Load(MapperConfig paths) =>
            new(paths ?? throw new ArgumentNullException(nameof(paths)),
                DeviceConfig.Current,
                GenerationConfig.Current,
                TelemetrySettings.Current,
                RigCatalog.Current,
                Translation.Interlocks.InterlockConfig.Current,
                TemplateCatalog.Current,
                LayoutCatalog.Load());
    }
}
