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

    public class IoBindings
    {
        public Dictionary<string, ActuatorBinding> Actuators { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, SensorBinding> Sensors { get; init; } = new(StringComparer.Ordinal);
        public string SourcePath { get; init; } = string.Empty;
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
