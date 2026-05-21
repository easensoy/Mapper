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
    /// Rewrites the deployed M580 <c>.hcf</c> channel symlinks after
    /// <see cref="Station2DeviceEmitter"/> has copied the IO-folder
    /// template verbatim into the M580 sysdev folder.
    ///
    /// <para>
    /// Why a second pass (Station2DeviceEmitter already copies the .hcf)?
    /// Two cleanups happen here that the verbatim copy can't do:
    /// </para>
    /// <list type="number">
    ///   <item>The exported .hcf carries the symlink prefix <c>'RES0.M580IO.…'</c>
    ///         because that's the resource name in the EAE project the user
    ///         exported from. Mapper now writes the M580 sysres as
    ///         <c>Name="M580_RES"</c>, so every channel value must be rewritten
    ///         from <c>'RES0.M580IO.…'</c> → <c>'M580_RES.M580IO.…'</c> so EAE's
    ///         IO Mapping table actually finds the M580IO FB.</item>
    ///   <item>If a Station 2 component (Bearing_PnP, Shaft_Hr, etc.) is
    ///         absent from the deployed syslay — operator narrowed the scope,
    ///         Control.xml dropped a component, etc. — its .hcf channel must
    ///         be cleared to <c>''</c>. Otherwise EAE compiles a dangling
    ///         symlink that points at a non-existent FB.</item>
    /// </list>
    ///
    /// <para>M580 architecture specifics this copier handles:</para>
    /// <list type="bullet">
    ///   <item>Root element is <c>HwConfigExportedConfiguration</c>
    ///         (NOT <c>DeviceHwConfigurationItems</c> like M262).</item>
    ///   <item>Namespace is <c>SE.IoX80</c> (X80 backplane bus), reached via
    ///         BMXBUS → BMEXBP0400 rack → BMXCPS2010 PSU → BMED581020 CPU.</item>
    ///   <item>IO modules are <c>BMXDDM16025</c> — MIXED DI/DO modules:
    ///         channels 0..7 are <c>DI00..DI07</c>; channels 8..15 are outputs
    ///         numbered <c>DO16..DO23</c> (EAE offsets the DO numbering by 16).
    ///         Each output has a paired <c>DO16_Status..DO23_Status</c> that
    ///         this copier intentionally leaves at <c>''</c>.</item>
    ///   <item>Slot 2 BMXDDM16025 carries the Bearing PnP zone IO; slot 3
    ///         carries the Shaft PnP zone IO. Both modules are walked.</item>
    ///   <item>Symlink values are human-readable single-quoted strings —
    ///         <c>'RES0.M580IO.SwivelArmAtPick'</c> — NOT the GUID-dotted
    ///         <c>54EB...E786...PusherAtHome</c> form M262 uses.</item>
    /// </list>
    ///
    /// <para>BX1 is intentionally not handled here. Its .hcf has no per-pin
    /// symlinks at all (entire 16-bit input/output words map to a single
    /// VTQWORD symlink each, bit decoding lives inside the BX1_IO SIFB),
    /// so the verbatim copy from <see cref="Station2DeviceEmitter"/> is the
    /// only sensible thing to do — there is nothing per-pin to rewrite.</para>
    /// </summary>
    public static class M580HwConfigCopier
    {
        // M580 sysdev GUID — must match Station2DeviceEmitter.M580SysdevId.
        const string M580SysdevId = "00000000-0000-0000-0000-000000000003";

        // Fixed sysres name Mapper writes (per the user's spec). The deployed
        // .hcf's RES0.* prefix is rewritten to this string so EAE's symlink
        // resolver finds the M580IO FB hosted by this resource.
        const string M580ResourceName = "M580_RES";

        // FB instance name of the IO bridge inside the M580 resource. Every
        // .hcf symlink looks like '{ResourceName}.{IoFbName}.{SignalName}'.
        const string M580IoFbName = "M580IO";

        /// <summary>
        /// Maps each .hcf signal name to the Control.xml component that owns it.
        /// Used to decide whether a pin's symlink survives or gets emptied —
        /// if the owner component isn't in the deployed syslay, the pin is
        /// cleared to <c>''</c>. Encoded from the user's spec:
        ///   slot 2 = Bearing PnP zone (swivel arm + bearing gripper + clamp + bearing sensor)
        ///   slot 3 = Shaft PnP zone   (shaft Hr + shaft Vr + shaft gripper + shaft sensor)
        /// Keys are case-insensitive so an xlsx-driven extension can hand back
        /// raw signal names without normalisation.
        /// </summary>
        static readonly Dictionary<string, string> SignalToComponent =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Slot 2 — Bearing PnP (swivel arm + bearing gripper + bearing-place sensor + clamp)
                ["SwivelArmAtPick"]          = "Bearing_PnP",
                ["SwivelArmAtPlace"]         = "Bearing_PnP",
                ["SwivelArmAtHome"]          = "Bearing_PnP",
                ["Swivel_Arm_Left_Q"]        = "Bearing_PnP",
                ["Swivel_Arm_Right_Q"]       = "Bearing_PnP",
                ["Bearing_Gripper_Open"]     = "Bearing_Gripper",
                ["Bearing_Gripper_Closed"]   = "Bearing_Gripper",
                ["Bearing_Gripper_Q"]        = "Bearing_Gripper",
                ["Bearing_At_Place_Sensor"]  = "BearingSensor",
                ["ClampAtWork"]              = "Clamp",
                ["ClampAtHome"]              = "Clamp",
                ["Clamp"]                    = "Clamp",
                // Slot 3 — Shaft PnP (vertical + horizontal cylinders + gripper + bearing-position sensor)
                ["ShaftPnpVrAtHome"]         = "Shaft_Vr",
                ["ShaftPnpVrAtWork"]         = "Shaft_Vr",
                ["Shaft_Vertical"]           = "Shaft_Vr",
                ["ShaftPnpHrAtHome"]         = "Shaft_Hr",
                ["ShaftPnpHrAtWork"]         = "Shaft_Hr",
                ["Shaft_Horizontal"]         = "Shaft_Hr",
                ["ShaftPnpGripperOpened"]    = "Shaft_Gripper",
                ["ShaftPnpGripperClosed"]    = "Shaft_Gripper",
                ["Shaft_Gripper"]            = "Shaft_Gripper",
                ["ShaftPnpSensor"]           = "ShaftSensor",
            };

        const string EmptyPinValue = "''";

        public static HwConfigCopyResult Copy(MapperConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var result = new HwConfigCopyResult();

            var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(cfg);
            if (string.IsNullOrEmpty(eaeRoot))
            {
                result.Warnings.Add(
                    "Cannot derive EAE project root — M580 .hcf rewrite skipped.");
                return result;
            }

            var hcfPath = ResolveM580HcfPath(eaeRoot);
            if (hcfPath == null || !File.Exists(hcfPath))
            {
                result.Warnings.Add(
                    $"M580 .hcf not found under {eaeRoot}\\IEC61499\\System\\{M580SysdevId}\\ " +
                    "(run Station2DeviceEmitter first so the IO-folder template is copied in).");
                return result;
            }
            result.HcfPath = hcfPath;

            XDocument hcfDoc;
            try
            {
                hcfDoc = XDocument.Load(hcfPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load M580 .hcf '{hcfPath}': {ex.Message}", ex);
            }

            var syslayFbNames = ReadDeployedSyslayFbNames(cfg);
            int rewritten = OverwriteBmxddm16025ParameterValuesInMemory(
                hcfDoc, syslayFbNames, result);

            if (rewritten == 0)
                result.Warnings.Add(
                    "M580 .hcf walked, no ParameterValues touched (every pin's owning " +
                    "component is either already in the syslay with the correct prefix, " +
                    "or no BMXDDM16025 modules were found).");

            WriteHcfWithRetry(hcfDoc, hcfPath, result);
            return result;
        }

        /// <summary>
        /// Walks every BMXDDM16025 module under
        /// <c>HwConfigExportedConfiguration</c>, rewrites each <c>DI##</c> /
        /// <c>DO##</c> channel's <c>Value</c> attribute according to the
        /// signal→component map and the deployed syslay's FB list.
        ///
        /// Three outcomes per pin, in priority order:
        /// <list type="number">
        ///   <item>If current Value is already <c>''</c> (empty) it stays
        ///         empty. Status pins (<c>DO16_Status</c> etc.) live in this
        ///         bucket on a clean export.</item>
        ///   <item>If the signal name is recognised AND its owning component
        ///         is in the syslay, the prefix is rewritten from
        ///         <c>RES0</c> (the export-time resource name) to
        ///         <c>M580_RES</c> (the deployed sysres name).</item>
        ///   <item>If the signal name is unrecognised, or the owning
        ///         component is not in the syslay, the Value is cleared to
        ///         <c>''</c>. EAE then compiles a clean orphan-free .hcf.</item>
        /// </list>
        /// </summary>
        static int OverwriteBmxddm16025ParameterValuesInMemory(
            XDocument doc, HashSet<string> syslayFbNames, HwConfigCopyResult result)
        {
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            int rewritten = 0;

            // Find every <ConfigurationBaseItem> whose <Type><Name> = BMXDDM16025.
            // The XML nests deeply (BMXBUS → BMEXBP0400 → BMXCPS2010 / BMED581020 / BMXDDM16025);
            // Descendants() picks both module instances regardless of which rack/slot
            // they hang from.
            var modules = doc.Descendants(ns + "ConfigurationBaseItem")
                .Where(e =>
                {
                    var typeName = (string?)e.Element(ns + "Type")?
                        .Element(ns + "Name") ?? string.Empty;
                    return string.Equals(typeName, "BMXDDM16025", StringComparison.Ordinal);
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
                    // Skip non-channel parameters (e.g. _Status outputs are
                    // always empty in the exported .hcf; nothing to rewrite).
                    if (pin.EndsWith("_Status", StringComparison.Ordinal)) continue;
                    if (!IsChannelPin(pin)) continue;

                    var valueAttr = pv.Attribute("Value");
                    var current = valueAttr?.Value ?? string.Empty;
                    if (current == EmptyPinValue || current.Length == 0) continue;

                    var signal = ExtractSignalName(current);
                    if (signal == null)
                    {
                        // Value not in the expected '{Res}.{IoFb}.{Signal}' shape — leave alone
                        // (lets manual hand edits survive a Mapper run).
                        result.Warnings.Add(
                            $"M580 .hcf pin {pin} has non-standard Value {current} — left as-is.");
                        continue;
                    }

                    string newValue;
                    if (SignalToComponent.TryGetValue(signal, out var owner)
                        && syslayFbNames.Contains(owner))
                    {
                        newValue = $"'{M580ResourceName}.{M580IoFbName}.{signal}'";
                    }
                    else
                    {
                        newValue = EmptyPinValue;
                        result.Warnings.Add(
                            $"M580 .hcf pin {pin} signal '{signal}' " +
                            (SignalToComponent.ContainsKey(signal)
                                ? $"owner '{owner ?? "?"}' not in syslay → cleared."
                                : "not in M580 signal map → cleared."));
                    }

                    if (string.Equals(current, newValue, StringComparison.Ordinal)) continue;

                    if (valueAttr == null) pv.SetAttributeValue("Value", newValue);
                    else valueAttr.Value = newValue;

                    if (newValue != EmptyPinValue)
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
        /// True for pin names that name an actual channel: <c>DI00</c>..<c>DI07</c>
        /// (slot 0..7 inputs) or <c>DO16</c>..<c>DO23</c> (slot 8..15 outputs,
        /// EAE numbers them +16). Excludes status / setup pins.
        /// </summary>
        static bool IsChannelPin(string pin)
        {
            if (pin.Length < 4) return false;
            if (!(pin[0] == 'D' && (pin[1] == 'I' || pin[1] == 'O'))) return false;
            for (int i = 2; i < pin.Length; i++)
            {
                if (pin[i] < '0' || pin[i] > '9') return false;
            }
            return true;
        }

        /// <summary>
        /// Pulls the trailing signal name from a quoted three-dotted
        /// <c>'A.B.C'</c> value. Returns null on shape mismatch so the
        /// caller can leave the pin untouched.
        /// </summary>
        static string? ExtractSignalName(string quotedValue)
        {
            var s = quotedValue.Trim();
            if (s.Length < 2 || s[0] != '\'' || s[^1] != '\'') return null;
            var inner = s.Substring(1, s.Length - 2);
            var lastDot = inner.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == inner.Length - 1) return null;
            return inner.Substring(lastDot + 1);
        }

        /// <summary>
        /// Resolves the deployed M580 .hcf path under
        /// <c>{eaeRoot}/IEC61499/System/{system-guid}/{m580-sysdev-guid}/{m580-sysdev-guid}.hcf</c>.
        /// Returns null when the M580 sysdev folder isn't there yet (typical
        /// before <see cref="Station2DeviceEmitter"/> runs).
        /// </summary>
        public static string? ResolveM580HcfPath(string eaeRoot)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            foreach (var sysGuidDir in Directory.EnumerateDirectories(systemDir))
            {
                var sysdevFolder = Path.Combine(sysGuidDir, M580SysdevId);
                if (!Directory.Exists(sysdevFolder)) continue;
                var hcf = Path.Combine(sysdevFolder, $"{M580SysdevId}.hcf");
                if (File.Exists(hcf)) return hcf;
            }
            return null;
        }

        /// <summary>
        /// Reads every top-level <c>&lt;FB&gt;</c> Name from the deployed
        /// syslay so the channel-rewrite pass can skip pins owned by
        /// components missing from the canvas. Reused logic mirrored from
        /// M262HwConfigCopier — kept private here to avoid surface-area
        /// bleed across copiers.
        /// </summary>
        static HashSet<string> ReadDeployedSyslayFbNames(MapperConfig cfg)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var syslay = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(syslay) || !File.Exists(syslay)) return names;
            try
            {
                var doc = XDocument.Load(syslay);
                foreach (var fb in doc.Descendants().Where(e => e.Name.LocalName == "FB"))
                {
                    var name = (string?)fb.Attribute("Name");
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            catch { /* defensive — empty set just means every pin gets cleared */ }
            return names;
        }

        /// <summary>
        /// Saves the in-memory document back to the target path with
        /// exponential-backoff retry to survive transient EAE file locks.
        /// Preserves UTF-8 + BOM (Schneider's <c>.hcf</c> exporter writes
        /// the BOM; downstream tooling sometimes assumes it's there).
        /// Mirrors <see cref="M262HwConfigCopier"/> retry behaviour.
        /// </summary>
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
                        FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                    if (attempt > 1)
                        result.Warnings.Add(
                            $"M580 .hcf write succeeded on attempt {attempt} (EAE briefly held a lock).");
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
            // Final attempt — let the exception propagate so the caller sees the lock.
            using (var fs = new FileStream(dstHcf,
                FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var w = System.Xml.XmlWriter.Create(fs, settings))
            {
                doc.Save(w);
            }
        }
    }
}
