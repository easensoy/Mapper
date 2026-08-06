using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodeGen.Configuration;
using CodeGen.Devices.Core;

namespace CodeGen.Translation.Process
{
    // TELEMETRY ONLY: the ordinal -> phase-name map process telemetry publishes against. Nothing in the
    // generated project reads it, so a missing or stale map only degrades a subscriber to bare numbers.
    internal static class ProcessPhaseMapEmitter
    {
        internal const string FileName = "process-phases.json";

        // Written OUTSIDE the EAE solution (the project root's parent): EAE enumerates its own tree and
        // an unregistered file inside it raises a Solution Integrity complaint.
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
