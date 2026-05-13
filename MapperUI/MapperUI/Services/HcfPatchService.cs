using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace MapperUI.Services
{
    /// <summary>
    /// Patches the deployed M262 <c>.hcf</c> after Button-2 / Generate so EAE
    /// picks up the symbolic-link bindings on reload.
    ///
    /// Reads the EXISTING .hcf on disk (NOT the baseline — that's the Deploy
    /// Universal Architecture flow's job via <see cref="M262HwConfigCopier.Copy"/>),
    /// resolves <c>resourceId</c> + <c>m262IoFbId</c> from the deployed M262
    /// sysres, rewrites the TM3 module ParameterValues against the in-scope
    /// syslay FB names + IO bindings, and saves.
    ///
    /// Lives in MapperUI.Services because it cross-references M262HcfDocument /
    /// M262HwConfigCopier / M262SysdevEmitter, none of which CodeGen.dll can see.
    /// </summary>
    public static class HcfPatchService
    {
        /// <summary>
        /// Run the patch. Each pin actually rewritten is appended to
        /// <paramref name="report"/>.<see cref="SystemInjector.BindingApplicationReport.HcfPinAssignments"/>;
        /// every skip reason / warning is appended to
        /// <see cref="SystemInjector.BindingApplicationReport.Missing"/>.
        /// </summary>
        /// <summary>
        /// Convenience overload — reads in-scope FB names from the just-emitted
        /// syslay file (every &lt;FB Name="..."/&gt; inside &lt;SubAppNetwork&gt;).
        /// Suitable for MainForm.btnTestStation1_Click which already knows the
        /// syslay path it asked the injector to write.
        /// </summary>
        public static void PatchDeployed(MapperConfig? config,
            string syslayPath, IoBindings? bindings,
            SystemInjector.BindingApplicationReport report)
        {
            var syslayFbNames = ReadSyslayFbNames(syslayPath);
            PatchDeployed(config, syslayFbNames, bindings, report);
        }

        /// <summary>Core overload — caller supplies the in-scope FB-name set directly.</summary>
        public static void PatchDeployed(MapperConfig? config,
            HashSet<string> syslayFbNames,
            IoBindings? bindings,
            SystemInjector.BindingApplicationReport report)
        {
            if (bindings == null || bindings.PinAssignments.Count == 0)
            {
                report.Missing.Add("[Hcf] skipped, no bindings");
                return;
            }
            if (config == null)
            {
                report.Missing.Add("[Hcf] skipped, no MapperConfig available");
                return;
            }

            try
            {
                var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(config);
                if (eaeRoot == null)
                {
                    report.Missing.Add("[Hcf] skipped, could not derive EAE project root");
                    return;
                }
                var hcfPath = M262HwConfigCopier.ResolveTargetHcfPath(eaeRoot);
                if (hcfPath == null || !File.Exists(hcfPath))
                {
                    report.Missing.Add("[Hcf] skipped, deployed .hcf not found under sysdev");
                    return;
                }

                // Resolve resourceId + m262IoFbId from the deployed M262 sysres —
                // not from hardcoded constants. The sysres root carries the
                // Resource GUID; the M262IO PLC_RW_M262 FB inside the FBNetwork
                // carries the FB GUID.
                var (resourceId, m262IoFbId) = ReadSysresIds(eaeRoot);
                if (string.IsNullOrEmpty(resourceId) || string.IsNullOrEmpty(m262IoFbId))
                {
                    report.Missing.Add("[Hcf] skipped, sysres / M262IO FB IDs not resolvable");
                    return;
                }

                var hcf = M262HcfDocument.Load(hcfPath);
                hcf.OverwriteHcfParameterValuesInMemory(bindings, resourceId, m262IoFbId, syslayFbNames);
                hcf.WriteHcfToDisk(hcfPath);

                foreach (var (pin, value) in hcf.EnumerateOverwrittenPins())
                    report.HcfPinAssignments.Add((pin, value));
                foreach (var w in hcf.LastResult.Warnings)
                    report.Missing.Add($"[Hcf][Warn] {w}");
            }
            catch (Exception ex)
            {
                report.Missing.Add($"[Hcf] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Reads &lt;FB Name="..." /&gt; values from a syslay file's
        /// &lt;SubAppNetwork&gt; root. Returns an empty set if the file can't
        /// be parsed.</summary>
        private static HashSet<string> ReadSyslayFbNames(string syslayPath)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (string.IsNullOrEmpty(syslayPath) || !File.Exists(syslayPath)) return names;
                var doc = XDocument.Load(syslayPath);
                foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
                {
                    var name = (string?)fb.Attribute("Name");
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            catch { /* best-effort */ }
            return names;
        }

        /// <summary>
        /// Reads the M262 sysres root's <c>ID</c> attribute (resource GUID) and
        /// the <c>M262IO</c> FB's <c>ID</c> attribute. Returns blanks if anything
        /// is missing — caller treats blanks as a skip signal.
        /// </summary>
        private static (string resourceId, string m262IoFbId) ReadSysresIds(string eaeRoot)
        {
            try
            {
                var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
                if (!Directory.Exists(systemDir)) return (string.Empty, string.Empty);

                var sysresPath = Directory
                    .EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (sysresPath == null) return (string.Empty, string.Empty);

                var doc = XDocument.Load(sysresPath);
                var root = doc.Root;
                if (root == null) return (string.Empty, string.Empty);

                var resourceId = (string?)root.Attribute("ID") ?? string.Empty;
                var m262Io = root.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "FB" &&
                                         (string?)e.Attribute("Name") == "M262IO" &&
                                         (string?)e.Attribute("Type") == "PLC_RW_M262");
                var m262IoFbId = (string?)m262Io?.Attribute("ID") ?? string.Empty;
                return (resourceId, m262IoFbId);
            }
            catch { return (string.Empty, string.Empty); }
        }
    }
}
