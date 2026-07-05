using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    // Split-brain guard: every non-blank HCF DI/DO binding MUST be a Form-1 GUID triple
    // {resId}.{fbId}.{pin} whose fbId is an FB on the SAME resource's .sysres and whose pin is a
    // real port on that FB's type; a legacy symbolic name or a missing FB/pin never resolves on the device.
    public static class HcfReferenceValidator
    {
        public sealed record Violation(string Hcf, string Channel, string Value, string Reason)
        {
            public override string ToString() => $"{Hcf} {Channel} = \"{Value}\" -> {Reason}";
        }

        private static readonly Regex ChannelName = new(@"^D[IO]\d+$", RegexOptions.Compiled);
        private static readonly Regex Form1 =
            new(@"^([0-9A-Fa-f]{16})\.([0-9A-Fa-f]{16})\.([A-Za-z0-9_]+)$", RegexOptions.Compiled);

        public static List<Violation> Validate(string? eaeRoot)
        {
            var violations = new List<Violation>();
            if (string.IsNullOrEmpty(eaeRoot)) return violations;
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return violations;

            var pinsByType = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);

            foreach (var resFolder in Directory.EnumerateDirectories(systemDir, "*", SearchOption.AllDirectories))
            {
                var hcf = Directory.EnumerateFiles(resFolder, "*.hcf", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (hcf == null) continue;

                // Scan EVERY .sysres in the folder so an orphan sysres can't cause a false split-brain.
                var fbType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sysres in Directory.EnumerateFiles(resFolder, "*.sysres", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var doc = XDocument.Load(sysres);
                        foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
                        {
                            var type = (string?)fb.Attribute("Type") ?? string.Empty;
                            var id = (string?)fb.Attribute("ID");
                            var map = (string?)fb.Attribute("Mapping");
                            if (!string.IsNullOrEmpty(id)) fbType[id!] = type;
                            if (!string.IsNullOrEmpty(map)) fbType[map!] = type;
                        }
                    }
                    catch { }
                }

                XDocument hdoc;
                try { hdoc = XDocument.Load(hcf); }
                catch (Exception ex)
                {
                    violations.Add(new(Path.GetFileName(hcf), "(file)", "", $"unreadable HCF: {ex.Message}"));
                    continue;
                }

                foreach (var pv in hdoc.Descendants().Where(e => e.Name.LocalName == "ParameterValue"))
                {
                    var name = (string?)pv.Attribute("Name");
                    if (name == null || !ChannelName.IsMatch(name)) continue;
                    var raw = ((string?)pv.Attribute("Value") ?? string.Empty).Trim();
                    var val = raw.Trim('\'');
                    if (string.IsNullOrWhiteSpace(val)) continue;                 // blank = unbound, OK

                    var m = Form1.Match(val);
                    if (!m.Success)
                    {
                        violations.Add(new(Path.GetFileName(hcf), name, val,
                            "not a Form-1 GUID triple {resId}.{fbId}.{pin} (legacy symbolic reference)"));
                        continue;
                    }

                    var fbId = m.Groups[2].Value;
                    var pin = m.Groups[3].Value;
                    if (!fbType.TryGetValue(fbId, out var type))
                    {
                        violations.Add(new(Path.GetFileName(hcf), name, val,
                            $"FB id {fbId} is NOT present in this resource's sysres"));
                        continue;
                    }

                    var pins = PinsForType(eaeRoot, type, pinsByType);
                    if (pins != null && pins.Count > 0 && !pins.Contains(pin))
                        violations.Add(new(Path.GetFileName(hcf), name, val,
                            $"pin '{pin}' is NOT a port on FB type '{type}'"));
                }
            }
            return violations;
        }

        // Null when the .fbt can't be found/parsed -> pin validation skipped (no false split-brain
        // just because a type file is missing).
        private static HashSet<string>? PinsForType(string eaeRoot, string type,
            Dictionary<string, HashSet<string>?> cache)
        {
            if (string.IsNullOrEmpty(type)) return null;
            if (cache.TryGetValue(type, out var cached)) return cached;

            HashSet<string>? pins = null;
            try
            {
                var fbt = Path.Combine(eaeRoot, "IEC61499", type, type + ".fbt");
                if (File.Exists(fbt))
                {
                    var text = File.ReadAllText(fbt);
                    pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var doc = XDocument.Parse(text);
                    foreach (var vd in doc.Descendants().Where(e => e.Name.LocalName == "VarDeclaration"))
                    {
                        var n = (string?)vd.Attribute("Name");
                        if (!string.IsNullOrEmpty(n)) pins.Add(n!);
                    }
                    // The HCF binds DI/DO channels to $${PATH}<name> symlink endpoints (athome/atwork/
                    // Input/OutputToWork/...), which EAE resolves per-instance — not InterfaceList vars.
                    foreach (Match sm in Regex.Matches(text, @"\$\$\{PATH\}(\w+)"))
                        pins.Add(sm.Groups[1].Value);
                }
            }
            catch { pins = null; }

            cache[type] = pins;
            return pins;
        }
    }
}
