using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeGen.Hmi
{
    internal sealed class HmiConfigException : Exception
    {
        internal HmiConfigException(string file, string message)
            : base($"[Hmi] Config/{file} is invalid: {message}") { }
    }

    // Shared strict-YAML infrastructure for the two HMI configuration files.
    //
    // Strict on purpose. Unlike the other config loaders these do NOT ignore unmatched properties:
    // a key the schema does not know is a typo or a stale edit, and silently dropping it would leave
    // the generator running on a default the YAML appears to override. There are no C#-side defaults
    // to fall back to, so every required value must be present.
    internal static class HmiYaml
    {
        private static readonly IDeserializer Strict = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();       // no IgnoreUnmatchedProperties: unknown keys throw

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

        internal static T Deserialize<T>(string file, string yaml) where T : class
        {
            try { return Strict.Deserialize<T>(yaml) ?? throw new HmiConfigException(file, "empty document."); }
            catch (YamlException ex) { throw new HmiConfigException(file, ex.Message); }
        }

        // Caches a parsed definition against the file's timestamp so repeated generations in one
        // process re-read only when the file actually changes.
        internal sealed class Cache<T> where T : class
        {
            private readonly string _file;
            private readonly Func<string, T> _parse;
            private readonly object _gate = new();
            private T? _value;
            private DateTime _stampUtc;

            internal Cache(string file, Func<string, T> parse) { _file = file; _parse = parse; }

            internal T Load()
            {
                var path = PathOf(_file);
                if (_value != null && Stamp(path) == _stampUtc) return _value;
                lock (_gate)
                {
                    // The stamp is re-read INSIDE the lock and captured BEFORE the content, so a file
                    // rewritten between the two can never be cached under the older timestamp - which
                    // would pin stale content until the next unrelated edit.
                    var stamp = Stamp(path);
                    if (_value != null && stamp == _stampUtc) return _value;
                    var parsed = _parse(Read(_file));
                    _value = parsed;
                    _stampUtc = stamp;
                    return parsed;
                }
            }

            private static DateTime Stamp(string path) =>
                File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        // Accumulates every problem so one run reports the whole set rather than the first typo.
        internal class Validator
        {
            private readonly string _file;
            protected readonly List<string> _problems = new();

            internal Validator(string file) => _file = file;

            internal void Fail(string message) => _problems.Add(message);

            internal void Throw()
            {
                if (_problems.Count > 0)
                    throw new HmiConfigException(_file, Environment.NewLine + "  - " +
                        string.Join(Environment.NewLine + "  - ", _problems));
            }

            internal T Section<T>(T? section, string path) where T : class
            {
                if (section != null) return section;
                _problems.Add($"missing required section '{path}'.");
                Throw();
                throw new InvalidOperationException();   // unreachable: Throw always throws here
            }

            internal T Required<T>(T? value, string path) where T : struct
            {
                if (value.HasValue) return value.Value;
                _problems.Add($"missing required value '{path}'.");
                return default;
            }

            internal int Positive(int? value, string path)
            {
                var v = Required(value, path);
                if (v <= 0) _problems.Add($"'{path}' must be greater than zero (got {v}).");
                return v;
            }

            internal int NonNegative(int? value, string path)
            {
                var v = Required(value, path);
                if (v < 0) _problems.Add($"'{path}' must not be negative (got {v}).");
                return v;
            }

            internal string Text(string? value, string path)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value!.Trim();
                _problems.Add($"missing required value '{path}'.");
                return string.Empty;
            }

            // Anything used as a C# identifier or file stem must be safe to emit verbatim.
            internal string Identifier(string? value, string path)
            {
                var t = Text(value, path);
                if (t.Length > 0 && !t.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                    _problems.Add($"'{path}' must be a bare identifier (got '{t}').");
                return t;
            }

            internal string Guid(string? value, string path)
            {
                var t = Text(value, path);
                if (t.Length > 0 && !System.Guid.TryParse(t, out _))
                    _problems.Add($"'{path}' is not a valid GUID (got '{t}').");
                return t;
            }

            internal string Ip(string? value, string path)
            {
                var t = Text(value, path);
                if (t.Length > 0 && !IPAddress.TryParse(t, out _))
                    _problems.Add($"'{path}' is not a valid IP address (got '{t}').");
                return t;
            }

            internal int Port(int? value, string path)
            {
                var v = Required(value, path);
                if (v is < 1 or > 65535) _problems.Add($"'{path}' must be 1-65535 (got {v}).");
                return v;
            }

            // A template or destination name must stay inside its directory: no rooted paths, no
            // traversal, no directory separators at all.
            internal string SafeFileName(string? value, string path)
            {
                var t = Text(value, path);
                if (t.Length == 0) return t;
                if (Path.IsPathRooted(t) || t.Contains("..", StringComparison.Ordinal) ||
                    t.Contains('/') || t.Contains('\\'))
                    _problems.Add($"'{path}' must be a plain file name (got '{t}').");
                return t;
            }

            internal IReadOnlyList<string> Strings(List<string>? value, string path)
            {
                if (value == null) { _problems.Add($"missing required list '{path}'."); return Array.Empty<string>(); }
                var cleaned = value.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
                foreach (var dup in cleaned.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1))
                    _problems.Add($"'{path}' repeats '{dup.Key}'.");
                return cleaned;
            }

            internal IReadOnlyList<T> List<T>(List<T>? value, string path)
            {
                if (value != null && value.Count > 0) return value;
                _problems.Add($"'{path}' must declare at least one entry.");
                return Array.Empty<T>();
            }

            internal void Distinct<T>(IEnumerable<(string Path, T Value)> entries, string what)
            {
                foreach (var dup in entries.GroupBy(e => e.Value)
                             .Where(g => g.Count() > 1))
                    _problems.Add($"{what} '{dup.Key}' is used by {string.Join(", ", dup.Select(d => d.Path))}.");
            }
        }
    }
}
