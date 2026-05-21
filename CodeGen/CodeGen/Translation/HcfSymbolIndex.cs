using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;

namespace CodeGen.Translation
{
    /// <summary>
    /// Three-PLC partitioning identifier produced by <see cref="HcfSymbolIndex"/>.
    /// </summary>
    public enum PlcAssignment
    {
        Unknown = 0,
        M262 = 1,
        M580 = 2,
        BX1 = 3,
    }

    /// <summary>
    /// Reverse index from a symlink symbol (e.g. <c>RES0.M262IO.PusherAtHome</c>) back
    /// to the PLC that owns the .hcf binding. Built once at the start of each Test
    /// Runtime click by reading the three exported HCF templates in
    /// <see cref="MapperConfig.IoFolderPath"/>. Used by <see cref="SystemLayoutInjector"/>
    /// and the frame layout code to decide which coloured PLC zone each component sits in.
    /// <para>
    /// Authority order:
    /// <list type="number">
    ///   <item>Exact symbol match in the parsed HCF (M262IO.hcf or M580IO.hcf — these carry
    ///         individual <c>&lt;ParameterValue Name="DI00" Value="'RES0.M262IO.…'"/&gt;</c>
    ///         channel bindings).</item>
    ///   <item>Symbol prefix fallback: <c>RES0.M262IO.*</c> → M262, <c>RES0.M580IO.*</c> → M580,
    ///         <c>RES0.BX1IO.*</c> → BX1. Catches symbols defined in IoBindings.xlsx whose
    ///         corresponding channel is not yet wired into the HCF (notably the BX1 case
    ///         where the HCF only carries EIP word routing, not per-symbol bindings).</item>
    ///   <item>Hard-coded component-name fallback (<see cref="NameBasedPlcGuess"/>) for
    ///         components that have no binding row in IoBindings.xlsx — e.g. when the
    ///         operator has not yet filled in their pin columns. Lets the syslay still
    ///         render with the right colour even before IO is wired.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class HcfSymbolIndex
    {
        private readonly Dictionary<string, PlcAssignment> _symbolToPlc =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>HCF file paths that were successfully loaded, keyed by PLC.</summary>
        public Dictionary<PlcAssignment, string> LoadedHcfs { get; } = new();

        /// <summary>Diagnostics — files Mapper tried to load but couldn't, or that were empty.</summary>
        public List<string> Warnings { get; } = new();

        /// <summary>Builds the index from MapperConfig's three HCF template paths.</summary>
        public static HcfSymbolIndex Build(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var idx = new HcfSymbolIndex();
            idx.AddHcf(cfg.M262HcfTemplatePath, PlcAssignment.M262);
            idx.AddHcf(cfg.M580HcfTemplatePath, PlcAssignment.M580);
            idx.AddHcf(cfg.BX1HcfTemplatePath, PlcAssignment.BX1);
            return idx;
        }

        private void AddHcf(string? path, PlcAssignment plc)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Warnings.Add($"{plc} HCF path not configured in mapper_config.json.");
                return;
            }
            if (!File.Exists(path))
            {
                Warnings.Add($"{plc} HCF not found at {path}.");
                return;
            }
            XDocument doc;
            try { doc = XDocument.Load(path); }
            catch (Exception ex)
            {
                Warnings.Add($"{plc} HCF failed to parse ({path}): {ex.Message}");
                return;
            }

