using System.Collections.Generic;

namespace CodeGen.Hmi
{
    // Nullable transport types for Config/hmi.yml.
    //
    // Every member is nullable on purpose: absence must be distinguishable from a legal zero so the
    // validator can reject a missing required value instead of silently substituting a default. No
    // default lives here or anywhere in C# - hmi.yml is the only source, which is what stops the
    // configuration and the code drifting apart.
    internal sealed class HmiDefinitionDto
    {
        public int? SchemaVersion { get; set; }
        public PolicyDto? Policy { get; set; }
        public GeometryDto? Geometry { get; set; }
        public StyleDto? Style { get; set; }
        public ScreensDto? Screens { get; set; }
        public ProtocolDto? Protocol { get; set; }
        public CapabilitiesDto? Capabilities { get; set; }
        public RuntimeDto? Runtime { get; set; }
        public DeploymentDto? Deployment { get; set; }
        public IdentitiesDto? Identities { get; set; }
        public NetworkDto? Network { get; set; }
        public LibraryDto? Library { get; set; }
        public LogicalDeviceDto? LogicalDevice { get; set; }
        public List<ArtefactDto>? Artefacts { get; set; }
        public SwitchDto? Switch { get; set; }

        internal sealed class IdentitiesDto
        {
            public string? Device { get; set; }
            public string? Workstation { get; set; }
            public string? WorkstationNic { get; set; }
            public string? WorkstationRuntime { get; set; }
            public string? Panel { get; set; }
            public string? PanelRuntime { get; set; }
            public string? PanelContainer { get; set; }
            public string? PanelContainerRuntime { get; set; }
            public string? RuntimeType { get; set; }
            public string? ContainerRuntimeType { get; set; }
            public string? SimulationService { get; set; }
            public string? NullDevice { get; set; }
        }

        internal sealed class NetworkDto
        {
            public string? HostIp { get; set; }
            public string? InternalRuntimeIp { get; set; }
            public string? Subnet { get; set; }
            public int? LogicalPort { get; set; }
            public int? SecurePort { get; set; }
        }

        internal sealed class LibraryDto
        {
            public string? Name { get; set; }
            public string? Version { get; set; }
        }

        internal sealed class LogicalDeviceDto
        {
            public string? Type { get; set; }
            public string? Name { get; set; }
        }

        internal sealed class ArtefactDto
        {
            public string? Template { get; set; }
            public string? Into { get; set; }
            public string? Name { get; set; }
            public string? Register { get; set; }
        }

        internal sealed class SwitchDto
        {
            public string? EquipmentFile { get; set; }
        }

        internal sealed class RuntimeDto
        {
            public string? SchemaVersion { get; set; }
            public string? StartCanvas { get; set; }
            public ResolutionDto? Resolution { get; set; }
            public ResolutionDto? FallbackResolution { get; set; }
            public ChromeDto? Chrome { get; set; }

            internal sealed class ResolutionDto
            {
                public string? Name { get; set; }
                public string? Template { get; set; }
                public string? ResizeBehaviour { get; set; }
                public int? CanvasButtonHeight { get; set; }
                public int? SiblingButtonCount { get; set; }
                public int? ChildButtonCount { get; set; }
            }

            internal sealed class ChromeDto
            {
                public bool? Logger { get; set; }
                public bool? Login { get; set; }
                public bool? CurrentUser { get; set; }
                public bool? LanguageButton { get; set; }
                public bool? RuntimeConnection { get; set; }
                public bool? NavigationBar { get; set; }
                public bool? NewVersionDeployed { get; set; }
                public int? NavigationControl { get; set; }
            }
        }

        internal sealed class PolicyDto
        {
            public bool? ReadOnly { get; set; }
            public string? UnknownContract { get; set; }
            public string? BannerText { get; set; }
            public string? UnsupportedCommandNotice { get; set; }
            public Dictionary<string, string>? UnavailableReasons { get; set; }
        }

        internal sealed class GeometryDto
        {
            public int? CanvasWidth { get; set; }
            public int? CanvasHeight { get; set; }
            public int? NavigationBarHeight { get; set; }
            public int? Margin { get; set; }
            public int? Gap { get; set; }
            public int? CaptionHeight { get; set; }
            public int? BannerY { get; set; }
            public int? TitleY { get; set; }
            public int? ContentTop { get; set; }
            public ButtonDto? Button { get; set; }
            public SizeDto? SymbolFallback { get; set; }

