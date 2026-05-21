using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Translation;

namespace MapperUI.Services
{
    public static class M262HwConfigCopier
    {
        public static HwConfigCopyResult Copy(MapperConfig cfg) => Copy(cfg, bindingsOverride: null);

        public static HwConfigCopyResult Copy(MapperConfig cfg, IoBindings? bindingsOverride)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            // NEW PATH (2026-05-21): if cfg.M262HcfTemplatePath points at the
            // IO-folder export (the fresh HwConfigExportedConfiguration-format
            // .hcf with 'RES0.M262IO.…' quoted symbols), use it directly —
            // same mechanism Station2DeviceEmitter + M580HwConfigCopier use
            // for the other two PLCs. Avoids the legacy GUID-dotted-baseline
            // path entirely, which was emitting a 169-byte
            // <DeviceHwConfigurationItems /> shell after the baseline folder
            // disappeared, breaking the EAE Hardware Configuration view.
            if (!string.IsNullOrWhiteSpace(cfg.M262HcfTemplatePath) &&
                File.Exists(cfg.M262HcfTemplatePath))
                return CopyFromIoTemplate(cfg);

            var result = new HwConfigCopyResult();
            var baseline = cfg.M262HardwareConfigBaselinePath;
            if (string.IsNullOrWhiteSpace(baseline))
            {
                result.Warnings.Add(
                    "MapperConfig.M262HcfTemplatePath AND M262HardwareConfigBaselinePath " +
                    "both empty — skipping hcf copy. Set IoFolderPath + M262HcfTemplatePath " +
                    "in mapper_config.json to point at C:\\VueOneMapper\\IO\\M262IO.hcf.");
                return result;
            }
            if (!Directory.Exists(baseline))
                throw new DirectoryNotFoundException($"M262 baseline folder not found: {baseline}");

            var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg)
                ?? throw new InvalidOperationException(
                    "Cannot derive EAE project root from MapperConfig.SyslayPath/SyslayPath2.");

            var srcHwDir = Path.Combine(baseline, "HwConfiguration");
            if (Directory.Exists(srcHwDir))
            {
                var dstHwDir = Path.Combine(eaeRoot, "HwConfiguration");
                CopyDirRecursive(srcHwDir, dstHwDir, result);
            }
            else
            {
                result.Warnings.Add($"Baseline HwConfiguration/ folder missing under {baseline}");
            }

            var srcHcf = FindBaselineHcf(baseline)
                ?? throw new FileNotFoundException(
                    $"No .hcf found under {baseline}\\IEC61499\\System\\");
            var dstHcf = ResolveTargetHcfPath(eaeRoot)
                ?? throw new InvalidOperationException(
                    "Cannot resolve target .hcf path — no .sysdev under target IEC61499/System tree.");

            Directory.CreateDirectory(Path.GetDirectoryName(dstHcf)!);
            result.HcfPath = dstHcf;

            XDocument hcfDoc;
            try
            {
                hcfDoc = XDocument.Load(srcHcf);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load baseline .hcf '{srcHcf}': {ex.Message}", ex);
            }

            var sysresId = ReadTargetSysresId(eaeRoot);
            if (!string.IsNullOrEmpty(sysresId))
            {
                if (PatchHcfResourceIdInMemory(hcfDoc, sysresId))
                    result.Warnings.Add($"Patched .hcf ResourceId to {sysresId} (was baseline's value)");
            }
            else
            {
                result.Warnings.Add(
                    "Could not read target .sysres ID — .hcf left with baseline ResourceId. " +
                    "EAE IO Mapping table will be empty until ResourceId matches the resource.");
            }

            var bindings = bindingsOverride ?? LoadBindings(cfg);
            int paramsWritten = 0;
            if (bindings != null)
            {
                var syslayFbNames = ReadSyslayFbNames(cfg);
                paramsWritten = OverwriteHcfParameterValuesInMemory(
                    hcfDoc, bindings, syslayFbNames, result,
                    resourceId: string.IsNullOrEmpty(sysresId) ? "00000000-0000-0000-0000-000000000000" : sysresId);
                if (paramsWritten == 0 && bindings.PinAssignments.Count == 0)
                    result.Warnings.Add(
                        "IO bindings xlsx has no pin_di_athome / pin_di_atwork / " +
                        "pin_do_outputToWork columns — add them to drive .hcf channel " +
                        "symlinks. .hcf left at baseline values.");
            }
            else
            {
                result.Warnings.Add(
                    "IO bindings xlsx not found — hcf channel symlinks left as baseline.");
            }

            WriteHcfWithRetry(hcfDoc, dstHcf, result);

