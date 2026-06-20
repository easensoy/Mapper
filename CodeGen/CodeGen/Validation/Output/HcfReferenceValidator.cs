using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Split-brain guard. A deployed <c>.hcf</c> binds each physical DI/DO channel to a
    /// function-block pin. EAE resolves that binding against the SAME resource's
    /// <c>.sysres</c> FBNetwork — so every non-blank binding MUST be a Form-1 GUID triple
    /// <c>{resourceId}.{fbId}.{pin}</c> whose <c>{fbId}</c> is an FB actually present on that
    /// resource and whose <c>{pin}</c> is a real port on that FB's type. If the binding is a
    /// legacy symbolic name (<c>'RES0.M262IO.PusherAtHome'</c>, <c>'RES0.Robot_Pick_And_Place1.x'</c>)
    /// or points at an FB/pin that does not exist on the resource, the channel is "split-brain":
    /// the HCF references something the running application does not contain, so the I/O never
    /// resolves on the device.
    ///
    /// This validator parses every <c>.hcf</c> under the project's System tree, builds the FB-id
    /// + pin set from the matching <c>.sysres</c>, and returns one <see cref="Violation"/> per bad
    /// binding. The generation pipeline calls it after the symbol binders and FAILS LOUDLY on any
    /// violation; the byte-identical verification gate treats a non-empty result as a hard failure.
    /// </summary>
    public static class HcfReferenceValidator
    {
        public sealed record Violation(string Hcf, string Channel, string Value, string Reason)
        {
            public override string ToString() => $"{Hcf} {Channel} = \"{Value}\" -> {Reason}";
        }

        private static readonly Regex ChannelName = new(@"^D[IO]\d+$", RegexOptions.Compiled);
        private static readonly Regex Form1 =
            new(@"^([0-9A-Fa-f]{16})\.([0-9A-Fa-f]{16})\.([A-Za-z0-9_]+)$", RegexOptions.Compiled);

        /// <summary>Validate every HCF under <paramref name="eaeRoot"/>/IEC61499/System.</summary>
        public static List<Violation> Validate(string? eaeRoot)
        {
            var violations = new List<Violation>();
            if (string.IsNullOrEmpty(eaeRoot)) return violations;
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return violations;

            // Cache of FB-type -> set of interface var names (pins), parsed from the type .fbt.
            var pinsByType = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);

            foreach (var resFolder in Directory.EnumerateDirectories(systemDir, "*", SearchOption.AllDirectories))
            {
                var hcf = Directory.EnumerateFiles(resFolder, "*.hcf", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (hcf == null) continue;

                // FB id/mapping -> type, from EVERY .sysres in this resource folder (so an orphan
                // sysres cannot cause a false split-brain; the binding need only resolve in one).
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
                    catch { /* unreadable sysres -> treated as carrying no FBs */ }
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
                    if (name == null || !ChannelName.IsMatch(name)) continue;     // only DI/DO channels
                    var raw = ((string?)pv.Attribute("Value") ?? string.Empty).Trim();
                    var val = raw.Trim('\'');                                      // strip ST string quotes
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

        /// <summary>
        /// Interface var names (Input/Output) declared by the FB type's <c>.fbt</c>. Returns null
        /// when the .fbt can't be found/parsed — pin validation is then skipped for that type (we
        /// never raise a false split-brain just because a type file is missing).
        /// </summary>
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
                    // Symlink endpoints the CAT publishes as $${PATH}<name>: the HCF binds physical
                    // DI/DO channels to THESE (athome/atwork/Input/OutputToWork/OutputToWork1/...),
                    // which EAE resolves per-instance — they are not InterfaceList vars.
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
