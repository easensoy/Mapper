using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;
using System.Xml.Linq;

namespace CodeGen.Translation
{
    // Reverse index from a symlink symbol (e.g. RES0.M262IO.PusherAtHome) to the target that owns the
    // .hcf binding. Resolution order: exact HCF match, then the symbol's own scope, then the deployment
    // allocation. Which file and which scope belong to a target is the target descriptor's answer.
    public class HcfSymbolIndex
    {
        private readonly Dictionary<string, PlcAssignment> _symbolToPlc =
            new(StringComparer.OrdinalIgnoreCase);

        // '<resource>.<scope>.<channel>': the scope is the authored hardware config's own name, which is
        // how a binding not yet written into the file still resolves to the target that will own it.
        private readonly Dictionary<string, PlcAssignment> _scopeToPlc =
            new(StringComparer.OrdinalIgnoreCase);



        // REPORTED, not collected: an index that loaded nothing silently demotes every PLC partition
        // to a name guess, and a list nobody reads is the same as no diagnostic at all.
        private static void Warn(string message) =>
            CodeGen.Services.MapperLogger.Warn($"[Hcf][Index] {message}");

        public static HcfSymbolIndex Build(Configuration.CompilerConfiguration cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var idx = new HcfSymbolIndex();
            var onDisk = cfg.Paths.HcfTemplatesByFileName;
            foreach (var target in cfg.Targets.All)
            {
                var file = target.HcfTemplate;
                if (string.IsNullOrWhiteSpace(file)) continue;   // a target with no authored config
                idx._scopeToPlc[Path.GetFileNameWithoutExtension(file)] = target.Plc;
                idx.AddHcf(onDisk.TryGetValue(file, out var path) ? path : null, target.Plc);
            }
            return idx;
        }

        private void AddHcf(string? path, PlcAssignment plc)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Warn($"{plc} HCF path not configured in mapper_config.json.");
                return;
            }
            if (!File.Exists(path))
            {
                Warn($"{plc} HCF not found at {path}.");
                return;
            }
            XDocument doc;
            try { doc = XDocument.Load(path); }
            catch (Exception ex)
            {
                Warn($"{plc} HCF failed to parse ({path}): {ex.Message}");
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

            if (added == 0)
                Warn(
                    $"{plc} HCF at {path} loaded but yielded zero RES*-symbol bindings " +
                    "(file likely carries only EIP word routing). Fallback to prefix + " +
                    "name will be used for components on this PLC.");
        }

        public PlcAssignment ResolveSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return PlcAssignment.Unknown;
            var sym = symbol.Trim().Trim('\'');
            if (_symbolToPlc.TryGetValue(sym, out var plc)) return plc;

            // Scope fallback for a binding not yet written into the .hcf.
            foreach (var (scope, owner) in _scopeToPlc)
                if (sym.Contains($".{scope}.", StringComparison.OrdinalIgnoreCase)) return owner;
            return PlcAssignment.Unknown;
        }

        // Owns a component by tracing an IO binding (atwork/athome/OutputToWork/OutputToHome,
        // then Sensor InputTag); falls back to the allocation when none is registered.
        public PlcAssignment ResolveComponent(string componentName, IoBindings? bindings,
            ControllerAllocation allocation)
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

            // No IO binding traced: fall back to the deployment allocation, which is where the component's
            // controller is decided in the first place.
            return allocation.Of(componentName);
        }
    }
}
