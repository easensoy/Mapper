using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodeGen.Configuration;
using CodeGen.Devices.Core;

namespace CodeGen.Translation.Process
{
    /// Writes the ordinal -> phase-name map that process telemetry publishes against.
    ///
    /// The wire payload is an integer, because that is the shared {state:N} convention every component
    /// already uses and the formatter is an INT formatter. A subscriber therefore needs the names from
    /// somewhere, and deriving them by re-walking Control.xml would duplicate the compiler's numbering
    /// in every consumer. Emitting the map at generation time keeps one source of truth: whoever reads
    /// this file is reading the same numbering the PLC was built with.
    ///
    /// TELEMETRY ONLY. Nothing in the generated project references this file, so a missing or stale map
    /// degrades a subscriber to bare numbers and can never affect the rig.
    internal static class ProcessPhaseMapEmitter
    {
        internal const string FileName = "process-phases.json";

        /// Written to the PARENT of the EAE project root, deliberately outside the solution: EAE
        /// enumerates its own project tree, and an unregistered file inside it is a Solution Integrity
        /// complaint waiting to happen.
        internal static string? Emit(MapperConfig cfg,
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> byProcess,
            Action<string>? warn = null)
        {
            try
            {
                var eaeRoot = EaeProjectLayout.DeriveEaeProjectRoot(cfg);
                if (string.IsNullOrEmpty(eaeRoot)) return null;
                var dir = Path.GetDirectoryName(eaeRoot.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(dir)) return null;

                var processes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                foreach (var (proc, names) in byProcess)
                {
                    var m = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (ordinal, name) in names) m[ordinal.ToString()] = name;
                    processes[proc] = m;
                }

                var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["topicRoot"] = cfg.MqttTopicRoot + "/process",
                    ["note"] = "Maps the integer published on <topicRoot>/<process> to the VueOne state "
                             + "name. Ordinals are the twin's declaration order, 1-based; 0 means no "
                             + "owning state. Telemetry only - the rig does not read this file.",
                    ["processes"] = processes,
                };

                var path = Path.Combine(dir, FileName);
                File.WriteAllText(path,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                return path;
            }
            catch (Exception ex)
            {
                warn?.Invoke($"[Telemetry] phase-name map not written: {ex.Message}");
                return null;
            }
        }
    }
}