            return result;
        }

        /// <summary>
        /// New unified IO-template path. Reads the user's exported
        /// <c>M262IO.hcf</c> from the IO folder (the same source
        /// <see cref="Station2DeviceEmitter"/> uses for M580/BX1), rewrites
        /// the export-time <c>'RES0.M262IO.…'</c> prefix to the deployed
        /// sysres name (<c>cfg.ResourceName</c>, typically <c>M262_RES</c>),
        /// clears symlinks whose owning component is absent from the syslay,
        /// and writes the result to the M262 sysdev's <c>.hcf</c> with the
        /// same retry semantics as the legacy baseline path.
        ///
        /// All three PLCs (M262, M580, BX1) now follow this same pattern:
        /// verbatim-copy from IO folder, then a post-copy rewrite pass per
        /// PLC namespace + module conventions.
        /// </summary>
        static HwConfigCopyResult CopyFromIoTemplate(MapperConfig cfg)
        {
            var result = new HwConfigCopyResult();
            var srcHcf = cfg.M262HcfTemplatePath;

            var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg)
                ?? throw new InvalidOperationException(
                    "Cannot derive EAE project root from MapperConfig.SyslayPath/SyslayPath2.");

            var dstHcf = ResolveTargetHcfPath(eaeRoot)
                ?? throw new InvalidOperationException(
                    "Cannot resolve target M262 .hcf path — no .sysdev under target IEC61499/System tree.");
            Directory.CreateDirectory(Path.GetDirectoryName(dstHcf)!);
            result.HcfPath = dstHcf;

            XDocument hcfDoc;
            try
            {
                hcfDoc = XDocument.Load(srcHcf);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load M262 .hcf template '{srcHcf}': {ex.Message}", ex);
            }

            var resourceName = string.IsNullOrWhiteSpace(cfg.ResourceName)
                ? "M262_RES" : cfg.ResourceName;
            var syslayFbNames = ReadSyslayFbNames(cfg);

            int rewritten = RewriteM262TemplateInMemory(hcfDoc, resourceName, syslayFbNames, result);
            if (rewritten == 0)
                result.Warnings.Add(
                    "M262 .hcf walked, no ParameterValues touched. Either the template " +
                    "already matched the deployed sysres name, no TM3 modules were found, " +
                    "or every signal's owning component is absent from the syslay.");

            WriteHcfWithRetry(hcfDoc, dstHcf, result);
            return result;
        }

        /// <summary>
        /// Walks every <c>TM3DI16_G</c> + <c>TM3DQ16T_G</c> module under the
        /// <c>HwConfigExportedConfiguration</c> root, rewriting each
        /// <c>DI##</c> / <c>DO##</c> channel's <c>Value</c> attribute:
        /// <list type="number">
        ///   <item>Pin already <c>''</c> empty → leave alone.</item>
        ///   <item>Signal in <see cref="M262SignalToComponent"/> AND owning
        ///         component is in the syslay → rewrite quoted prefix from
        ///         <c>RES0</c> to <paramref name="resourceName"/>.</item>
        ///   <item>Otherwise → clear to <c>''</c>.</item>
        /// </list>
        /// Same shape M580HwConfigCopier uses for BMXDDM16025 modules —
        /// only the module-name set + signal-to-component map differ.
        /// </summary>
        static int RewriteM262TemplateInMemory(XDocument doc, string resourceName,
            HashSet<string> syslayFbNames, HwConfigCopyResult result)
        {
            const string EmptyPin = "''";
            const string IoFbName = "M262IO";
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var ioModules = new HashSet<string>(StringComparer.Ordinal)
            {
                "TM3DI16_G", "TM3DQ16T_G",
            };
            int rewritten = 0;

            var modules = doc.Descendants(ns + "ConfigurationBaseItem")
                .Where(e =>
                {
                    var typeName = (string?)e.Element(ns + "Type")?
                        .Element(ns + "Name") ?? string.Empty;
                    return ioModules.Contains(typeName);
                })
                .ToList();

            foreach (var module in modules)
            {
                var pvContainer = module.Element(ns + "ParameterValues");
                if (pvContainer == null) continue;

                foreach (var pv in pvContainer.Elements(ns + "ParameterValue"))
                {
                    var pin = (string?)pv.Attribute("Name") ?? string.Empty;
                    if (string.IsNullOrEmpty(pin)) continue;
                    if (pin.Length < 4) continue;
                    if (!(pin[0] == 'D' && (pin[1] == 'I' || pin[1] == 'O'))) continue;
                    // Skip non-numeric tails (e.g. status/setup pins) — same guard M580 uses.
                    bool allDigits = true;
                    for (int i = 2; i < pin.Length; i++)
                        if (pin[i] < '0' || pin[i] > '9') { allDigits = false; break; }
                    if (!allDigits) continue;

                    var valueAttr = pv.Attribute("Value");
                    var current = valueAttr?.Value ?? string.Empty;
                    if (current == EmptyPin || current.Length == 0) continue;

                    // Pull the trailing signal name from 'A.B.Signal'.
                    string? signal = null;
                    var trimmed = current.Trim();
                    if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
                    {
                        var inner = trimmed.Substring(1, trimmed.Length - 2);
                        var lastDot = inner.LastIndexOf('.');
                        if (lastDot > 0 && lastDot < inner.Length - 1)
                            signal = inner.Substring(lastDot + 1);
                    }
                    if (signal == null)
                    {
                        result.Warnings.Add(
                            $"M262 .hcf pin {pin} has non-standard Value {current} — left as-is.");
                        continue;
                    }

                    string newValue;
                    if (M262SignalToComponent.TryGetValue(signal, out var owner)
                        && syslayFbNames.Contains(owner))
                    {
                        newValue = $"'{resourceName}.{IoFbName}.{signal}'";
                    }
                    else
                    {
                        newValue = EmptyPin;
                        result.Warnings.Add(
                            $"M262 .hcf pin {pin} signal '{signal}' " +
                            (M262SignalToComponent.ContainsKey(signal)
                                ? $"owner '{owner ?? "?"}' not in syslay → cleared."
                                : "not in M262 signal map → cleared."));
                    }
                    if (string.Equals(current, newValue, StringComparison.Ordinal)) continue;

                    if (valueAttr == null) pv.SetAttributeValue("Value", newValue);
                    else valueAttr.Value = newValue;

                    if (newValue != EmptyPin)
                    {
                        result.ParametersOverwrittenSet.Add(pin);
                        result.ParametersOverwritten.Add($"{pin}={newValue}");
                    }
                    rewritten++;
                }
            }
            return rewritten;
        }

        /// <summary>
        /// Signal name → owning component for the M262 IO-folder template.
        /// Mirrors the reverse direction of <see cref="M262IoVariableMap"/>
        /// so the rewriter can filter symlinks against the syslay's FB list
        /// the same way <see cref="M580HwConfigCopier"/> does for the
        /// BMXDDM16025 modules. Pusher = the rig-canonical hardware name
        /// for VueOne's "Feeder" (per InstanceNameResolver.RigAliases).
        /// </summary>
        static readonly Dictionary<string, string> M262SignalToComponent =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["PusherAtHome"]   = "Pusher",
                ["PusherAtWork"]   = "Pusher",
                ["ExtendPusher"]   = "Pusher",
                ["CheckerUp"]      = "Checker",
                ["CheckerDown"]    = "Checker",
                ["ExtendChecker"]  = "Checker",
                ["TransferAtHome"] = "Transfer",
                ["TransferAtWork"] = "Transfer",
                ["ExtendTransfer"] = "Transfer",
                ["ExtendRejector"] = "Ejector",
                ["Hopper"]         = "PartInHopper",
                ["PartAtChecker"]  = "PartAtChecker",
                ["PartAtAssembly"] = "PartAtAssembly",
                ["PartAtExit"]     = "PartAtExit",
                ["RobotStatus_Task_Complete"] = "Robot",
                ["RobotCommands_StartTask"]   = "Robot",
            };

        static void WriteHcfWithRetry(XDocument doc, string dstHcf, HwConfigCopyResult result)
        {
            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            };

            const int MaxAttempts = 8;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(dstHcf,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                    if (attempt > 1)
                        result.Warnings.Add(
                            $".hcf write succeeded on attempt {attempt} (EAE briefly held a lock).");
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs *= 2;
                }
                catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs *= 2;
                }
            }

            using (var fs = new FileStream(dstHcf,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read))
            using (var w = System.Xml.XmlWriter.Create(fs, settings))
            {
                doc.Save(w);
            }
        }

        static bool PatchHcfResourceIdInMemory(XDocument doc, string newResourceId)
        {
            if (string.IsNullOrWhiteSpace(newResourceId)) return false;
            var item = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "DeviceHwConfigurationItem");
            if (item == null) return false;
            var attr = item.Attribute("ResourceId");
            if (attr != null && string.Equals(attr.Value, newResourceId, StringComparison.Ordinal)) return false;
            item.SetAttributeValue("ResourceId", newResourceId);
            return true;
        }

        static readonly Dictionary<(string Component, string Port), string> M262IoVariableMap =
            new(new ComponentPortComparer())
            {
                [("Feeder",        "athome")]       = "PusherAtHome",
                [("Feeder",        "atwork")]       = "PusherAtWork",
                [("Feeder",        "OutputToWork")] = "ExtendPusher",
                [("Checker",       "athome")]       = "CheckerUp",
                [("Checker",       "atwork")]       = "CheckerDown",
                [("Checker",       "OutputToWork")] = "ExtendChecker",
                [("Transfer",      "athome")]       = "TransferAtHome",
                [("Transfer",      "atwork")]       = "TransferAtWork",
                [("Transfer",      "OutputToWork")] = "ExtendTransfer",
                [("Rejector",      "OutputToWork")] = "ExtendRejector",
                [("Rejecter",      "OutputToWork")] = "ExtendRejector",
                [("PartInHopper",  "Input")]        = "Hopper",
                [("PartAtChecker", "Input")]        = "PartAtChecker",
                [("PartAtAssembly","Input")]        = "PartAtAssembly",
                [("PartAtExit",    "Input")]        = "PartAtExit",
            };

        sealed class ComponentPortComparer : IEqualityComparer<(string Component, string Port)>
        {
            public bool Equals((string Component, string Port) a, (string Component, string Port) b) =>
                StringComparer.OrdinalIgnoreCase.Equals(a.Component, b.Component) &&
                StringComparer.OrdinalIgnoreCase.Equals(a.Port,      b.Port);
            public int GetHashCode((string Component, string Port) obj) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Component),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Port));
        }

        // Internal overload — uses M262SysdevEmitter.M262IoFbId as the default FB ID.
        // Existing baseline-replay Copy() workflow keeps this convenience.
        static int OverwriteHcfParameterValuesInMemory(XDocument doc, IoBindings bindings,
            HashSet<string> syslayFbNames, HwConfigCopyResult result, string resourceId)
            => OverwriteHcfParameterValuesInMemory(doc, bindings, syslayFbNames, result,
                resourceId, M262SysdevEmitter.M262IoFbId);

        // Caller-supplies the m262IoFbId. Used by M262HcfDocument so the GUID can
        // come from the syslay's M262IO instance rather than a hardcoded constant.
        internal static int OverwriteHcfParameterValuesInMemory(XDocument doc, IoBindings bindings,
            HashSet<string> syslayFbNames, HwConfigCopyResult result, string resourceId,
            string m262IoFbId)
        {
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            int written = 0;
            var ioModuleNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "TM3DI16_G", "TM3DQ16T_G"
            };

            const string EmptyPinValue = "";

            foreach (var module in doc.Descendants().Where(e =>
                ioModuleNames.Contains((string?)e.Element(ns + "Name")?.Value ?? string.Empty)))
            {
                var pvContainer = module.Element(ns + "ParameterValues");
                if (pvContainer == null) continue;

                foreach (var pv in pvContainer.Elements(ns + "ParameterValue"))
                {
                    var pin = (string?)pv.Attribute("Name");
                    if (string.IsNullOrEmpty(pin)) continue;

                    string targetValue = EmptyPinValue;
                    if (bindings.PinAssignments.TryGetValue(pin, out var pa))
                    {
                        if (syslayFbNames.Contains(pa.ComponentName) &&
                            M262IoVariableMap.TryGetValue((pa.ComponentName, pa.Port), out var varName))
                        {
                            targetValue = $"{resourceId}.{m262IoFbId}.{varName}";
                        }
                    }

                    var valueAttr = pv.Attribute("Value");
                    var current = valueAttr?.Value ?? string.Empty;
                    if (string.Equals(current, targetValue, StringComparison.Ordinal)) continue;

                    if (valueAttr == null) pv.SetAttributeValue("Value", targetValue);
                    else valueAttr.Value = targetValue;

                    if (targetValue.Length > 0)
                    {
                        result.ParametersOverwrittenSet.Add(pin);
                        result.ParametersOverwritten.Add($"{pin}={targetValue}");
                    }
                    written++;
                }
            }
            return written;
        }

        public static string? FindBaselineHcf(string baselineRoot)
        {
            var systemDir = Path.Combine(baselineRoot, "IEC61499", "System");
            if (Directory.Exists(systemDir))
            {
                var hit = Directory.EnumerateFiles(systemDir, "*.hcf", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
            return Directory.EnumerateFiles(baselineRoot, "*.hcf", SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        public static string? ResolveTargetHcfPath(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            var sysdev = Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (sysdev == null) return null;
            var sysdevDir = Path.GetDirectoryName(sysdev)!;
            var stem = Path.GetFileNameWithoutExtension(sysdev);
            return Path.Combine(sysdevDir, stem, stem + ".hcf");
        }

        public static string ReadTargetSysresId(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return string.Empty;
            var sysres = Directory.EnumerateFiles(systemDir, "*.sysres", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (sysres == null) return string.Empty;
            try
            {
                var doc = XDocument.Load(sysres);
                return (string?)doc.Root?.Attribute("ID") ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        public static int PatchHcfResourceId(string hcfPath, string newResourceId)
        {
            if (!File.Exists(hcfPath) || string.IsNullOrWhiteSpace(newResourceId)) return 0;
            var doc = XDocument.Load(hcfPath);
            var item = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "DeviceHwConfigurationItem");
            if (item == null) return 0;
            var attr = item.Attribute("ResourceId");
            if (attr != null && string.Equals(attr.Value, newResourceId, StringComparison.Ordinal)) return 0;
            item.SetAttributeValue("ResourceId", newResourceId);
            doc.Save(hcfPath);
            return 1;
        }

        static IoBindings? LoadBindings(MapperConfig cfg)
        {
            var path = cfg.IoBindingsPath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppContext.BaseDirectory, path);
            if (!File.Exists(path)) return null;
            return IoBindingsLoader.LoadBindings(path);
        }

        static int OverwriteHcfParameterValues(string hcfPath, IoBindings bindings,
            HashSet<string> syslayFbNames, HwConfigCopyResult result)
        {
            var doc = XDocument.Load(hcfPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            int written = 0;

            var ioModuleNames = new HashSet<string>(StringComparer.Ordinal)
            {
                "TM3DI16_G", "TM3DQ16T_G"
            };

            const string EmptyPinValue = "''";

            foreach (var module in doc.Descendants().Where(e =>
                ioModuleNames.Contains((string?)e.Element(ns + "Name")?.Value ?? string.Empty)))
            {
                var pvContainer = module.Element(ns + "ParameterValues");
                if (pvContainer == null) continue;

                foreach (var pv in pvContainer.Elements(ns + "ParameterValue"))
                {
                    var pin = (string?)pv.Attribute("Name");
                    if (string.IsNullOrEmpty(pin)) continue;

                    string targetValue = EmptyPinValue;
                    var symbol = bindings.ResolveSymbol(pin);
                    if (symbol != null)
                    {
                        bindings.PinAssignments.TryGetValue(pin, out var pa);
                        var owner = pa?.ComponentName ?? string.Empty;
                        if (syslayFbNames.Contains(owner)) targetValue = symbol;
                    }

                    var valueAttr = pv.Attribute("Value");
                    var current = valueAttr?.Value ?? string.Empty;
                    if (string.Equals(current, targetValue, StringComparison.Ordinal)) continue;

                    if (valueAttr == null)
                        pv.SetAttributeValue("Value", targetValue);
                    else
                        valueAttr.Value = targetValue;

                    if (targetValue != EmptyPinValue)
                    {
                        result.ParametersOverwrittenSet.Add(pin);
                        result.ParametersOverwritten.Add($"{pin}={targetValue}");
                    }
                    written++;
                }
            }

            if (written > 0) doc.Save(hcfPath);
            return written;
        }

        static HashSet<string> ReadSyslayFbNames(MapperConfig cfg)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var path = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return set;
            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) return set;
                XNamespace ns = root.GetDefaultNamespace();
                var net = root.Element(ns + "SubAppNetwork") ?? root.Element(ns + "FBNetwork");
                if (net == null) return set;
                foreach (var fb in net.Elements(ns + "FB"))
                {
                    var name = (string?)fb.Attribute("Name");
                    if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
                }
            }
            catch { }
            return set;
        }

        static void CopyDirRecursive(string src, string dst, HwConfigCopyResult result)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, f);
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!TryCopyWithRetry(f, target))
                    result.Warnings.Add($"Could not copy '{rel}' (file locked) — skipped.");
                else
                    result.FilesCopied++;
            }
        }

        static bool TryCopyWithRetry(string src, string dst)
        {
            const int MaxAttempts = 5;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try { File.Copy(src, dst, overwrite: true); return true; }
                catch (IOException) when (attempt < MaxAttempts)
                { System.Threading.Thread.Sleep(delayMs); delayMs *= 2; }
                catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                { System.Threading.Thread.Sleep(delayMs); delayMs *= 2; }
                catch { return false; }
            }
            return false;
        }
    }

    public class HwConfigCopyResult
    {
        public string? HcfPath { get; set; }
        public int FilesCopied { get; set; }
        public List<string> ParametersOverwritten { get; } = new();
        public HashSet<string> ParametersOverwrittenSet { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = new();
    }
}
