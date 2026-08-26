using System.Collections.Generic;

namespace CodeGen.Configuration
{
    // Config/templates.yml, typed. One row per FB type the Mapper owns and the whole of its contract;
    // TemplateIndex is the run's resolved view over it. Declaration order is preserved because it is
    // deploy order and it breaks graph-shape ties.
    public sealed class TemplateCatalog
    {
        public List<TemplateDeclaration> Templates { get; set; } = new();

        private static readonly YamlConfigFile<TemplateCatalog> _file =
            new("Config", "templates.yml");

        public static TemplateCatalog Current => _file.Load();

        /// The same declaration read from a run's OWN profile bundle. A root of null is the
        /// bundle shipped beside CodeGen.dll, which is what a normal run reads.
        public static TemplateCatalog LoadFrom(string? root) => _file.Load(root);
    }

    // Where an artefact lives, which also fixes deploy order.
    public enum TemplateArtefactKind { Basic, Adapter, Composite, HmiCat, Cat, DataType }

    // What the emitted FB IS to the generator. Drives ring/station wiring, not deployment.
    public enum TemplateRole { Actuator, Sensor, Process, Infrastructure }

    public sealed class TemplateDeclaration
    {
        public string Name { get; set; } = string.Empty;
        public TemplateArtefactKind Kind { get; set; }
        public TemplateRole Role { get; set; }
        public bool Deploy { get; set; }
        public bool MirrorToSysres { get; set; }
        public bool Emitted { get; set; }
        public bool ForceRefresh { get; set; }
        public bool DeployLast { get; set; }
        public bool HmiFaceplate { get; set; }
        public bool SensorTimed { get; set; }
        public bool StationAdapter { get; set; } = true;
        public bool SymlinkQi { get; set; }
        public List<string> Ports { get; set; } = new();
        public string? NameParameter { get; set; }
        public List<string> InfraRoles { get; set; } = new();

        // The namespace an emitted instance of this type carries. Empty means the project's own
        // (config.yaml projectNamespace); a library FB names the library it is declared in.
        public string? Namespace { get; set; }
        public TelemetryTapDeclaration? Telemetry { get; set; }
        public CatProtocolDeclaration? Protocol { get; set; }
        public PhaseHandoffDeclaration? PhaseHandoff { get; set; }
    }

    // The process type's cross-controller phase-announcement contract: the ports a producer raises on
    // and the ports plus receiver slot a consumer answers with.
    public sealed class PhaseHandoffDeclaration
    {
        public string CommandToken { get; set; } = string.Empty;
        public string EventOut { get; set; } = string.Empty;
        public string DataOut { get; set; } = string.Empty;
        public string EventIn { get; set; } = string.Empty;
        public string DataIn { get; set; } = string.Empty;
        public string ReceiverSlotParam { get; set; } = string.Empty;
        public int ProducersPerConsumer { get; set; }
    }

    // Where a CAT's own state lives, so a publisher is wired in without the deployer knowing the CAT.
    public sealed class TelemetryTapDeclaration
    {
        public string StateEventSource { get; set; } = string.Empty;
        public string StateDataSource { get; set; } = string.Empty;
        public string InitSource { get; set; } = string.Empty;
        public string TopicNameSource { get; set; } = string.Empty;

        // Appended to telemetry.yml's topicRoot for this type's publisher. Empty means the root itself;
        // a type that publishes something other than a component's own state names its own subtree.
        public string TopicSuffix { get; set; } = string.Empty;

        // A boundary InputVar the publisher's own source depends on, carried into the same internal
        // FB. Declared here so it is re-asserted in a deterministic position beside the publisher's
        // wires rather than wherever the patch that created it happened to leave it. Empty = none.
        public string RowDataVar { get; set; } = string.Empty;

        // Where the publisher pair is drawn inside this type. A coordinate is part of the artefact,
        // so it is declared rather than assumed.
        public int PublisherY { get; set; } = 2580;
    }
}
