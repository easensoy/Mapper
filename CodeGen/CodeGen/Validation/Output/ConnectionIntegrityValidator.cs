using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeGen.Validation.Output
{
    // Every generated connection must name endpoints that exist, and every VALUE-carrying input must be
    // driven once.
    //
    // Two failure modes this catches, both silent at generation time and both expensive on the rig:
    //
    //  * A DOUBLE-DRIVEN data or adapter input. Two sources on one such destination is not an error EAE
    //    reports; it resolves the conflict by evaluation order, so what a consumer reads is whichever wire
    //    happened to win. That is how a component spliced onto both a local ring and the cross-controller
    //    segment reported through one path on one build and the other path on the next.
    //
    //  * An endpoint naming an FB that is not in the artefact, or a port the FB's type does not declare.
    //    EAE drops what it cannot resolve, so the wire simply is not there at runtime.
    //
    // EVENT inputs are deliberately exempt from the one-source rule: an event input is a trigger, and
    // OR-ing triggers is the normal IEC 61499 idiom -- the resource bootstrap fires FB1.INIT from both
    // START.COLD and START.WARM on purpose. Only a value has a single well-defined writer.
    //
    // Port checking is skipped for a type whose .fbt is not deployed (library FBs), so an unknown type
    // degrades to endpoint-existence rather than a false failure.
    public static class ConnectionIntegrityValidator
    {
        // The resource's implicit start FB. EAE provides it; a sysres never declares it, so an endpoint
        // addressing it is resolved by the runtime rather than by the artefact.
        private const string ImplicitResourceStartFb = "START";

        public sealed record Violation(string Artefact, string Kind, string Detail)
        {
            public override string ToString() => $"{Artefact}: {Kind} — {Detail}";
        }

        private sealed record Ports(
            HashSet<string> EventIn, HashSet<string> EventOut,
            HashSet<string> DataIn, HashSet<string> DataOut,
            HashSet<string> Sockets, HashSet<string> Plugs);

        public static List<Violation> Validate(string? eaeRoot)
        {
            var violations = new List<Violation>();
            if (string.IsNullOrEmpty(eaeRoot)) return violations;
            var iec = Path.Combine(eaeRoot!, "IEC61499");
            if (!Directory.Exists(iec)) return violations;

            var byType = LoadDeployedTypes(iec);
            foreach (var artefact in Directory.EnumerateFiles(iec, "*.syslay", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(iec, "*.sysres", SearchOption.AllDirectories)))
                ValidateArtefact(artefact, byType, violations);
            return violations;
        }

        private static void ValidateArtefact(string path, IReadOnlyDictionary<string, Ports> byType,
            List<Violation> violations)
        {
            XDocument doc;
            try { doc = XDocument.Load(path); }
            catch (Exception ex)
            {
                violations.Add(new Violation(Path.GetFileName(path), "unreadable", ex.Message));
                return;
            }

            var fbTypeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
            {
                var name = (string?)fb.Attribute("Name");
                if (!string.IsNullOrEmpty(name)) fbTypeOf[name!] = (string?)fb.Attribute("Type") ?? string.Empty;
            }
            // Frames are canvas decoration, not FBs, but a connection never names one; listing them keeps
            // an endpoint check from mistaking a decoration for a missing FB.
            foreach (var frame in doc.Descendants().Where(e => e.Name.LocalName == "Frame"))
            {
                var name = (string?)frame.Attribute("Name");
                if (!string.IsNullOrEmpty(name)) fbTypeOf[name!] = string.Empty;
            }

            var artefact = Path.GetFileName(path);
            foreach (var section in doc.Descendants().Where(e =>
                         e.Name.LocalName is "EventConnections" or "DataConnections" or "AdapterConnections"))
            {
                var kind = section.Name.LocalName;
                var drivenBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var conn in section.Elements().Where(e => e.Name.LocalName == "Connection"))
                {
                    var source = ((string?)conn.Attribute("Source") ?? string.Empty).Trim();
                    var dest = ((string?)conn.Attribute("Destination") ?? string.Empty).Trim();
                    if (source.Length == 0 || dest.Length == 0)
                    {
                        violations.Add(new Violation(artefact, kind, $"connection with an empty endpoint ('{source}' -> '{dest}')"));
                        continue;
                    }
                    (drivenBy.TryGetValue(dest, out var l) ? l : drivenBy[dest] = new List<string>()).Add(source);
                    CheckEndpoint(artefact, kind, source, isSource: true, fbTypeOf, byType, violations);
                    CheckEndpoint(artefact, kind, dest, isSource: false, fbTypeOf, byType, violations);
                }

                if (kind == "EventConnections") continue;
                foreach (var kv in drivenBy.Where(kv => kv.Value.Count > 1))
                    violations.Add(new Violation(artefact, kind,
                        $"'{kv.Key}' is driven by {kv.Value.Count} sources ({string.Join(", ", kv.Value)}); " +
                        "a value has one writer, so which one arrives is undefined"));
            }
        }

        // `X.Y` addresses port Y on FB X; a bare name is the enclosing artefact's own boundary pin, which
        // has no FB to resolve against.
        private static void CheckEndpoint(string artefact, string kind, string endpoint, bool isSource,
            IReadOnlyDictionary<string, string> fbTypeOf, IReadOnlyDictionary<string, Ports> byType,
            List<Violation> violations)
        {
            int dot = endpoint.LastIndexOf('.');
            if (dot <= 0) return;
            var fb = endpoint.Substring(0, dot);
            var port = endpoint.Substring(dot + 1);
            if (string.Equals(fb, ImplicitResourceStartFb, StringComparison.Ordinal)) return;

            if (!fbTypeOf.TryGetValue(fb, out var type))
            {
                violations.Add(new Violation(artefact, kind,
                    $"'{endpoint}' names FB '{fb}', which this artefact does not declare"));
                return;
            }
            if (string.IsNullOrEmpty(type) || !byType.TryGetValue(type, out var ports)) return;

            bool known = kind switch
            {
                "EventConnections" => isSource ? ports.EventOut.Contains(port) : ports.EventIn.Contains(port),
                "DataConnections" => isSource ? ports.DataOut.Contains(port) : ports.DataIn.Contains(port),
                // An adapter wire runs plug -> socket, and an adapter's own events are addressed through
                // the declaration (stateRptCmdAdptr_in.CNF), so accept either half plus its members.
                _ => ports.Plugs.Contains(port) || ports.Sockets.Contains(port),
            };
            // Adapter members and the composite's own boundary pins are addressed as <declaration>.<member>;
            // the declaration itself is what this validator can resolve, so a dotted remainder is accepted.
            if (!known && (ports.Sockets.Contains(port) || ports.Plugs.Contains(port))) known = true;
            if (!known)
                violations.Add(new Violation(artefact, kind,
                    $"'{endpoint}' addresses port '{port}', which type '{type}' does not declare as " +
                    $"{(isSource ? "an output" : "an input")}"));
        }

        private static Dictionary<string, Ports> LoadDeployedTypes(string iecDir)
        {
            var byType = new Dictionary<string, Ports>(StringComparer.OrdinalIgnoreCase);
            foreach (var fbt in Directory.EnumerateFiles(iecDir, "*.fbt", SearchOption.AllDirectories))
            {
                XDocument doc;
                try { doc = XDocument.Load(fbt); } catch { continue; }
                var root = doc.Root;
                if (root == null) continue;
                var typeName = (string?)root.Attribute("Name") ?? Path.GetFileNameWithoutExtension(fbt);
                var iface = root.Elements().FirstOrDefault(e => e.Name.LocalName == "InterfaceList");
                if (iface == null) continue;

                HashSet<string> Names(string section, string element) =>
                    new(iface.Elements().Where(e => e.Name.LocalName == section)
                            .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == element))
                            .Select(e => (string?)e.Attribute("Name") ?? string.Empty)
                            .Where(n => n.Length > 0),
                        StringComparer.OrdinalIgnoreCase);

                byType[typeName] = new Ports(
                    Names("EventInputs", "Event"), Names("EventOutputs", "Event"),
                    Names("InputVars", "VarDeclaration"), Names("OutputVars", "VarDeclaration"),
                    Names("Sockets", "AdapterDeclaration"), Names("Plugs", "AdapterDeclaration"));
            }
            return byType;
        }
    }
}
