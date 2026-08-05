using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using CodeGen.Configuration;

namespace CodeGen.Validation.Output
{
    // Address-role guard over the generated EAE topology. It exists because an address collision is only
    // visible ACROSS emitters: the RevPi device (RevPiDeviceEmitter) and the HMI panel (HmiRuntimeEmitter)
    // are written by unrelated code paths that hardcode the SAME broadcast domain, so neither can see the
    // other's claim and no single-file check can catch it.
    //
    // Two failures this catches that nothing else did:
    //  1. A Soft dPAC CONTAINER sharing an address with another endpoint. A container is a Docker macvlan
    //     child holding its own MAC/IP, and it is the endpoint EAE deploys and logs in to, so it must be
    //     uniquely reachable. Every container the Mapper emits is unique today; a third one pointed at an
    //     address another container already owns would silently break both.
    //  2. A RevPi whose host and container addresses are equal or malformed. They are different layer-2
    //     endpoints — the HOST runs the Soft dPAC Manager on 8080, the CONTAINER runs the IEC 61499
    //     runtime — so collapsing them onto one address cannot work, yet nothing rejected it.
    //
    // Host-NIC duplicates are reported as WARNING, not ERROR, on purpose: the shipped Demonstrator AND
    // Jyotsna's reference both carry 192.168.1.2 on two host NICs and both import into EAE, so failing on
    // that would reject known-good output. Only container-level collisions are treated as fatal.
    public static class TopologyAddressValidator
    {
        public const string Error = "ERROR";
        public const string Warn = "WARN";

        public sealed record Violation(string Severity, string Scope, string Detail)
        {
            public bool IsError => string.Equals(Severity, Error, StringComparison.Ordinal);
            public override string ToString() => $"[{Severity}][{Scope}] {Detail}";
        }

        sealed record Endpoint(string FileName, string Equipment, string Catalog, string Ip, string Domain)
        {
            // A Soft dPAC container's catalog is *SoftdpacContainer* (HMIB1X_SoftdpacContainer_V01…,
            // SoftdpacContainer_V01…). These carry the RuntimeDEO EAE deploys to.
            public bool IsSoftDpacContainer =>
                Catalog.Contains("SoftdpacContainer", StringComparison.OrdinalIgnoreCase);
        }

        // "" and 0.0.0.0 are "unconfigured", not a claim on an address.
        static bool IsRealAddress(string? ip) =>
            !string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0";

        public static List<Violation> Validate(string? eaeRoot, MapperConfig? cfg)
        {
            var violations = new List<Violation>();
            if (cfg != null) violations.AddRange(ValidateRevPiRoles(cfg));
            if (string.IsNullOrEmpty(eaeRoot)) return violations;

            var topologyDir = Path.Combine(eaeRoot, "Topology");
            if (!Directory.Exists(topologyDir)) return violations;

            var endpoints = new List<Endpoint>();
            foreach (var file in Directory.EnumerateFiles(topologyDir, "Equipment_*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    Collect(doc.RootElement, Path.GetFileName(file), equipment: "", catalog: "", endpoints);
                }
                catch (Exception ex)
                {
                    violations.Add(new(Warn, "Topology",
                        $"unreadable topology equipment '{Path.GetFileName(file)}': {ex.Message}"));
                }
            }

            foreach (var group in endpoints
                         .GroupBy(e => (e.Ip, e.Domain))
                         .Where(g => g.Select(e => e.Equipment + "|" + e.FileName).Distinct(StringComparer.Ordinal).Count() > 1)
                         .OrderBy(g => g.Key.Ip, StringComparer.Ordinal))
            {
                var owners = string.Join(" AND ", group
                    .Select(e => $"{e.Equipment} [{(e.IsSoftDpacContainer ? "container" : "host")}] in {e.FileName}")
                    .Distinct(StringComparer.Ordinal));

                bool containerInvolved = group.Any(e => e.IsSoftDpacContainer);
                violations.Add(containerInvolved
                    ? new(Error, "Topology-Address",
                        $"{group.Key.Ip} is claimed by a Soft dPAC CONTAINER and another endpoint in broadcast domain " +
                        $"{Short(group.Key.Domain)}: {owners} — a container is a macvlan child with its own MAC and is " +
                        "EAE's deploy/login target, so it must own its address exclusively")
                    : new(Warn, "Topology-Address",
                        $"{group.Key.Ip} is claimed by {group.Select(e => e.Equipment).Distinct(StringComparer.Ordinal).Count()} " +
                        $"host NICs in broadcast domain {Short(group.Key.Domain)}: {owners} — pre-existing and tolerated by " +
                        "EAE (the reference solution carries the same duplicate), but it should be resolved"));
            }

            return violations;
        }

        // Config-level role checks: these hold regardless of what reached the topology folder, so they
        // still fire when generation aborts before any equipment JSON is written.
        public static IEnumerable<Violation> ValidateRevPiRoles(MapperConfig cfg)
        {
            bool revPiSelected = MapperConfig.FeedStationController == FeedController.RevPi
                              || MapperConfig.PartialRevPi;
            if (!revPiSelected) yield break;

            string host = cfg.RevPiHostIp ?? string.Empty;
            string target = cfg.RevPiTargetIp ?? string.Empty;

            foreach (var (label, value) in new[] { ("hostIp", host), ("targetIp", target) })
            {
                if (string.IsNullOrWhiteSpace(value))
                    yield return new(Error, "RevPi-Address",
                        $"revPi.{label} is empty in Config/device.yml — the RevPi cannot be addressed");
                else if (!IPAddress.TryParse(value, out var parsed) ||
                         parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    yield return new(Error, "RevPi-Address",
                        $"revPi.{label} ('{value}') is not a valid IPv4 address");
            }

            if (!string.IsNullOrWhiteSpace(host) && string.Equals(host, target, StringComparison.Ordinal))
                yield return new(Error, "RevPi-Address",
                    $"revPi.hostIp and revPi.targetIp are both '{host}' — the Docker HOST and its macvlan " +
                    "CONTAINER are separate layer-2 endpoints and cannot share one address. The host runs the " +
                    "Soft dPAC Manager (8080); the container runs the IEC 61499 runtime EAE deploys to.");
        }

        // Walk the equipment tree, carrying the nearest enclosing equipment's identifier/catalogReference
        // down to each endpoint so a collision can be reported against a name a human recognises.
        static void Collect(JsonElement el, string fileName, string equipment, string catalog,
                            List<Endpoint> sink)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("catalogReference", out var cat))
                    {
                        catalog = cat.GetString() ?? catalog;
                        if (el.TryGetProperty("identifier", out var id))
                            equipment = id.GetString() ?? equipment;
                    }

                    if (el.TryGetProperty("ipAddress", out var ipEl))
                    {
                        var ip = ipEl.GetString();
                        if (IsRealAddress(ip))
                        {
                            var domain = el.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
                            sink.Add(new Endpoint(fileName, equipment, catalog, ip!, domain));
                        }
                    }

                    foreach (var prop in el.EnumerateObject())
                        Collect(prop.Value, fileName, equipment, catalog, sink);
                    break;

                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                        Collect(item, fileName, equipment, catalog, sink);
                    break;
            }
        }

        static string Short(string domain) =>
            string.IsNullOrWhiteSpace(domain) ? "(none)"
            : domain.StartsWith("00000000-0000", StringComparison.Ordinal) ? "NoConf"
            : domain.Length >= 8 ? domain[..8] : domain;
    }
}
