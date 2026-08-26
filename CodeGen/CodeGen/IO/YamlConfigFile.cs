using System;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeGen.Configuration
{
    // THE one reader of a declaration file: same naming convention, same converters, same strictness
    // wherever a .yml is parsed. A second builder elsewhere is a second answer to how a declaration is
    // read - which is how `plc: M262` could mean one thing in one place and nothing in another.
    public static class YamlDeclarations
    {
        public static readonly IDeserializer Reader = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new PlcAssignmentYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();
    }

    internal sealed class YamlConfigFile<T> where T : class
    {
        private readonly string _relativePath;
        private readonly object _gate = new();
        private T? _cached;
        private DateTime _cachedStampUtc;

        public Action<T>? OnLoaded { get; init; }

        public YamlConfigFile(params string[] relativePathSegments) =>
            _relativePath = Path.Combine(relativePathSegments);

        // The default bundle - the Config folder shipped beside CodeGen.dll - is mtime-cached, because
        // every run in a process reads the same files. A run given its OWN bundle root is read fresh and
        // cached nowhere: two runs holding different profiles must not share one slot, and that is the
        // whole point of being handed a root.
        public T Load(string? root = null)
        {
            var path = ResolvePath(root);
            if (root != null) return Read(path);

            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_cached != null && stamp == _cachedStampUtc) return _cached;
            lock (_gate)
            {
                stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (_cached != null && stamp == _cachedStampUtc) return _cached;
                _cached = Read(path);
                _cachedStampUtc = stamp;
                return _cached;
            }
        }

        T Read(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Config not found at '{path}'. It ships beside CodeGen.dll via a CodeGen.csproj " +
                    "<None CopyToOutputDirectory> entry; rebuild CodeGen.", path);
            var loaded = YamlDeclarations.Reader.Deserialize<T>(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"'{path}' deserialized to null.");
            OnLoaded?.Invoke(loaded);
            return loaded;
        }

        public string ResolvePath(string? root = null) => Path.Combine(root ?? AppContext.BaseDirectory, _relativePath);
    }
}
