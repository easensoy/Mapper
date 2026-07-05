using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeGen.Translation
{
    public record ActuatorBinding(
        string ComponentName,
        string? AthomeTag,
        string? AtworkTag,
        string? OutputToWorkTag,
        string? OutputToHomeTag);

    public record SensorBinding(
        string ComponentName,
        string? InputTag);

    // One row of the (Pin -> RES0 symlink) routing table the M262 .hcf needs.
    public record PinAssignment(string Pin, string ComponentName, string Port);

    public class IoBindings
    {
        public Dictionary<string, ActuatorBinding> Actuators { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, SensorBinding> Sensors { get; init; } = new(StringComparer.Ordinal);

        // Pin id (e.g. "DI00") -> assignment; empty when the optional pin_* columns are absent
        // (ResolveSymbol then returns null and the .hcf keeps its baseline values).
        public Dictionary<string, PinAssignment> PinAssignments { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string SourcePath { get; init; } = string.Empty;

        // Returns 'RES0.<component>.<port>' (literal quotes — EAE .hcf schema requires them) for a
        // mapped pin, else null (the .hcf rewriter then leaves the baseline Value untouched).
        public string? ResolveSymbol(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin)) return null;
            if (!PinAssignments.TryGetValue(pin, out var assignment)) return null;
            return $"'RES0.{assignment.ComponentName}.{assignment.Port}'";
        }
    }

    public class IoBindingsLoader
    {
        private static readonly object Lock = new();
        private static IoBindings? _cache;
        private static string _cachedPath = string.Empty;

        public static IoBindings LoadBindings(string xlsxPath)
        {
            if (string.IsNullOrEmpty(xlsxPath))
                throw new ArgumentException("Bindings xlsx path is required.", nameof(xlsxPath));
            if (!File.Exists(xlsxPath))
                throw new FileNotFoundException($"IO bindings file not found: {xlsxPath}");

            lock (Lock)
            {
                if (_cache != null && string.Equals(_cachedPath, xlsxPath, StringComparison.OrdinalIgnoreCase))
                    return _cache;

                var bindings = new IoBindings { SourcePath = xlsxPath };

                var actuatorRows = XlsxRuleLoader.ReadXlsxSheet(xlsxPath, "Actuators");
                ParseActuatorSheet(actuatorRows, bindings);

                var sensorRows = XlsxRuleLoader.ReadXlsxSheet(xlsxPath, "Sensors");
                ParseSensorSheet(sensorRows, bindings);

                _cache = bindings;
                _cachedPath = xlsxPath;
                return bindings;
            }
        }

        public static void InvalidateCache()
        {
            lock (Lock) { _cache = null; _cachedPath = string.Empty; }
        }

        private static void ParseActuatorSheet(List<List<string>> rows, IoBindings bindings)
        {
            if (rows.Count == 0)
                throw new InvalidDataException("Actuators sheet is empty.");

            var header = rows[0].Select(s => s ?? string.Empty).ToList();
            var expected = new[] { "Component", "Type", "athome_tag", "atwork_tag", "outputToWork_tag", "outputToHome_tag" };
            for (int i = 0; i < expected.Length; i++)
            {
                if (header.Count <= i || !string.Equals(header[i], expected[i], StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Actuators sheet column {i} expected '{expected[i]}', got '{(i < header.Count ? header[i] : "<missing>")}'");
            }

            // Optional pin columns, indexed by header name; absent columns just mean no pin assignment.
            int idxPinDiAthome     = header.FindIndex(h => string.Equals(h, "pin_di_athome",      StringComparison.OrdinalIgnoreCase));
            int idxPinDiAtwork     = header.FindIndex(h => string.Equals(h, "pin_di_atwork",      StringComparison.OrdinalIgnoreCase));
            int idxPinDoToWork     = header.FindIndex(h => string.Equals(h, "pin_do_outputToWork", StringComparison.OrdinalIgnoreCase));
            int idxPinDoToHome     = header.FindIndex(h => string.Equals(h, "pin_do_outputToHome", StringComparison.OrdinalIgnoreCase));
            int idxNotes           = header.FindIndex(h => string.Equals(h, "Notes",              StringComparison.OrdinalIgnoreCase));

            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.Count == 0 || string.IsNullOrWhiteSpace(row[0])) continue;

                var name = row[0];
                var binding = new ActuatorBinding(
                    ComponentName: name,
                    AthomeTag: NullIfEmpty(Get(row, 2)),
                    AtworkTag: NullIfEmpty(Get(row, 3)),
                    OutputToWorkTag: NullIfEmpty(Get(row, 4)),
                    OutputToHomeTag: NullIfEmpty(Get(row, 5)));
                bindings.Actuators[name] = binding;

                AddPinIfPresent(bindings, idxPinDiAthome, row, name, "athome");
                AddPinIfPresent(bindings, idxPinDiAtwork, row, name, "atwork");
                AddPinIfPresent(bindings, idxPinDoToWork, row, name, "OutputToWork");
                AddPinIfPresent(bindings, idxPinDoToHome, row, name, "OutputToHome");

                // Notes-column fallback drives the .hcf from the hand-crafted Notes cell (tokens like
                // "DI00=PusherAtHome") — NEVER regenerate the xlsx, so no schema change is possible here.
                if (idxNotes >= 0)
                {
                    var notes = Get(row, idxNotes);
                    if (!string.IsNullOrWhiteSpace(notes))
                        ParseNotesPinAssignments(bindings, notes, binding);
                }
            }
        }

        private static void AddPinIfPresent(IoBindings bindings, int idx,
            List<string> row, string componentName, string port)
        {
            if (idx < 0) return;
            var pin = NullIfEmpty(Get(row, idx));
            if (pin == null) return;
            // Last writer wins on a duplicate pin — EAE's hcf schema also forbids duplicates.
            bindings.PinAssignments[pin] = new PinAssignment(pin, componentName, port);
        }

        private static readonly System.Text.RegularExpressions.Regex NotesPinPattern =
            new(@"\b(D[IO]\d{2})\s*=\s*([A-Za-z_][A-Za-z0-9_]*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void ParseNotesPinAssignments(IoBindings bindings, string notes,
            ActuatorBinding binding)
        {
            foreach (System.Text.RegularExpressions.Match m in NotesPinPattern.Matches(notes))
            {
                var pin = m.Groups[1].Value.ToUpperInvariant();
                var tag = m.Groups[2].Value;

                string? port = null;
                if (string.Equals(tag, binding.AthomeTag,        StringComparison.OrdinalIgnoreCase)) port = "athome";
                else if (string.Equals(tag, binding.AtworkTag,        StringComparison.OrdinalIgnoreCase)) port = "atwork";
                else if (string.Equals(tag, binding.OutputToWorkTag,  StringComparison.OrdinalIgnoreCase)) port = "OutputToWork";
                else if (string.Equals(tag, binding.OutputToHomeTag,  StringComparison.OrdinalIgnoreCase)) port = "OutputToHome";
                if (port == null) continue;

                // An explicit pin_* column already ran above and wins over the Notes free-text.
                if (bindings.PinAssignments.ContainsKey(pin)) continue;
                bindings.PinAssignments[pin] = new PinAssignment(pin, binding.ComponentName, port);
            }
        }

        private static void ParseSensorSheet(List<List<string>> rows, IoBindings bindings)
        {
            if (rows.Count == 0)
                throw new InvalidDataException("Sensors sheet is empty.");

            var header = rows[0].Select(s => s ?? string.Empty).ToList();
            var expected = new[] { "Component", "Type", "input_tag" };
            for (int i = 0; i < expected.Length; i++)
            {
                if (header.Count <= i || !string.Equals(header[i], expected[i], StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Sensors sheet column {i} expected '{expected[i]}', got '{(i < header.Count ? header[i] : "<missing>")}'");
            }

            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.Count == 0 || string.IsNullOrWhiteSpace(row[0])) continue;

                var name = row[0];
                bindings.Sensors[name] = new SensorBinding(name, NullIfEmpty(Get(row, 2)));
            }
        }

        private static string Get(List<string> row, int idx) => idx < row.Count ? row[idx] : string.Empty;
        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
