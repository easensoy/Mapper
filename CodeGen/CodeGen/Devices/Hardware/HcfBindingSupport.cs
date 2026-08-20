using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeGen.Configuration;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    // Shared parsing/IO helpers for the per-PLC HCF symbol binders.
    public static class HcfBindingSupport
    {
        // Split a channel value into head.mid.last; false for empty values, literals and T#... durations.
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

        // Name -> FB id: the .hcf channel's middle segment must be this id or EAE cannot resolve the link.
        public static Dictionary<string, string> BuildComponentIdMap(string sysdevFolder) =>
            ReadFbAttribute(sysdevFolder, "ID");

        // Component name -> the CAT type actually emitted for it. The deployed sysres is the single source
        // of truth, so a binder reads the port vocabulary here instead of keying off a model name.
        public static Dictionary<string, string> BuildComponentTypeMap(string sysdevFolder) =>
            ReadFbAttribute(sysdevFolder, "Type");


        private static Dictionary<string, string> ReadFbAttribute(string sysdevFolder, string attribute)
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
                    var name = (string?)fb.Attribute("Name");
                    var value = (string?)fb.Attribute(attribute);
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value)) map[name!] = value!;
                }
            }
            catch { }
            return map;
        }

        public static string? FindSysdevByType(string eaeRoot, string deviceType, string deviceNamespace) =>
            EaeProjectLayout.FindSysdevByDeviceType(eaeRoot, deviceType);

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
            Services.FbtXmlEditor.SaveXmlRetrying(hcfPath, settings, doc.Save);
        }
    }
}
