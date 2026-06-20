using System;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeGen.Configuration
{
    internal sealed class YamlConfigFile<T> where T : class
    {
        private static readonly IDeserializer _yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        private readonly string _relativePath;
        private readonly object _gate = new();
        private T? _cached;
        private DateTime _cachedStampUtc;

        public Action<T>? OnLoaded { get; init; }

        public YamlConfigFile(params string[] relativePathSegments) =>
            _relativePath = Path.Combine(relativePathSegments);

        public T Load()
        {
            var path = ResolvePath();
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_cached != null && stamp == _cachedStampUtc) return _cached;
            lock (_gate)
            {
                stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (_cached != null && stamp == _cachedStampUtc) return _cached;
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        $"Config not found at '{path}'. It ships beside CodeGen.dll via a CodeGen.csproj " +
                        "<None CopyToOutputDirectory> entry; rebuild CodeGen.", path);
                _cached = _yaml.Deserialize<T>(File.ReadAllText(path))
                    ?? throw new InvalidOperationException($"'{path}' deserialized to null.");
                OnLoaded?.Invoke(_cached);
                _cachedStampUtc = stamp;
                return _cached;
            }
        }

        public string ResolvePath() => Path.Combine(AppContext.BaseDirectory, _relativePath);
    }
}
