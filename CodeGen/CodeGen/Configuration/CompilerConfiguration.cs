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
    // It carries no decisions of its own: it is the declarations, joined and INDEXED, so that one fact
    // has one owner and no planner, emitter, patcher or validator has to know which file it came from.
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
        public SecurityProfile Security { get; }                 // Config/security.yml

        // THE RESOLVED VIEWS, built from the declarations above and belonging to this snapshot alone.
        //
        // These were static classes that computed their derived sets on first touch. Whichever run got
        // there first decided what every later run in the process saw, so a second profile - another
        // twin, a concurrent generation, a test with its own bundle - compiled half against its own
        // declarations and half against the first run's. Both halves were individually valid, so
        // nothing reported it. Resolving them per snapshot is what makes that unrepresentable.
        public Mapping.TemplateIndex Manifest { get; }
        public Mapping.TargetIndex Targets { get; }

        private CompilerConfiguration(MapperConfig paths, DeviceConfig devices,
            GenerationConfig generation, TelemetrySettings telemetry, RigCatalog rig,
            Translation.Interlocks.InterlockConfig interlocks, TemplateCatalog templates,
            LayoutCatalog layout, SecurityProfile security)
        {
            Paths = paths;
            Devices = devices;
            Generation = generation;
            Telemetry = telemetry;
            Rig = rig;
            Interlocks = interlocks;
            Templates = templates;
            Layout = layout;
            Security = security;

            // templates.yml is validated against smc-rig.yml HERE rather than inside either loader,
            // because a cross-file rule needs both files - and a loader that reached for the other
            // file's process-wide singleton would check this run's templates against another run's roles.
            TemplateCatalogValidator.Validate(templates, rig);

            Manifest = new Mapping.TemplateIndex(templates, rig, generation.ProjectNamespace);
            Targets = new Mapping.TargetIndex(devices, Manifest);
        }

        // The same declarations against a different roster. Returns a NEW snapshot rather than
        // mutating this one, so a caller that overrides the layout cannot change what another
        // caller is compiling against.
        // The same declarations addressing a different output tree. Used by the transaction to point
        // a run at its staging copy; every other path the compiler resolves is derived from these.
        public CompilerConfiguration With(MapperConfig paths) =>
            new(paths ?? throw new ArgumentNullException(nameof(paths)),
                Devices, Generation, Telemetry, Rig, Interlocks, Templates, Layout, Security);

        // The same run against different device declarations. A caller that needs to ask "what would
        // this validator say about THESE addresses" gets a new snapshot rather than mutating a shared
        // one, so two callers cannot see each other's overrides.
        public CompilerConfiguration With(DeviceConfig devices) =>
            new(Paths, devices ?? throw new ArgumentNullException(nameof(devices)),
                Generation, Telemetry, Rig, Interlocks, Templates, Layout, Security);

        public CompilerConfiguration With(LayoutCatalog layout) =>
            new(Paths, Devices, Generation, Telemetry, Rig, Interlocks, Templates,
                layout ?? throw new ArgumentNullException(nameof(layout)), Security);

        // THE PROCESS-WIDE SNAPSHOT OF THE SHIPPED BUNDLE. It exists for the compatibility facades
        // ONLY - MapperConfig's forwarders and ControllerMap, both of which the separately-owned HMI
        // module and the prebuilt VueOne runner link against by exact signature and so cannot be
        // handed a run's own snapshot. Nothing in the compiler may read it: a generation carries its
        // configuration, and ArchitectureTests fails the build if a planner reaches for this instead.
        static readonly Lazy<CompilerConfiguration> _default = new(() => Load(new MapperConfig()));
        internal static CompilerConfiguration Default => _default.Value;

        // THE ONE PLACE the declaration files are read for a run. Each loader validates its own file
        // as it loads, so an invalid declaration stops the run here - before a plan exists, and so
        // before anything is written.
        //
        // profileRoot names a run's OWN bundle: the directory holding its Config folder. Null is the
        // bundle shipped beside CodeGen.dll, which is what a normal run reads. A named root is read
        // fresh and shared with nothing, which is what lets two runs hold different declarations at
        // the same time without either one seeing the other's.
        public static CompilerConfiguration Load(MapperConfig paths, string? profileRoot = null) =>
            new(paths ?? throw new ArgumentNullException(nameof(paths)),
                DeviceConfig.LoadFrom(profileRoot),
                GenerationConfig.LoadFrom(profileRoot),
                TelemetrySettings.LoadFrom(profileRoot),
                RigCatalog.LoadFrom(profileRoot),
                Translation.Interlocks.InterlockConfig.LoadFrom(profileRoot),
                TemplateCatalog.LoadFrom(profileRoot),
                LayoutCatalog.LoadFrom(profileRoot),
                SecurityProfile.LoadFrom(profileRoot));
    }
}
