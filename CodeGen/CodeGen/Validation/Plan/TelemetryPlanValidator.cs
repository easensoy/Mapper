using System;
using System.Collections.Generic;
using System.Linq;
using CodeGen.Configuration;
using CodeGen.Mapping;

namespace CodeGen.Translation
{
    // A resource's publishers bind to a connection on their OWN resource: without one they drop every
    // message silently, and a connection nothing starts never opens. Neither failure surfaces anywhere -
    // the project generates, deploys and simply publishes nothing - so the declaration is proved against
    // the finished plan HERE, before any file is written.
    internal static class TelemetryPlanValidator
    {
        public static void Validate(GenerationContext ctx)
        {
            if (!ctx.Config.MqttPublishEnabled) return;

            var errors = new List<string>();
            var declared = ctx.Cfg.Telemetry.Connections;

            foreach (var g in declared.GroupBy(c => c.Plc).Where(g => g.Count() > 1))
                errors.Add($"telemetry.yml declares {g.Count()} connections for target '{g.Key}'");
            foreach (var c in declared.Where(c => !TargetRegistry.IsRegistered(c.Plc)))
                errors.Add($"telemetry.yml declares a connection for '{c.Plc}', which no backend implements");

            foreach (var target in TargetRegistry.All.Where(t => ctx.Emits(t.Plc)))
            {
                var connection = declared.FirstOrDefault(c => c.Plc == target.Plc);
                if (connection == null)
                {
                    if (Publishes(ctx, target.Plc))
                        errors.Add($"resource '{target.ResourceName}' hosts publishers but telemetry.yml " +
                                   "declares no connection on it, so every message it publishes is dropped");
                    continue;
                }

                var name = connection.NameFor(ctx.Config.UseTelemetryCat);
                if (string.IsNullOrWhiteSpace(name))
                    errors.Add($"the connection on '{target.ResourceName}' has no instance name for the " +
                               "form this run emits");
                else if (connection.IsDrawnAtRosterRow && ctx.Roster.Get(name) == null)
                    errors.Add($"the connection on '{target.ResourceName}' is drawn at its roster row, but " +
                               $"layout.yml gives '{name}' no row, so it would be drawn at the origin");

                var resource = ctx.ResourceFor(target.Plc);
                var starter = connection.BroughtUpBy switch
                {
                    ConnectionStarter.Area => resource.AreaFb,
                    ConnectionStarter.Station => resource.StationFb,
                    ConnectionStarter.RingHead => resource.InitAnchor,
                    _ => "self",
                };
                if (string.IsNullOrWhiteSpace(starter))
                    errors.Add($"the connection on '{target.ResourceName}' is started by its " +
                               $"'{connection.BroughtUpBy}', which that resource does not have, so nothing " +
                               "would bring it up");
            }

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "[MQTT] the telemetry declaration does not match the planned resources:" +
                    Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", errors));
        }

        // A resource publishes when it hosts a component whose CAT carries the embedded tap.
        private static bool Publishes(GenerationContext ctx, PlcAssignment plc) =>
            ctx.Station.Sensors.Concat(ctx.Station.Actuators)
                .Select(c => (c.Name ?? string.Empty).Trim())
                .Where(n => n.Length > 0 && ctx.Allocation.Of(n) == plc)
                .Any(n => ctx.CatTypes.TryGetValue(n, out var cat) &&
                          TemplateManifest.Find(cat)?.Telemetry != null);
    }
}
