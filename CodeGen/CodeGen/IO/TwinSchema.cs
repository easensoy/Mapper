using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Models;

namespace CodeGen.Configuration
{
    // The VueOne source schema this compiler reads: Config/twin-schema.yml.
    //
    // It answers exactly one question - what KIND of component a <Type> token names - and it is the
    // single owner of that answer. The reader stamps the kind onto each component as it parses, so no
    // consumer downstream re-derives it from the token, and none of them can disagree.
    public sealed class TwinSchema
    {
        // token -> kind, as written in the declaration. Case-insensitive on the token, because
        // Control.xml is authored by hand and a twin may spell the same role `Sensor` or `sensor`.
        public Dictionary<string, string> ComponentKinds { get; set; } = new();

        private Dictionary<string, ComponentKind> _resolved =
            new(StringComparer.OrdinalIgnoreCase);

        // The kinds a declaration may name. Spelled once here so the refusal can list them.
        private static readonly Dictionary<string, ComponentKind> Vocabulary =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["actuator"] = ComponentKind.Actuator,
                ["sensor"]   = ComponentKind.Sensor,
                ["process"]  = ComponentKind.Process,
                ["excluded"] = ComponentKind.Excluded,
            };

        /// The kind this token names, or null if the declaration does not map it.
        public ComponentKind? TryKind(string? token) =>
            !string.IsNullOrWhiteSpace(token) && _resolved.TryGetValue(token!.Trim(), out var k)
                ? k : (ComponentKind?)null;

        /// The kind this token names. An unmapped token STOPS THE RUN rather than defaulting: a
        /// component the compiler cannot classify is one it would either drive or ignore by accident.
        public ComponentKind KindOf(string? token, string componentName)
        {
            var kind = TryKind(token);
            if (kind != null) return kind.Value;

            var spelled = string.IsNullOrWhiteSpace(token) ? "(empty)" : token!.Trim();
            throw new InvalidOperationException(
                $"[Twin] component '{componentName}' declares Type '{spelled}', which " +
                "Config/twin-schema.yml does not map to a component kind, so this compiler does not " +
                "know whether to drive it, observe it or ignore it. Declared tokens: " +
                $"{string.Join(", ", _resolved.Keys.OrderBy(k => k, StringComparer.Ordinal))}. " +
                "Add the token under componentKinds with one of: " +
                $"{string.Join(", ", Vocabulary.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
        }

        internal static void Validate(TwinSchema s)
        {
            var errors = new List<string>();
            var resolved = new Dictionary<string, ComponentKind>(StringComparer.OrdinalIgnoreCase);

            if (s.ComponentKinds.Count == 0)
                errors.Add("componentKinds declares no tokens, so every component would be refused");

            foreach (var (token, kindName) in s.ComponentKinds)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    errors.Add("componentKinds declares an empty <Type> token");
                    continue;
                }
                if (!Vocabulary.TryGetValue((kindName ?? string.Empty).Trim(), out var kind))
                {
                    errors.Add($"componentKinds maps '{token}' to '{kindName}', which is not a kind. " +
                               $"Use one of: {string.Join(", ", Vocabulary.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
                    continue;
                }
                if (!resolved.TryAdd(token.Trim(), kind))
                    errors.Add($"componentKinds maps '{token}' twice; the token comparison is " +
                               "case-insensitive, so two spellings of one token are one declaration");
            }

            // A schema that maps no token onto a driven or an observed component reads a twin as a
            // list of things to ignore, and the run would report success having generated nothing.
            if (errors.Count == 0 &&
                !resolved.Values.Any(k => k == ComponentKind.Actuator || k == ComponentKind.Sensor))
                errors.Add("componentKinds maps no token to 'actuator' or 'sensor', so no twin could " +
                           "declare anything this compiler drives or observes");

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "Config/twin-schema.yml is invalid: " + string.Join("; ", errors));

            s._resolved = resolved;
        }

        private static readonly YamlConfigFile<TwinSchema> _file =
            new("Config", "twin-schema.yml") { OnLoaded = Validate };

        /// The same declaration read from a run's OWN profile bundle. A root of null is the
        /// bundle shipped beside CodeGen.dll, which is what a normal run reads.
        public static TwinSchema LoadFrom(string? root) => _file.Load(root);
    }
}
