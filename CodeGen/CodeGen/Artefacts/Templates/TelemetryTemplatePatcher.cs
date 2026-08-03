using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using static CodeGen.Services.FbtXmlEditor;

namespace CodeGen.Services
{
    // Deploy-time artifacts for the Telemetry_CAT connection wrapper: the Config/Health datatypes it
    // exposes and the sweep that retires the whole wrapper. Consumed via `using static` so the call sites
    // in TemplateLibraryDeployer stay unqualified. (The per-component embedded MQTT publish/formatter is a
    // separate concern and stays with the actuator/sensor CAT patches.)
    internal static class TelemetryTemplatePatcher
    {

        // Deploy the TelemetryConfig datatype (the Telemetry_CAT Config input). Idempotent.
        internal static void DeployTelemetryConfigDatatype(MapperConfig cfg, string eaeProjectDir, DeployResult result)
            => DeployDatatype(eaeProjectDir, "TelemetryConfig",
                TemplateDocument.Load(cfg, @"DataType\TelemetryConfig.dt"), result);


        // Deploy the TelemetryHealth datatype (the Telemetry_CAT Health output). Idempotent.
        internal static void DeployTelemetryHealthDatatype(MapperConfig cfg, string eaeProjectDir, DeployResult result)
            => DeployDatatype(eaeProjectDir, "TelemetryHealth",
                TemplateDocument.Load(cfg, @"DataType\TelemetryHealth.dt"), result);

        // Removes deployed Telemetry wrapper artifacts (files + .dfbproj entries): the composite (BOTH the
        // current Telemetry.fbt AND the legacy Telemetry_CAT.fbt name, migrated away on re-deploy), its
        // .composite.offline.xml, the helper FBs TelemetryUnpack/TelemetryPack.fbt, and the datatypes
        // TelemetryConfig/TelemetryHealth.dt. Called on the flag-OFF path and at the top of flag-ON (clean
        // slate before a fresh deploy). Idempotent.
        internal static void SweepTelemetryCat(string eaeProjectDir, DeployResult result)
        {
            try
            {
                var iec = Path.Combine(eaeProjectDir, "IEC61499");
                int filesGone = 0;
                foreach (var rel in new[]
                {
                    "Telemetry.fbt",
                    "Telemetry.composite.offline.xml",
                    "Telemetry_CAT.fbt",                    // legacy name (pre-rename) — migrate away
                    "Telemetry_CAT.composite.offline.xml",
                    "TelemetryUnpack.fbt",
                    "TelemetryPack.fbt",
                    Path.Combine("DataType", "TelemetryConfig.dt"),
                    Path.Combine("DataType", "TelemetryHealth.dt"),
                })
                {
                    var p = Path.Combine(iec, rel);
                    if (File.Exists(p)) { File.Delete(p); filesGone++; }
                }

                var dfbproj = Path.Combine(iec, "IEC61499.dfbproj");
                int entriesGone = 0;
                if (File.Exists(dfbproj))
                {
                    var doc = XDocument.Load(dfbproj, LoadOptions.PreserveWhitespace);
                    bool Match(string? inc) => inc != null &&
                        (inc.Equals("Telemetry.fbt", StringComparison.OrdinalIgnoreCase) ||
                         inc.Equals("Telemetry.composite.offline.xml", StringComparison.OrdinalIgnoreCase) ||
                         inc.Equals("Telemetry_CAT.fbt", StringComparison.OrdinalIgnoreCase) ||
                         inc.Equals("Telemetry_CAT.composite.offline.xml", StringComparison.OrdinalIgnoreCase) ||
                         inc.Equals("TelemetryUnpack.fbt", StringComparison.OrdinalIgnoreCase) ||
                         inc.Equals("TelemetryPack.fbt", StringComparison.OrdinalIgnoreCase) ||
                         inc.EndsWith("TelemetryConfig.dt", StringComparison.OrdinalIgnoreCase) ||
                         inc.EndsWith("TelemetryHealth.dt", StringComparison.OrdinalIgnoreCase));
                    foreach (var el in doc.Descendants()
                        .Where(e => (e.Name.LocalName == "Compile" || e.Name.LocalName == "None")
                            && Match((string?)e.Attribute("Include"))).ToList())
                    { el.Remove(); entriesGone++; }
                    if (entriesGone > 0) doc.Save(dfbproj);
                }
                if (filesGone > 0 || entriesGone > 0)
                    result.PatchesApplied.Add($"Telemetry artifacts swept: {filesGone} file(s) + {entriesGone} dfbproj entry(ies) removed");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"SweepTelemetryCat failed: {ex.Message}");
            }
        }
    }
}
