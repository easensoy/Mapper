using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using static CodeGen.Services.FbtXmlEditor;

namespace CodeGen.Services
{
    // Deploy-time artifacts for the Telemetry_CAT connection wrapper: the Config/Health datatypes it
    // exposes and the sweep that retires the whole wrapper. Consumed via `using static` so the call sites
    // in TemplateLibraryDeployer stay unqualified. (The per-component embedded MQTT publish/formatter is a
    // separate concern and stays with the actuator/sensor CAT patches.)
    internal static class TelemetryTemplatePatcher
    {
        // TelemetryConfig: the resource-telemetry connection inputs folded into one struct —
        // matches MQTT_CONNECTION's QI/ConnectionID/URL/ClientIdentifier/ValidateCert/CACert types.
        const string TelemetryConfigDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"TelemetryConfig\" Comment=\"Telemetry connection config: wraps the MQTT_CONNECTION inputs\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"6/21/2026\" Remarks=\"single STRUCT input for Telemetry\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            // STRING members are explicitly sized: an unsized STRING defaults to STRING[15] in EAE,
            // and the 24-char URL 'mqtt://192.168.1.50:1883' then triggers ERR_CONST_INIT on the struct
            // initializer. Sizes must match TelemetryUnpack's output vars (the ST copy must not truncate).
            "    <VarDeclaration Name=\"QI\" Type=\"BOOL\" />\r\n" +
            "    <VarDeclaration Name=\"ConnectionID\" Type=\"STRING[80]\" />\r\n" +
            "    <VarDeclaration Name=\"URL\" Type=\"STRING[255]\" />\r\n" +
            "    <VarDeclaration Name=\"ClientIdentifier\" Type=\"STRING[80]\" />\r\n" +
            "    <VarDeclaration Name=\"ValidateCert\" Type=\"USINT\" />\r\n" +
            "    <VarDeclaration Name=\"CACert\" Type=\"STRING[255]\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        // Deploy the TelemetryConfig datatype (the Telemetry_CAT Config input). Idempotent.
        internal static void DeployTelemetryConfigDatatype(string eaeProjectDir, DeployResult result)
            => DeployDatatype(eaeProjectDir, "TelemetryConfig", TelemetryConfigDt, result);

        // TelemetryHealth: the MQTT_CONNECTION status outputs folded into one struct.
        const string TelemetryHealthDt =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!DOCTYPE DataType SYSTEM \"../LibraryElement.dtd\">\r\n" +
            "<DataType Namespace=\"Main\" Name=\"TelemetryHealth\" Comment=\"Telemetry connection health: wraps the MQTT_CONNECTION status outputs\">\r\n" +
            "  <Identification Standard=\"1131-3\" />\r\n" +
            "  <VersionInfo Organization=\"WMG\" Version=\"0.1\" Author=\"easensoy\" Date=\"6/21/2026\" Remarks=\"single STRUCT output for Telemetry\" />\r\n" +
            "  <CompilerInfo />\r\n" +
            "  <StructuredType>\r\n" +
            // STRING members explicitly sized (unsized -> STRING[15] default + WRN_UNSIZED_STRING).
            // Sizes match TelemetryPack's input vars so the ST copy from the MQTT status outputs is exact.
            "    <VarDeclaration Name=\"IsConnected\" Type=\"BOOL\" />\r\n" +
            "    <VarDeclaration Name=\"ReturnCode\" Type=\"USINT\" />\r\n" +
            "    <VarDeclaration Name=\"Status\" Type=\"STRING[255]\" />\r\n" +
            "    <VarDeclaration Name=\"NetworkState\" Type=\"STRING[80]\" />\r\n" +
            "    <VarDeclaration Name=\"SecurityState\" Type=\"STRING[80]\" />\r\n" +
            "    <VarDeclaration Name=\"ProtocolState\" Type=\"STRING[80]\" />\r\n" +
            "  </StructuredType>\r\n" +
            "</DataType>";

        // Deploy the TelemetryHealth datatype (the Telemetry_CAT Health output). Idempotent.
        internal static void DeployTelemetryHealthDatatype(string eaeProjectDir, DeployResult result)
            => DeployDatatype(eaeProjectDir, "TelemetryHealth", TelemetryHealthDt, result);

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
