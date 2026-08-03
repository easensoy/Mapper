using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeGen.Hmi
{
    // One canvas inside a faceplate template folder.
    // EAE distinguishes the two kinds by geometry alone: a placeable symbol declares SymbolSize,
    // a pop-up faceplate declares only Size. That is also what drives IsFaceplate in the CAT .cfg.
    //
    // CommandCapable comes from the symbol's own .cnv.xml contract: a symbol can reach the controller
    // exactly when the contract declares an <EventOutputs> event or an <Outputs> variable. That is the
    // authoritative test - hiding a button is visibility, not a write block.
    internal sealed record HmiSymbol(
        string Name,
        bool IsFaceplate,
        int Width,
        int Height,
        bool HasContract,
        IReadOnlyList<string> OutputEvents,
        IReadOnlyList<string> OutputTags)
    {
        public bool CommandCapable => OutputEvents.Count > 0 || OutputTags.Count > 0;

        public string Outputs => string.Join(", ", OutputEvents.Concat(OutputTags).Distinct());
    }

    // A CAT type's faceplate template (one folder under Template Library\HMI\Faceplates).
    internal sealed record HmiCatTemplate(string CatType, string SourceDir, IReadOnlyList<HmiSymbol> Symbols)
    {
        public HmiSymbol? Placeable(string name) =>
            Symbols.FirstOrDefault(s => !s.IsFaceplate && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // The canvas a component shows when simply placed on a screen.
        public HmiSymbol? Primary =>
            Placeable(HmiNames.DefaultSymbol) ?? Symbols.FirstOrDefault(s => !s.IsFaceplate);

    }

    // A syslay FB drawn on a canvas. TagName is the syslay FB Id, which is the only
    // binding EAE needs to resolve the symbol to the running instance.
    internal sealed record HmiPlaceable(
        string Name, string TagName, string CatType, HmiSymbol Symbol, int X, int Y);

    // A literal, unbound label. Operator captions come from the syslay instance name - never from
    // actuator_name (the lower-case ring/MQTT key) and never from the CAT's untriggered name_event.
    internal sealed record HmiCaption(string Name, string Text, int X, int Y, bool Emphasis);

    internal sealed record HmiNavButton(string Name, string CanvasName, string Text, int X, int Y);

    internal sealed record HmiScreen(
        string Name,
        string Title,
        IReadOnlyList<HmiPlaceable> Items,
        IReadOnlyList<HmiNavButton> Buttons,
        IReadOnlyList<HmiCaption> Captions);

    internal sealed record HmiPlan(
        IReadOnlyList<HmiScreen> Screens,
        IReadOnlyList<HmiCatTemplate> UsedTemplates,
        IReadOnlyList<string> Diagnostics,
        bool ReadOnly)
    {
        public string FirstCanvas => Screens.Count > 0 ? Screens[0].Name : string.Empty;
    }

    internal static class HmiNames
    {
        internal const string DefaultSymbol = "sDefault";

        // Symbol families that exist only to command the plant. Never placed, never a screen family.
        internal static readonly string[] CommandSymbolNames = { "sSetup", "setup" };

        internal const string ReadOnlyBanner = "MONITORING ONLY - HMI COMMANDS DISABLED";

        internal const string CommandContractDiagnostic =
            "Controller Start/Stop and Setup control are unavailable by contract; " +
            "control-logic support is required before they can be enabled safely.";

        internal static bool IsCommandSymbol(string name) =>
            CommandSymbolNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }
}