            internal sealed class ButtonDto
            {
                public int? Width { get; set; }
                public int? Height { get; set; }
                public int? BottomInset { get; set; }
            }
        }

        internal sealed class SizeDto
        {
            public int? Width { get; set; }
            public int? Height { get; set; }
        }

        internal sealed class FontDto
        {
            public string? Family { get; set; }
            public double? Size { get; set; }
            public bool? Bold { get; set; }
        }

        internal sealed class ColorDto
        {
            public int? R { get; set; }
            public int? G { get; set; }
            public int? B { get; set; }
        }

        internal sealed class StyleDto
        {
            public string? CanvasBrush { get; set; }
            public string? ButtonBrush { get; set; }
            public FontDto? ButtonFont { get; set; }
            public FontDto? CaptionFont { get; set; }
            public FontDto? DetailFont { get; set; }
            public ColorDto? ButtonTextColor { get; set; }
            public ColorDto? CaptionColor { get; set; }
            public ColorDto? EmphasisColor { get; set; }
            public ColorDto? DetailColor { get; set; }
        }

        internal sealed class ScreensDto
        {
            public string? HubName { get; set; }
            public string? HubTitle { get; set; }
            public string? ResidualName { get; set; }
            public string? ResidualTitle { get; set; }
            public string? Suffix { get; set; }
            public string? HubButtonText { get; set; }
            public string? PreviousText { get; set; }
            public string? NextText { get; set; }
            public DetailDto? Detail { get; set; }

            internal sealed class DetailDto
            {
                public bool? ShowStates { get; set; }
                public bool? ShowInterlocks { get; set; }
                public bool? ShowAllocation { get; set; }
                public bool? ShowProtocolLegend { get; set; }
                public int? MaxStateChars { get; set; }
                public int? MaxInterlockChars { get; set; }
                public int? MaxProtocolChars { get; set; }
            }
        }

        internal sealed class ValueLabelDto
        {
            public int? Value { get; set; }
            public string? Label { get; set; }
        }

        internal sealed class ProtocolDto
        {
            public List<ValueLabelDto>? Modes { get; set; }
            public List<ValueLabelDto>? CycleTypes { get; set; }
            public List<StatesProfileDto>? StatesProfiles { get; set; }

            internal sealed class StatesProfileDto
            {
                public string? Id { get; set; }
                public MatchDto? Match { get; set; }
                public List<string>? Labels { get; set; }

                internal sealed class MatchDto
                {
                    public List<string>? InputEventCarries { get; set; }
                }
            }
        }

        internal sealed class CapabilitiesDto
        {
            public List<CommandDto>? Commands { get; set; }
            public InterlockDto? InterlockDiagnostics { get; set; }
            public EngineProbeDto? EngineProbe { get; set; }

            internal sealed class CommandDto
            {
                public string? Purpose { get; set; }
                public string? OutputEvent { get; set; }
                public List<string>? OutputData { get; set; }
                public List<List<string>>? OutputDataVariants { get; set; }
                public string? AlsoRequiresOutputEvent { get; set; }
                public List<string>? AnyFeedback { get; set; }
                public bool? NeedsModeChain { get; set; }
                public bool? NeedsRecipeEngine { get; set; }
            }

            internal sealed class InterlockDto
            {
                public List<string>? RequiredFeedback { get; set; }
            }

            internal sealed class EngineProbeDto
            {
                public string? TypeName { get; set; }
                public List<string>? AutoGatingTokens { get; set; }
                public List<string>? ManualSteppingTokens { get; set; }
            }
        }

        internal sealed class DeploymentDto
        {
            public string? HmiFolderName { get; set; }
            public string? CanvasNamespaceSuffix { get; set; }
            public string? SymbolNamespaceSuffix { get; set; }
            public string? DefaultLibraryNamespace { get; set; }
            public string? GeneratedBanner { get; set; }
            public string? OwnershipManifest { get; set; }
            public string? PrimarySymbol { get; set; }
            public List<string>? CommandSymbols { get; set; }
        }
    }
}
