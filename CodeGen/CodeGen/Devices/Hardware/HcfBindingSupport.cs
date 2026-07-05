using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    // Shared parsing/IO helpers for the per-PLC HCF symbol binders (M580SymbolBinder, BX1SymbolBinder).
    public static class HcfBindingSupport
    {
        // Split a channel value into head.mid.last (strips wrapping quotes); false for empty values,
        // string literals, and T#... durations.
        public static bool TrySplitSymlink(string raw, out string head, out string mid, out string last)
        {
            head = mid = last = string.Empty;
            var t = raw.Trim();
            if (t.Length == 0) return false;
            bool quoted = t.Length >= 2 && t[0] == '\'' && t[^1] == '\'';
            var inner = quoted ? t.Substring(1, t.Length - 2).Trim() : t;
            if (inner.Length == 0) return false;
            if (inner.StartsWith("T#", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = inner.Split('.');
            if (parts.Length != 3) return false;
            if (parts.Any(p => p.Length == 0)) return false;
            head = parts[0]; mid = parts[1]; last = parts[2];
            return true;
        }

        // Component instance Name -> FB id, from the deployed .sysres. The .hcf channel's middle
        // segment must be this id so EAE resolves the link to the FB instance.
        public static Dictionary<string, string> BuildComponentIdMap(string sysdevFolder)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var sysres = Directory.Exists(sysdevFolder)
                    ? Directory.EnumerateFiles(sysdevFolder, "*.sysres").FirstOrDefault()
                    : null;
                if (sysres == null) return map;
                var root = XDocument.Load(sysres).Root;
                if (root == null) return map;
                XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "FBNetwork");
                if (net == null) return map;
                foreach (var fb in net.Elements(ns + "FB"))
                {
                    var n = (string?)fb.Attribute("Name");
                    var id = (string?)fb.Attribute("ID");
                    if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(id))
                        map[n!] = id!;
                }
            }
            catch { }
            return map;
        }

        public static string? FindSysdevByType(string eaeRoot, string deviceType, string deviceNamespace)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            foreach (var sd in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var root = XDocument.Load(sd).Root;
                    if (root == null || root.Name.LocalName != "Device") continue;
                    if ((string?)root.Attribute("Type") == deviceType &&
                        (string?)root.Attribute("Namespace") == deviceNamespace)
                        return sd;
                }
                catch { }
            }
            return null;
        }

        // Read the deployed resource ID (prefers the .sysres root ID attribute, else the file stem) and Name.
        public static (string Id, string? Name) ReadSysresIdentity(string sysdevFolder)
        {
            try
            {
                var sysres = Directory.Exists(sysdevFolder)
                    ? Directory.EnumerateFiles(sysdevFolder, "*.sysres").FirstOrDefault()
                    : null;
                if (sysres == null) return (string.Empty, null);
                string id = Path.GetFileNameWithoutExtension(sysres);
                string? name = null;
                try
                {
                    var root = XDocument.Load(sysres).Root;
                    var rootId = (string?)root?.Attribute("ID");
                    if (!string.IsNullOrWhiteSpace(rootId)) id = rootId!;
                    name = (string?)root?.Attribute("Name");
                }
                catch { }
                return (id, name);
            }
            catch { return (string.Empty, null); }
        }

        // Save with UTF-8 + BOM, retrying if EAE briefly holds a write lock.
        public static void SaveHcf(XDocument doc, string hcfPath)
        {
            var settings = new System.Xml.XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            };
            const int MaxAttempts = 8;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(hcfPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 800);
                }
                catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 800);
                }
            }
        }
    }
}
