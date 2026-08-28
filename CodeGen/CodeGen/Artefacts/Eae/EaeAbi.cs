namespace CodeGen.Artefacts
{
    // WHAT EAE 24.1 FIXES, IN ONE PLACE.
    //
    // These are not deployment choices and not plant facts: they are the vendor's own vocabulary, and
    // changing one does not configure anything - it produces a project EAE will not load. So they stay
    // typed code rather than moving to YAML, and they are stated ONCE rather than restated at each
    // emitter that happens to need them. Seven files used to spell the null UUID for themselves.
    //
    // Each MEANING is named separately even where the value is shared. They are the same string for
    // unrelated reasons, and a future EAE that separated them would be a change to one of them only.
    public static class EaeAbi
    {
        /// EAE's null UUID. Never a real identity - always one of the sentinels below.
        public const string NullUuid = "00000000-0000-0000-0000-000000000000";

        /// The one system every generated project carries. Its .system file name is derived from it.
        public const string SystemId = NullUuid;

        /// The one application, inside that system.
        public const string ApplicationId = "00000000-0000-0000-0000-000000000001";

        /// The system's project file: TopologyManager binds a logical device to its system through
        /// a DependentUpon naming exactly this, so a sysdev that names anything else is filtered out
        /// of Deploy & Diagnostic as orphaned.
        public const string SystemFileName = SystemId + SystemExtension;

        /// NOCONF: a device with no broadcast-domain binding. Not "unknown" - explicitly unbound.
        public const string NoBroadcastDomain = NullUuid;

        /// The solution id read back from a project that carries none, so an emitted reference to it
        /// is inert rather than pointing at some other solution.
        public const string UnknownSolution = NullUuid;

        // ---- artefact extensions ---------------------------------------------------------------
        public const string SystemExtension      = ".system";
        public const string ApplicationExtension = ".sysapp";
        public const string LayoutExtension      = ".syslay";

        // ---- vendor type identities --------------------------------------------------------------

        /// The namespace every device EAE deploys to lives in.
        public const string DeviceNamespace = "SE.DPAC";

        /// The embedded resource type a device's resource is, and the namespace that declares it.
        /// A device carrying two of these is what EAE reports as "contains 2 instances of EMB_RES_ECO".
        public const string EmbeddedResourceType = "EMB_RES_ECO";
        public const string RuntimeNamespace     = "Runtime.Management";

        /// The runtime library the boot sequence's start FB comes from.
        public const string AppBaseNamespace = "SE.AppBase";
    }
}
