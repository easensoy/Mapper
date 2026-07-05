using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;

namespace CodeGen.Translation
{
    public enum PlcAssignment
    {
        Unknown = 0,
        M262 = 1,
        M580 = 2,
        BX1 = 3,
    }

    // Reverse index from a symlink symbol (e.g. RES0.M262IO.PusherAtHome) to the PLC that
    // owns the .hcf binding. Resolution order: exact HCF match, then symbol prefix
    // (RES0.M262IO.*/M580IO.*/BX1IO.*), then NameBasedPlcGuess.
    public class HcfSymbolIndex
    {
        private readonly Dictionary<string, PlcAssignment> _symbolToPlc =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<PlcAssignment, string> LoadedHcfs { get; } = new();

        public List<string> Warnings { get; } = new();

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
            // EAE's .hcf binds each channel as <ParameterValue Name="DI00" Value="'RES0.M262IO.PusherAtHome'"/>;
            // index any single-quoted RES* Value against the PLC.
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
                // Ignore EIP word-level routing (BX1 uses non-RES*-prefixed GUID triples).
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

        public PlcAssignment ResolveSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return PlcAssignment.Unknown;
            var sym = symbol.Trim().Trim('\'');
            if (_symbolToPlc.TryGetValue(sym, out var plc)) return plc;

            // Prefix fallback for bindings not yet written into the .hcf.
            if (sym.Contains(".M262IO.", StringComparison.OrdinalIgnoreCase)) return PlcAssignment.M262;
            if (sym.Contains(".M580IO.", StringComparison.OrdinalIgnoreCase)) return PlcAssignment.M580;
            if (sym.Contains(".BX1IO.",  StringComparison.OrdinalIgnoreCase)) return PlcAssignment.BX1;
            return PlcAssignment.Unknown;
        }

        // Owns a component by tracing an IO binding (atwork/athome/OutputToWork/OutputToHome,
        // then Sensor InputTag); falls back to NameBasedPlcGuess when none is registered.
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

        // Fallback when a component has no row in IoBindings.xlsx: primary partition from
        // ControllerMap, then the alias/Robot list below for unregistered names.
        public static PlcAssignment NameBasedPlcGuess(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName)) return PlcAssignment.Unknown;

            var fromRegistry = ControllerMap.PlcOf(componentName);
            if (fromRegistry != PlcAssignment.Unknown) return fromRegistry;

            // Rejector=Ejector synonym (M262); TopCoverSensor variant of TopCoverSenosr (BX1);
            // Robot RobotStatus channel is on M262IO.
            var n = componentName.Trim();
            if (Eq(n, "Rejector")) return PlcAssignment.M262;
            if (Eq(n, "TopCoverSensor")) return PlcAssignment.BX1;
            if (Eq(n, "Robot") || Eq(n, "Robot_Pick_And_Place1")) return PlcAssignment.M262;

            return PlcAssignment.Unknown;
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
