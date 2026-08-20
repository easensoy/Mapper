using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace CodeGen.Hmi
{
    internal sealed class HmiConfigException : Exception
    {
        internal HmiConfigException(string file, string message)
            : base($"[Hmi] Config/{file} is invalid: {message}") { }
    }

    // Strict-YAML infrastructure for hmi.yml.
    //
    // The document is read as a plain node tree and addressed by path, so the schema lives in the one
    // place that consumes it instead of being mirrored into a parallel set of nullable transport
    // types. Strictness is kept: every path a load reads is recorded, and any key the load never
    // touched is reported - which covers the whole tree rather than one type at a time.
    internal static class HmiYaml
    {
        private static readonly IDeserializer Untyped = new DeserializerBuilder().Build();

        internal static string PathOf(string file) =>
            Path.Combine(AppContext.BaseDirectory, "Config", file);

        internal static string Read(string file)
        {
            var path = PathOf(file);
            if (!File.Exists(path))
                throw new HmiConfigException(file,
                    $"not found at '{path}'. It ships beside CodeGen.dll via a CodeGen.csproj " +
                    "<None CopyToOutputDirectory> entry; rebuild CodeGen.");
            return File.ReadAllText(path);
        }

        internal static object Parse(string file, string yaml)
        {
            try
            {
                return Untyped.Deserialize<object>(yaml)
                    ?? throw new HmiConfigException(file, "empty document.");
            }
            catch (YamlException ex) { throw new HmiConfigException(file, ex.Message); }
        }

        // Accumulates every problem so one run reports the whole set rather than the first typo.
        internal class Validator
        {
            private readonly string _file;
            protected readonly List<string> _problems = new();
            private readonly HashSet<string> _read = new(StringComparer.Ordinal);
            private readonly HashSet<string> _whole = new(StringComparer.Ordinal);
            private object? _root;

            internal Validator(string file) => _file = file;

            internal Node Root(string yaml)
            {
                _root = Parse(_file, yaml);
                return new Node(this, _root, string.Empty);
            }

            internal void Fail(string message) => _problems.Add(message);

            internal void Note(string path) => _read.Add(path);

            internal void NoteSubtree(string path) { _read.Add(path); _whole.Add(path); }

            // Every key the load never addressed is a typo or a stale edit; reporting it is what
            // stops the generator running on a value the file appears to override.
            internal void Unknown()
            {
                Walk(_root, string.Empty);

                void Walk(object? node, string path)
                {
                    if (_whole.Contains(path)) return;
                    switch (node)
                    {
                        case IDictionary<object, object> map:
                            foreach (var kv in map)
                            {
                                var child = path.Length == 0 ? $"{kv.Key}" : $"{path}.{kv.Key}";
                                if (!_read.Contains(child)) { _problems.Add($"unknown key '{child}'."); continue; }
                                Walk(kv.Value, child);
                            }
                            break;
                        case IList<object> list:
                            for (var i = 0; i < list.Count; i++) Walk(list[i], $"{path}[{i}]");
                            break;
                    }
                }
            }

            internal void Throw()
            {
                if (_problems.Count > 0)
                    throw new HmiConfigException(_file, Environment.NewLine + "  - " +
                        string.Join(Environment.NewLine + "  - ", _problems));
            }

            internal void Distinct<T>(IEnumerable<(string Path, T Value)> entries, string what)
            {
                foreach (var dup in entries.GroupBy(e => e.Value).Where(g => g.Count() > 1))
                    _problems.Add($"{what} '{dup.Key}' is used by {string.Join(", ", dup.Select(d => d.Path))}.");
            }
        }

        // One addressable position in the document. Reading through a Node validates the value and
        // records the path, so the schema, the error message and the strictness check all come from
        // the single call site that needs the value.
        internal sealed class Node
        {
            private readonly Validator _v;
            private readonly object? _raw;

            internal Node(Validator v, object? raw, string path) { _v = v; _raw = raw; Path = path; }

            internal string Path { get; }

            private string At(string rel) =>
                rel.Length == 0 ? Path : Path.Length == 0 ? rel : Path + "." + rel;

            private object? Raw(string rel)
            {
                var cur = _raw;
                var path = Path;
                foreach (var part in rel.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    path = path.Length == 0 ? part : path + "." + part;
                    if (cur is IDictionary<object, object> map && map.TryGetValue(part, out var next))
                    { _v.Note(path); cur = next; }
                    else return null;
                }
                return cur;
            }

            private void Missing(string rel, string what) =>
                _v.Fail($"missing required {what} '{At(rel)}'.");

            internal bool Has(string rel) => Raw(rel) != null;

            // ---- structure --------------------------------------------------------------------

            internal Node Sec(string rel)
            {
                var raw = Raw(rel);
                if (raw is IDictionary<object, object>) return new Node(_v, raw, At(rel));
                Missing(rel, "section");
                _v.Throw();
                throw new InvalidOperationException();   // unreachable: Throw always throws here
            }

            internal IReadOnlyList<Node> Seq(string rel)
            {
                if (Raw(rel) is IList<object> list && list.Count > 0)
                    return list.Select((x, i) => new Node(_v, x, $"{At(rel)}[{i}]")).ToList();
                _v.Fail($"'{At(rel)}' must declare at least one entry.");
                return Array.Empty<Node>();
            }

            // ---- scalars ----------------------------------------------------------------------

            internal string? Opt(string rel)
            {
                var t = Raw(rel) as string;
                return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
            }

            internal string Text(string rel)
            {
                var t = Opt(rel);
                if (t != null) return t;
                Missing(rel, "value");
                return string.Empty;
            }

            internal string Identifier(string rel)
            {
                var t = Text(rel);
                if (t.Length > 0 && !t.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                    _v.Fail($"'{At(rel)}' must be letters, digits or underscore (got '{t}').");
                return t;
            }

            internal string Guid(string rel)
            {
                var t = Text(rel);
                if (t.Length > 0 && !System.Guid.TryParse(t, out _))
                    _v.Fail($"'{At(rel)}' is not a GUID (got '{t}').");
                return t;
            }

            internal string Ip(string rel)
            {
                var t = Text(rel);
                if (t.Length > 0 && !IPAddress.TryParse(t, out _))
                    _v.Fail($"'{At(rel)}' is not an IP address (got '{t}').");
                return t;
            }

            // A template or destination name must stay inside its directory: no rooted paths, no
            // traversal, no directory separators at all.
            internal string SafeFileName(string rel)
            {
                var t = Text(rel);
                if (t.Length == 0) return t;
                if (System.IO.Path.IsPathRooted(t) || t.Contains("..", StringComparison.Ordinal) ||
                    t.Contains('/') || t.Contains('\\'))
                    _v.Fail($"'{At(rel)}' must be a plain file name (got '{t}').");
                return t;
            }

            internal int Int(string rel)
            {
                var t = Opt(rel);
                if (t == null) { Missing(rel, "value"); return 0; }
                if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
                _v.Fail($"'{At(rel)}' must be a whole number (got '{t}').");
                return 0;
            }

            // One bounds check. The message names the range it was given, so a bad canvas width and
            // a bad port read the same way without four near-identical wrappers.
            internal int Int(string rel, int min, int max)
            {
                var n = Int(rel);
                if (n >= min && n <= max) return n;
                _v.Fail(max == int.MaxValue
                    ? $"'{At(rel)}' must be at least {min} (got {n})."
                    : $"'{At(rel)}' must be {min}-{max} (got {n}).");
                return n;
            }

            internal bool Flag(string rel)
            {
                var t = Opt(rel);
                if (t == null) { Missing(rel, "value"); return false; }
                if (bool.TryParse(t, out var b)) return b;
                _v.Fail($"'{At(rel)}' must be true or false (got '{t}').");
                return false;
            }

            internal bool FlagOr(string rel, bool fallback) => Has(rel) ? Flag(rel) : fallback;

            // ---- collections ------------------------------------------------------------------

            private List<string> Items(string rel)
            {
                _v.NoteSubtree(At(rel));
                return Raw(rel) is IList<object> list
                    ? list.OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s))
                          .Select(s => s.Trim()).ToList()
                    : new List<string>();
            }

            internal IReadOnlyList<string> Strings(string rel)
            {
                if (Raw(rel) is not IList<object>) { Missing(rel, "list"); return Array.Empty<string>(); }
                var cleaned = Items(rel);
                foreach (var dup in cleaned.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1))
                    _v.Fail($"'{At(rel)}' repeats '{dup.Key}'.");
                return cleaned;
            }

            internal IReadOnlyList<string> OptStrings(string rel) =>
                Raw(rel) is IList<object> ? Items(rel) : Array.Empty<string>();

            internal IReadOnlyList<IReadOnlyList<string>> StringLists(string rel)
            {
                _v.NoteSubtree(At(rel));
                return Raw(rel) is IList<object> list
                    ? list.Select(x => (IReadOnlyList<string>)(x is IList<object> inner
                        ? inner.OfType<string>().Select(s => s.Trim()).ToList()
                        : new List<string>())).ToList()
                    : Array.Empty<IReadOnlyList<string>>();
            }

            internal IReadOnlyDictionary<string, string> Map(string rel)
            {
                _v.NoteSubtree(At(rel));
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                if (Raw(rel) is IDictionary<object, object> d)
                    foreach (var kv in d) map[$"{kv.Key}"] = $"{kv.Value}";
                return map;
            }
        }
    }
}
