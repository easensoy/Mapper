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

    /// <summary>
    /// One row of the (Pin -> RES0 symlink) routing table the M262 .hcf needs.
    /// Built from optional <c>pin_di_athome</c> / <c>pin_di_atwork</c> /
    /// <c>pin_do_outputToWork</c> columns on the Actuators sheet.
    /// </summary>
    public record PinAssignment(string Pin, string ComponentName, string Port);

    public class IoBindings
    {
        public Dictionary<string, ActuatorBinding> Actuators { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, SensorBinding> Sensors { get; init; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Pin id (e.g. "DI00", "DO15") -> assignment. Populated only when the optional
        /// pin_di_athome / pin_di_atwork / pin_do_outputToWork columns are present in
        /// the IO bindings xlsx. Empty when the user hasn't added those columns yet —
        /// in that case <see cref="ResolveSymbol"/> returns null for everything and
        /// the .hcf is left with its baseline values.
        /// </summary>
        public Dictionary<string, PinAssignment> PinAssignments { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string SourcePath { get; init; } = string.Empty;

        /// <summary>
        /// For a pin id like "DI00", returns <c>'RES0.&lt;component&gt;.&lt;port&gt;'</c>
        /// (with literal single quotes — EAE's .hcf schema requires them) if the
        /// IO bindings xlsx maps that pin to an actuator port. Returns null if the
        /// pin has no assignment, in which case the .hcf rewriter leaves the baseline
        /// Value attribute untouched.
        /// </summary>
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

            // Optional pin columns — index by header name so the user can add them in any
            // order to the right of the existing columns. Absent columns just mean
            // ResolveSymbol() returns null for the corresponding pin.
            int idxPinDiAthome     = header.FindIndex(h => string.Equals(h, "pin_di_athome",      StringComparison.OrdinalIgnoreCase));
            int idxPinDiAtwork     = header.FindIndex(h => string.Equals(h, "pin_di_atwork",      StringComparison.OrdinalIgnoreCase));
            int idxPinDoToWork     = header.FindIndex(h => string.Equals(h, "pin_do_outputToWork", StringComparison.OrdinalIgnoreCase));
            int idxPinDoToHome     = header.FindIndex(h => string.Equals(h, "pin_do_outputToHome", StringComparison.OrdinalIgnoreCase));

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
            }
        }

        private static void AddPinIfPresent(IoBindings bindings, int idx,
            List<string> row, string componentName, string port)
        {
            if (idx < 0) return;
            var pin = NullIfEmpty(Get(row, idx));
            if (pin == null) return;
            // Last writer wins if a pin appears in two rows — EAE's hcf schema also
            // forbids duplicates, so this matches the runtime constraint.
            bindings.PinAssignments[pin] = new PinAssignment(pin, componentName, port);
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
