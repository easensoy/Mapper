using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // The validated, immutable HMI definition. Every presentation, protocol and capability value the
    // generator uses comes from here; nothing downstream carries a fallback of its own.

    internal sealed record HmiFont(string Family, int Size, bool Bold);

    internal sealed record HmiColor(int R, int G, int B);

    internal sealed record HmiGeometry(
        int CanvasWidth, int CanvasHeight, int NavigationBarHeight,
        int Margin, int Gap, int CaptionHeight, int TitleY, int ContentTop,
        int ButtonWidth, int ButtonHeight, int ButtonBottomInset)
    {
        // The drawable area: the canvas minus the runtime navigation bar EAE overlays.
        internal int WorkHeight => CanvasHeight - NavigationBarHeight;
        internal int NavBandY => WorkHeight - ButtonHeight - ButtonBottomInset;
        internal int ContentBottom => NavBandY - Gap;
        internal int ContentRight => CanvasWidth - Margin;
    }

    internal sealed record HmiStyle(
        string CanvasBrush, string ButtonBrush,
        HmiFont ButtonFont, HmiFont CaptionFont,
        HmiColor ButtonTextColor, HmiColor CaptionColor, HmiColor EmphasisColor);

    // The role a placed instance plays, DERIVED from the model: a component a process commands is an
    // actuator, one it only observes is a sensor. Never inferred from a CAT or instance name.
    internal enum HmiRole { Station, Process, Actuator, Sensor }

    // One July 30 screen family: which roles it shows, which symbol each role uses, and the
    // capability that makes its controls live.
    internal sealed record HmiScreenFamily(
        string Name,
        string Title,
        IReadOnlyList<HmiRole> Include,
        IReadOnlyDictionary<HmiRole, string> Variant,
        HmiCapabilityPurpose? Requires,
        bool OnlySupported)
    {
        internal string SymbolFor(HmiRole role, string primary) =>
            Variant.TryGetValue(role, out var s) ? s : primary;
    }

    internal sealed record HmiScreenPolicy(
        IReadOnlyList<HmiScreenFamily> Families,
        string HubName,
        // The read-only surface for model data no faceplate can show.
        string DetailName,
        string DetailTitle);

    // A runtime-state vocabulary, selected by the DEPLOYED contract signature rather than a CAT name.
    internal sealed record HmiStatesProfile(string Id, IReadOnlyList<string> InputEventCarries,
                                            IReadOnlyList<string> Labels)
    {
        internal bool Matches(HmiContract c) =>
            InputEventCarries.Count > 0 &&
            InputEventCarries.All(v => c.HasFeedback(v));
    }

    // The controller-side proof: some deployed ECC must guard on one of these tokens. InType
    // restricts the search to a single deployed FB type, which is what separates "the station
    // tracks the cycle" from "the recipe engine honours a STOP".
    internal sealed record HmiConsumptionSpec(
        string? InType,
        // EVERY token here must be guarded on.
        IReadOnlyList<string> Tokens,
        // At least ONE of these, when the deployed families legitimately name the same concept
        // differently. Empty means the clause does not apply.
        IReadOnlyList<string> AnyOf);

    internal sealed record HmiCapabilityRule(
        HmiCapabilityPurpose Purpose,
        string OutputEvent,
        // Accepted data shapes; several is how a two-position jog is told from a three-position one.
        IReadOnlyList<IReadOnlyList<string>> OutputData,
        string? AlsoRequiresOutputEvent,
        IReadOnlyList<string> AnyFeedback,
        bool NeedsModeChain,
        // The adapter ports the command travels on, for the rule that needs a chain walk.
        IReadOnlyList<string> ChainPorts,
        HmiConsumptionSpec Consumption);

    // One thing an operator can press.
    //
    // A capability proves the controller honours an EVENT; an action is one PAYLOAD on that event.
    // Two actions on the same event can have opposite verdicts, which is precisely why the gate that
    // decides whether a control ships has to be per action.
    //
    // Fires/Payload locate the action in the faceplate source by its CALL, so nothing here names a
    // control, a handler or a symbol - a renamed button cannot escape the gate.
    internal sealed record HmiOperatorAction(
        string Id,
        string Label,
        HmiCapabilityPurpose ProvedBy,
        string? Event,
        string? Payload,
        string? Writes,
        HmiActionProof? AlsoRequires)
    {
        // The exact call the staged faceplate makes, e.g. "FireEvent_MCNF(1)".
        public string? Call => Event == null ? null : $"FireEvent_{Event}({Payload})";
    }

    // An extra proof one PAYLOAD needs, read from the deployed ECC.
    internal sealed record HmiActionProof(string Guard, string? DistinctFrom, string? InterlockedBy);

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
        string PrimarySymbol);

    internal sealed record HmiDefinition(
        int SchemaVersion,
        bool FailOnUnknownContract,
        string UnsupportedCommandNotice,
        string NoContractNotice,
        string WithheldMarker,
        string WithheldHeading,
        HmiGeometry Geometry,
        HmiStyle Style,
        HmiScreenPolicy Screens,
        IReadOnlyList<HmiStatesProfile> StatesProfiles,
        IReadOnlyList<HmiCapabilityRule> Capabilities,
        IReadOnlyList<HmiOperatorAction> Actions,
        IReadOnlyList<string> InterlockFeedback,
        HmiRuntimePolicy Runtime,
        HmiDeploymentPolicy Deployment,
        // The deployment/device half of the SAME file, bound during the one load.
        HmiDeviceDefinition Device)
    {
        internal const int SupportedSchemaVersion = 1;

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
