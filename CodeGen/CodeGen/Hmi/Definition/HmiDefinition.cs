using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // The validated, immutable HMI definition. Every presentation, protocol and capability value the
    // generator uses comes from here; nothing downstream carries a fallback of its own.

    internal sealed record HmiFont(string Family, double Size, bool Bold);

    internal sealed record HmiColor(int R, int G, int B);

    internal sealed record HmiSize(int Width, int Height);

    internal sealed record HmiGeometry(
        int CanvasWidth, int CanvasHeight, int NavigationBarHeight,
        int Margin, int Gap, int CaptionHeight, int BannerY, int TitleY, int ContentTop,
        int ButtonWidth, int ButtonHeight, int ButtonBottomInset, HmiSize SymbolFallback)
    {
        // The drawable area: the canvas minus the runtime navigation bar EAE overlays.
        internal int WorkHeight => CanvasHeight - NavigationBarHeight;
        internal int NavBandY => WorkHeight - ButtonHeight - ButtonBottomInset;
        internal int ContentBottom => NavBandY - Gap;
        internal int ContentRight => CanvasWidth - Margin;
    }

    internal sealed record HmiStyle(
        string CanvasBrush, string ButtonBrush,
        HmiFont ButtonFont, HmiFont CaptionFont, HmiFont DetailFont,
        HmiColor ButtonTextColor, HmiColor CaptionColor, HmiColor EmphasisColor, HmiColor DetailColor);

    internal sealed record HmiScreenPolicy(
        string HubName, string HubTitle, string ResidualName, string ResidualTitle, string Suffix,
        string HubButtonText, string PreviousText, string NextText,
        bool ShowStates, bool ShowInterlocks, bool ShowAllocation, bool ShowProtocolLegend,
        int MaxStateChars, int MaxInterlockChars, int MaxProtocolChars);

    // A runtime-state vocabulary, selected by the DEPLOYED contract signature rather than a CAT name.
    internal sealed record HmiStatesProfile(string Id, IReadOnlyList<string> InputEventCarries,
                                            IReadOnlyList<string> Labels)
    {
        internal bool Matches(HmiContract c) =>
            InputEventCarries.Count > 0 &&
            InputEventCarries.All(v => c.HasFeedback(v));
    }

    internal sealed record HmiCapabilityRule(
        HmiCapabilityPurpose Purpose,
        string OutputEvent,
        IReadOnlyList<string> OutputData,
        IReadOnlyList<IReadOnlyList<string>> OutputDataVariants,
        string? AlsoRequiresOutputEvent,
        IReadOnlyList<string> AnyFeedback,
        bool NeedsModeChain,
        bool NeedsRecipeEngine);

    internal sealed record HmiEngineProbeSpec(
        string TypeName, IReadOnlyList<string> AutoGatingTokens, IReadOnlyList<string> ManualSteppingTokens);

    // One CanvasResolution entry in the runtime's canvas topology.
    internal sealed record HmiResolutionPolicy(
        string Name, string Template, string ResizeBehaviour, int CanvasButtonHeight,
        int SiblingButtonCount, int ChildButtonCount);

    // The runtime chrome EAE draws around the generated canvases. Every one is a monitoring
    // affordance; none of them command the plant.
    internal sealed record HmiChromePolicy(
        bool Logger, bool Login, bool CurrentUser, bool LanguageButton,
        bool RuntimeConnection, bool NavigationBar, bool NewVersionDeployed, int NavigationControl);

    internal sealed record HmiRuntimePolicy(
        string SchemaVersion, string StartCanvas,
        HmiResolutionPolicy Resolution, HmiResolutionPolicy FallbackResolution, HmiChromePolicy Chrome);

    internal sealed record HmiDeploymentPolicy(
        string HmiFolderName, string CanvasNamespaceSuffix, string SymbolNamespaceSuffix,
        string DefaultLibraryNamespace, string GeneratedBanner, string OwnershipManifest,
        string PrimarySymbol, IReadOnlyList<string> CommandSymbols)
    {
        internal bool IsCommandSymbol(string name) =>
            CommandSymbols.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed record HmiDefinition(
        int SchemaVersion,
        bool ReadOnly,
        bool FailOnUnknownContract,
        string BannerText,
        string UnsupportedCommandNotice,
        IReadOnlyDictionary<HmiUnavailableReason, string> UnavailableReasons,
        HmiGeometry Geometry,
        HmiStyle Style,
        HmiScreenPolicy Screens,
        IReadOnlyDictionary<int, string> ModeLabels,
        IReadOnlyDictionary<int, string> CycleLabels,
        IReadOnlyList<HmiStatesProfile> StatesProfiles,
        IReadOnlyList<HmiCapabilityRule> Capabilities,
        IReadOnlyList<string> InterlockFeedback,
        HmiEngineProbeSpec EngineProbe,
        HmiRuntimePolicy Runtime,
        HmiDeploymentPolicy Deployment)
    {
        internal const int SupportedSchemaVersion = 1;

        // Operator-facing text for a withheld capability. Every reason is present or the loader
        // rejected the file, so this is a lookup rather than a fallback chain.
        internal string Explain(HmiUnavailableReason reason) =>
            reason == HmiUnavailableReason.None ? string.Empty
            : UnavailableReasons.TryGetValue(reason, out var text) ? text
            : reason.ToString();

        // Selects the runtime vocabulary by contract signature. Ambiguity is an error, not a
        // first-match guess: two profiles matching one contract means the descriptors overlap and
        // the labels attached to a live value would be arbitrary.
        internal HmiStatesProfile? ProfileFor(HmiContract contract, out string? ambiguity)
        {
            ambiguity = null;
            var hits = StatesProfiles.Where(p => p.Matches(contract)).ToList();
            if (hits.Count == 0) return null;
            if (hits.Count == 1) return hits[0];

            // The most specific signature wins; equal specificity is genuinely ambiguous.
            var best = hits.OrderByDescending(p => p.InputEventCarries.Count).ToList();
            if (best[0].InputEventCarries.Count > best[1].InputEventCarries.Count) return best[0];

            ambiguity = $"contract matches {hits.Count} state profiles with equal specificity " +
                        $"({string.Join(", ", hits.Select(h => h.Id))})";
            return null;
        }
    }
}