            int added = 0;
            // EAE's .hcf wraps every channel binding in
            //   <ParameterValue Name="DI00" Value="'RES0.M262IO.PusherAtHome'"/>
            // The Value attribute carries the single-quoted symlink. Walk the
            // whole document; any ParameterValue whose Value looks like a
            // single-quoted RES* symbol is indexed against the PLC.
            foreach (var pv in doc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "ParameterValue", StringComparison.Ordinal)))
            {
                var raw = (string?)pv.Attribute("Value");
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var trimmed = raw.Trim();
                if (trimmed.Length < 3) continue;
                if (trimmed[0] != '\'' || trimmed[^1] != '\'') continue;
                var sym = trimmed.Substring(1, trimmed.Length - 2);
                if (string.IsNullOrWhiteSpace(sym)) continue;
                // Ignore EIP word-level routing (BX1 has things like
                // "78E9CD3D27851B64.F6C04A4BA6FA8593.EIP_Input_Word_1" without RES* prefix).
                if (!sym.StartsWith("RES", StringComparison.OrdinalIgnoreCase)) continue;
                _symbolToPlc[sym] = plc;
                added++;
            }

            LoadedHcfs[plc] = path;
            if (added == 0)
                Warnings.Add(
                    $"{plc} HCF at {path} loaded but yielded zero RES*-symbol bindings " +
                    "(file likely carries only EIP word routing). Fallback to prefix + " +
                    "name will be used for components on this PLC.");
        }

        /// <summary>Direct symbol lookup. Returns Unknown if not in any parsed HCF.</summary>
        public PlcAssignment ResolveSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return PlcAssignment.Unknown;
            // Strip any leading/trailing single quotes that may come from .hcf Value strings.
            var sym = symbol.Trim().Trim('\'');
            if (_symbolToPlc.TryGetValue(sym, out var plc)) return plc;

            // Prefix fallback: RES0.M262IO.* → M262 etc. Catches bindings declared in
            // IoBindings.xlsx whose channel isn't actually written into the .hcf yet.
            if (sym.Contains(".M262IO.", StringComparison.OrdinalIgnoreCase)) return PlcAssignment.M262;
            if (sym.Contains(".M580IO.", StringComparison.OrdinalIgnoreCase)) return PlcAssignment.M580;
            if (sym.Contains(".BX1IO.",  StringComparison.OrdinalIgnoreCase)) return PlcAssignment.BX1;
            return PlcAssignment.Unknown;
        }

        /// <summary>
        /// Looks up the PLC that owns a given component by tracing one of its IO bindings
        /// (atwork preferred, then athome, then OutputToWork, then OutputToHome, then the
        /// Sensor InputTag). Falls back to <see cref="NameBasedPlcGuess"/> when no binding
        /// is registered for the component in IoBindings.xlsx.
        /// </summary>
        public PlcAssignment ResolveComponent(string componentName, IoBindings? bindings)
        {
            if (string.IsNullOrWhiteSpace(componentName)) return PlcAssignment.Unknown;

            if (bindings != null)
            {
                if (bindings.Actuators.TryGetValue(componentName, out var act))
                {
                    foreach (var tag in new[] {
                        act.AtworkTag, act.AthomeTag, act.OutputToWorkTag, act.OutputToHomeTag })
                    {
                        if (string.IsNullOrWhiteSpace(tag)) continue;
                        var plc = ResolveSymbol(tag!);
                        if (plc != PlcAssignment.Unknown) return plc;
                    }
                }
                if (bindings.Sensors.TryGetValue(componentName, out var sen))
                {
                    if (!string.IsNullOrWhiteSpace(sen.InputTag))
                    {
                        var plc = ResolveSymbol(sen.InputTag!);
                        if (plc != PlcAssignment.Unknown) return plc;
                    }
                }
            }

            return NameBasedPlcGuess(componentName);
        }

        /// <summary>
        /// Hard-coded fallback used when a component has no row in IoBindings.xlsx.
        /// Mirrors the SMC_Rig_Expo_withClamp reference partitioning so the syslay
        /// renders with the right colour even before the bindings sheet is filled in.
        /// </summary>
        public static PlcAssignment NameBasedPlcGuess(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName)) return PlcAssignment.Unknown;
            var n = componentName.Trim();

            // Station 1 (M262)
            if (Eq(n, "Feeder") || Eq(n, "Checker") || Eq(n, "Transfer")
                || Eq(n, "Ejector") || Eq(n, "Rejector")
                || Eq(n, "PartInHopper") || Eq(n, "PartAtChecker"))
                return PlcAssignment.M262;

            // Station 2 (M580) — swivel arm + clamp + shaft column
            if (Eq(n, "Bearing_PnP") || Eq(n, "Bearing_Gripper") || Eq(n, "BearingSensor")
                || Eq(n, "Shaft_Hr") || Eq(n, "Shaft_Vr") || Eq(n, "Shaft_Gripper")
                || Eq(n, "ShaftSensor") || Eq(n, "Clamp"))
                return PlcAssignment.M580;

            // Station 2 (BX1) — cover pick-and-place + top cover sensing
            if (Eq(n, "CoverPNP_Hr") || Eq(n, "CoverPNP_Vr") || Eq(n, "CoverPnp_Gripper")
                || Eq(n, "TopCoverSenosr") || Eq(n, "TopCoverSensor"))
                return PlcAssignment.BX1;

            // Robot arm sits on M262 in the reference layout (RobotStatus channel is on M262IO).
            if (Eq(n, "Robot") || Eq(n, "Robot_Pick_And_Place1"))
                return PlcAssignment.M262;

            return PlcAssignment.Unknown;
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
