using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Post-generation MQTT sanity check. EAE 24.1's <c>MQTT_CONNECTION</c> is secure-by-default and
    /// its doc.xml defines two failure codes that are easy to ship by accident:
    /// <list type="bullet">
    ///   <item><b>ReturnCode 101</b> — a plain <c>mqtt://</c> URL with no device "Insecure Application"
    ///     override ("Secure URL required").</item>
    ///   <item><b>ReturnCode 100</b> — an <c>mqtts://</c> URL pointed at a NON-TLS port (e.g. mosquitto
    ///     on 1883): the TLS handshake fails ("TLS error"). This is the impossible config to catch.</item>
    /// </list>
    /// This validator reads every deployed <c>MQTT_CONNECTION</c>, prints its URL / ConnectionID /
    /// ClientIdentifier / ValidateCert (the connection matrix), and flags impossible / insecure configs
    /// so the operator knows exactly what the device will report before deploying. It does NOT change
    /// anything; the pipeline + gate just print its output.
    /// </summary>
    public static class MqttConnectionValidator
    {
        public sealed record Row(
            string Resource, string Fb, string Url, string ConnectionID,
            string ClientIdentifier, string ValidateCert);

        public sealed record Finding(string Resource, string Fb, string Detail, bool Impossible)
        {
            public override string ToString() =>
                (Impossible ? "[IMPOSSIBLE] " : "[INFO] ") + $"{Resource}.{Fb}: {Detail}";
        }

        /// <summary>Ports conventionally served as PLAIN MQTT (no TLS). mqtts:// against these fails.</summary>
        static readonly HashSet<int> PlainMqttPorts = new() { 1883, 1884 };

        public static (List<Row> Rows, List<Finding> Findings) Inspect(string? eaeRoot)
        {
            var rows = new List<Row>();
            var findings = new List<Finding>();
            if (string.IsNullOrEmpty(eaeRoot)) return (rows, findings);
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return (rows, findings);

            foreach (var sysres in Directory.EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories))
            {
                XDocument doc;
                try { doc = XDocument.Load(sysres); }
                catch { continue; }

                var resLabel = (string?)doc.Root?.Attribute("Name")
                    ?? Path.GetFileNameWithoutExtension(sysres);

                foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
                {
                    var type = (string?)fb.Attribute("Type") ?? string.Empty;
                    if (!type.StartsWith("MQTT_CONNECTION", StringComparison.Ordinal)) continue;
                    var name = (string?)fb.Attribute("Name") ?? string.Empty;

                    var p = fb.Elements().Where(e => e.Name.LocalName == "Parameter")
                        .Where(e => !string.IsNullOrEmpty((string?)e.Attribute("Name")))
                        .GroupBy(e => (string)e.Attribute("Name")!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key,
                                      g => (((string?)g.First().Attribute("Value")) ?? string.Empty).Trim().Trim('\''),
                                      StringComparer.OrdinalIgnoreCase);

                    string url = p.GetValueOrDefault("URL", string.Empty);
                    string cid = p.GetValueOrDefault("ConnectionID", string.Empty);
                    string clid = p.GetValueOrDefault("ClientIdentifier", string.Empty);
                    string vc = p.TryGetValue("ValidateCert", out var v) ? v : "(FB default)";
                    rows.Add(new(resLabel, name, url, cid, clid, vc));

                    bool isMqtts = url.StartsWith("mqtts://", StringComparison.OrdinalIgnoreCase) ||
                                   url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
                    bool isMqtt = url.StartsWith("mqtt://", StringComparison.OrdinalIgnoreCase) ||
                                  url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);
                    int port = PortOf(url);

                    if (isMqtts && PlainMqttPorts.Contains(port))
                        findings.Add(new(resLabel, name,
                            $"mqtts:// (TLS) to port {port} (a plain-MQTT port) — the TLS handshake will FAIL " +
                            "-> MQTT_CONNECTION ReturnCode 100. Use mqtt:// (+ enable EAE 'Insecure Application' " +
                            "on this device) for a plain broker, or point mqtts:// at a TLS listener (e.g. 8883).",
                            true));
                    else if (isMqtt)
                        findings.Add(new(resLabel, name,
                            "insecure mqtt:// — EAE returns ReturnCode 101 ('Secure URL required') UNLESS this " +
                            "device has 'Security -> Insecure Application -> Enable' set in EAE. With it enabled, " +
                            "this is the smallest path to ReturnCode 0 against a plain broker.",
                            false));
                    else if (!isMqtts)
                        findings.Add(new(resLabel, name,
                            $"URL scheme is not mqtt:// / mqtts:// / ws:// / wss:// (URL='{url}') — EAE rejects it " +
                            "('The URI scheme is not MQTT').",
                            true));
                }
            }
            return (rows, findings);
        }

        static int PortOf(string url)
        {
            var m = Regex.Match(url, @"://[^:/]+:(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : -1;
        }
    }
}
