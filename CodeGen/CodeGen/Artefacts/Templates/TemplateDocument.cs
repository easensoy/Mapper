using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CodeGen.Configuration;

namespace CodeGen.Services
{
    // Loads a complete IEC/EAE document from the Template Library and substitutes {{Token}}
    // placeholders. Static XML belongs in Template Library as a real artefact; C# supplies only the
    // values the model or the profile decides.
    internal static class TemplateDocument
    {
        internal static string Load(MapperConfig? cfg, string relativePath,
            IReadOnlyDictionary<string, string>? tokens = null)
        {
            var path = Path.Combine(
                (cfg ?? throw new ArgumentNullException(nameof(cfg))).RequireTemplateLibraryPath(),
                relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Template Library document not found: {path}", path);

            // Decoded byte-faithfully: File.ReadAllText strips a leading BOM, but some EAE
            // artefacts (the empty .syslay) are written WITH one, so the template must keep it.
            var text = new UTF8Encoding(false).GetString(File.ReadAllBytes(path));
            if (tokens == null) return text;

            foreach (var kv in tokens)
            {
                var placeholder = "{{" + kv.Key + "}}";
                if (text.IndexOf(placeholder, StringComparison.Ordinal) < 0)
                    throw new InvalidOperationException(
                        $"Template '{relativePath}' has no placeholder {placeholder}.");
                text = text.Replace(placeholder, kv.Value);
            }
            if (text.Contains("{{", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Template '{relativePath}' still has unsubstituted placeholders.");
            return text;
        }
    }
}
