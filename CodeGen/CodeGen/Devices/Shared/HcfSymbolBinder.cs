using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Devices.M262;
using CodeGen.Translation;

namespace CodeGen.Devices.Shared
{
    /// <summary>
    /// M580/BX1 equivalent of the M262 <see cref="HcfPatchService"/>, restricted
    /// to <b>symbol binding only</b> (no wiring, no recipes). After Test Runtime
    /// has deployed the Station-2 <c>.hcf</c> files
    /// (<see cref="CodeGen.Devices.M580.M580HwConfigCopier"/> /
    /// <c>BX1HwConfigCopier</c>) and mirrored the Station-2 FBs onto each
    /// resource, this rewrites the <b>deployed</b> channel
    /// <c>&lt;ParameterValue&gt;</c> targets so EAE's "Symbolic Link" view treats
    /// them as resolvable links instead of literal strings.
    ///
    /// <para><b>Why this is approach (B), not (A).</b> The M262 binding
    /// (<see cref="HcfPatchService"/>) is <i>direct</i>: each TM3 channel binds
    /// straight to the consumer FB instance (<c>{resId}.{consumerFbId}.{port}</c>,
    /// unquoted, GUID-based) because the IO-bindings xlsx
    /// (<c>SMC_Rig_IO_Bindings.xlsx</c>) carries a pin→component.port row for
    /// every M262 channel. It does <b>not</b> carry any Station-2 pin rows — the
    /// Actuators sheet lists only Feeder/Checker/Transfer/Rejecter (all M262), and
    /// the only Station-2 sensor rows (BearingSensor/ShaftSensor) have an empty
    /// <c>input_tag</c> ("Hardware tag not yet defined … To be confirmed against
    /// M580 station I/O list"). With no channel→FB.port map, direct binding (A)
    /// is impossible.</para>
    ///
    /// <para>Independently, the reference <c>SMC_Rig_Expo_withClamp</c> shows the
    /// M580 was authored as a <b>broker</b> design, the opposite of M262: every
    /// one of its 24 channels routes through a single <c>M580IO</c> FB of type
    /// <c>PLC_RW_M580</c> (deployed form
    /// <c>{resId}.{M580IO_fbId}.{brokerPort}</c>), and the actuator/sensor CATs
    /// connect to <c>M580IO</c> by event/data wires — not to the TM3 channels.
    /// The Mapper does not (yet) emit that broker FB, so full runtime resolution
    /// still needs it. That is out of scope here (symbol binding only).</para>
    ///
    /// <para><b>What this does (the minimal correct, non-destructive step).</b>
    /// For each bindable channel symlink it (1) strips the wrapping single quotes
    /// so EAE parses it as a symbolic link rather than a string constant, and
    /// (2) re-aligns the leading resource segment to the <b>deployed sysres ID</b>
    /// (the 16-hex GUID), matching the convention proven by both the deployed
    /// M262 <c>.hcf</c> (<c>1459BCD12760907D.{fb}.{port}</c>) and the reference
    /// M580 <c>.hcf</c> (<c>57E0C1B28A6C8371.{fb}.{port}</c>). The authored M580
    /// export instead uses the resource <i>name</i> head <c>'RES0'</c>, which (a)
    /// is quoted and (b) does not match the deployed resource — both reasons the
    /// link is unresolved today. The broker-FB segment (<c>M580IO</c> for M580,
    /// the EtherNet/IP word FB GUID for BX1) and the port segment are preserved
    /// verbatim.</para>
    ///
    /// <para>Genuine literals (empty <c>''</c> spare channels, <c>RSTP_REDUNDANCY</c>,
    /// the BX1 <c>SLAVE_BUS_ID='EtherNetIPDevice_1'</c>, bus ids, <c>T#…</c>
    /// durations, numbers, booleans) are left byte-for-byte untouched. Running
    /// twice is idempotent — the second pass finds every head already aligned and
    /// every value already unquoted and writes nothing.</para>
    ///
    /// <para><b>Does NOT touch the M262 <see cref="HcfPatchService"/>.</b> This is
    /// a separate class operating only on the M580/BX1 deployed sysdev folders.</para>
    /// </summary>
    public static class HcfSymbolBinder
    {
        /// <summary>Bind the deployed M580 X80 <c>.hcf</c> channels. Tag
        /// <c>[HcfBind][M580]</c>.</summary>
        public static void BindM580(MapperConfig? config,
            SystemInjector.BindingApplicationReport report)
            => BindDeployedHcf(config, "M580_dPAC", "SE.DPAC", "M580", report);

        /// <summary>Bind the deployed BX1 soft-dPAC <c>.hcf</c> channels. Tag
        /// <c>[HcfBind][BX1]</c>. The BX1 EtherNet/IP export is already
        /// GUID-headed and unquoted, so this is normally a verification no-op;
        /// it still re-aligns the head to the live sysres ID if a re-export ever
        /// changed it.</summary>
        public static void BindBX1(MapperConfig? config,
            SystemInjector.BindingApplicationReport report)
            => BindDeployedHcf(config, "Soft_dPAC", "SE.DPAC", "BX1", report);

        private static void BindDeployedHcf(MapperConfig? config,
            string deviceType, string deviceNamespace, string tag,
            SystemInjector.BindingApplicationReport report)
        {
            string Log(string m) { var s = $"[HcfBind][{tag}] {m}"; report.Missing.Add(s); return s; }

            if (config == null) { Log("skipped, no MapperConfig available"); return; }

            try
            {
                var eaeRoot = M262SysdevEmitter.DeriveEaeProjectRoot(config);
                if (string.IsNullOrEmpty(eaeRoot)) { Log("skipped, could not derive EAE project root"); return; }

                var sysdevFile = FindSysdevByType(eaeRoot, deviceType, deviceNamespace);
                if (sysdevFile == null)
                {
                    Log($"skipped, no deployed sysdev Type='{deviceType}' Namespace='{deviceNamespace}'");
                    return;
                }

                var sysdevStem = Path.GetFileNameWithoutExtension(sysdevFile);
                var sysdevFolder = Path.Combine(Path.GetDirectoryName(sysdevFile)!, sysdevStem);
                var hcfPath = Path.Combine(sysdevFolder, sysdevStem + ".hcf");
                if (!File.Exists(hcfPath))
                {
                    Log($"skipped, deployed .hcf not found at {hcfPath} (run the HCF copier first)");
                    return;
                }

                var (resId, resName) = ReadSysresIdentity(sysdevFolder);
                if (string.IsNullOrEmpty(resId))
                {
                    Log("skipped, deployed sysres ID not resolvable — cannot align channel resource scope");
                    return;
                }

                Log($"resource id={resId} name={resName ?? "<none>"}  → {hcfPath}");

                XDocument doc;
                try { doc = XDocument.Load(hcfPath); }
                catch (Exception ex) { Log($"skipped, .hcf parse failed: {ex.GetType().Name}: {ex.Message}"); return; }

                int rewritten = 0, alreadyOk = 0, literalsLeft = 0;
                foreach (var pv in doc.Descendants().Where(e => e.Name.LocalName == "ParameterValue"))
                {
                    var nameAttr = (string?)pv.Attribute("Name") ?? string.Empty;
                    var raw = (string?)pv.Attribute("Value");
                    if (raw == null) continue;

                    if (!TryRebindSymlink(raw, resId, out var bound, out var brokerFb, out var port))
                    {
                        literalsLeft++;
                        continue; // empty / literal / non-symlink — leave byte-identical
                    }

                    if (string.Equals(raw, bound, StringComparison.Ordinal))
                    {
                        alreadyOk++;
                        report.HcfPinAssignments.Add((nameAttr, bound));
                        continue;
                    }

                    pv.SetAttributeValue("Value", bound);
                    rewritten++;
                    report.HcfPinAssignments.Add((nameAttr, bound));
                    report.Missing.Add(
                        $"[HcfBind][{tag}] {nameAttr} = {bound}  (was {raw}; unquoted + head→resId, broker '{brokerFb}.{port}')");
                }

                if (rewritten > 0)
                {
                    SaveHcf(doc, hcfPath);
                    Log($"bound {rewritten} channel symlink(s); {alreadyOk} already aligned; {literalsLeft} literal/empty left untouched");
                }
                else
                {
                    Log($"no change — {alreadyOk} channel symlink(s) already well-formed; {literalsLeft} literal/empty left untouched");
                }

                // Honest scope note: the link now resolves to the RESOURCE, but the
                // broker FB it targets is not on the resource yet. Surface that so
                // the user knows what EAE's Symbolic Link view will still flag.
                if (string.Equals(tag, "M580", StringComparison.Ordinal))
                    Log("note: channels now point at the resource via the 'M580IO' broker name; " +
                        "full resolution still needs an M580IO FB (Type PLC_RW_M580) on the M580 resource — " +
                        "broker emission is out of scope for this symbol-binding pass.");
                else
                    Log("note: EtherNet/IP word channels point at the resource; full resolution still needs " +
                        "the BX1 EtherNet/IP word FB (PLC_RW_BX1 / EtherNetIPDevice SIFB) on the BX1 resource.");
            }
            catch (Exception ex)
            {
                Log($"failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Decide whether <paramref name="raw"/> is a bindable 3-segment channel
        /// symlink and, if so, produce its <paramref name="bound"/> form:
        /// quotes stripped and the leading resource segment forced to
        /// <paramref name="resId"/>. Returns false for empty values, genuine
        /// string literals, and anything that is not a <c>head.fb.port</c> triple.
        /// </summary>
        private static bool TryRebindSymlink(string raw, string resId,
            out string bound, out string brokerFb, out string port)
        {
            bound = string.Empty; brokerFb = string.Empty; port = string.Empty;

            var t = raw.Trim();
            if (t.Length == 0) return false;

            // Strip a single layer of wrapping single quotes ('…') — the authored
            // M580 export quotes every symlink. Empty quoted spares ('') collapse
            // to empty and are rejected below.
            bool wasQuoted = t.Length >= 2 && t[0] == '\'' && t[^1] == '\'';
            var inner = wasQuoted ? t.Substring(1, t.Length - 2).Trim() : t;
            if (inner.Length == 0) return false;

            // Must be exactly head.fb.port with three non-empty segments.
            var parts = inner.Split('.');
            if (parts.Length != 3) return false;
            if (parts.Any(p => p.Length == 0)) return false;

            // Reject obvious non-symlink literals that could still contain dots
            // (durations like T#0.5s never split to 3 parts, but guard anyway).
            if (inner.StartsWith("T#", StringComparison.OrdinalIgnoreCase)) return false;

            brokerFb = parts[1];
            port = parts[2];
            bound = $"{resId}.{brokerFb}.{port}";
            return true;
        }

        private static string? FindSysdevByType(string eaeRoot, string deviceType, string deviceNamespace)
        {
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return null;
            foreach (var sd in Directory.EnumerateFiles(systemDir, "*.sysdev", SearchOption.AllDirectories))
            {
                try
                {
                    var root = XDocument.Load(sd).Root;
                    if (root == null || root.Name.LocalName != "Device") continue;
                    if ((string?)root.Attribute("Type") == deviceType &&
                        (string?)root.Attribute("Namespace") == deviceNamespace)
                        return sd;
                }
                catch { /* skip malformed */ }
            }
            return null;
        }

        /// <summary>
        /// Read the deployed resource ID (16-hex GUID, = the .sysres file stem and
        /// its root <c>ID</c> attribute) and Name from the sysdev folder. The .hcf
        /// re-root pass already stamps <c>DeviceHwConfigurationItem/@ResourceId</c>
        /// to this same stem, so the head we write matches what EAE resolves
        /// against.
        /// </summary>
        private static (string Id, string? Name) ReadSysresIdentity(string sysdevFolder)
        {
            try
            {
                var sysres = Directory.Exists(sysdevFolder)
                    ? Directory.EnumerateFiles(sysdevFolder, "*.sysres").FirstOrDefault()
                    : null;
                if (sysres == null) return (string.Empty, null);
                string id = Path.GetFileNameWithoutExtension(sysres);
                string? name = null;
                try
                {
                    var root = XDocument.Load(sysres).Root;
                    var rootId = (string?)root?.Attribute("ID");
                    if (!string.IsNullOrWhiteSpace(rootId)) id = rootId!;   // prefer the in-file ID
                    name = (string?)root?.Attribute("Name");
                }
                catch { /* fall back to file stem */ }
                return (id, name);
            }
            catch { return (string.Empty, null); }
        }

        /// <summary>
        /// Save with UTF-8 + BOM and 2-space indent to match
        /// <see cref="HcfRootRewriter"/> / Schneider's own exporter, retrying if
        /// EAE briefly holds a write lock.
        /// </summary>
        private static void SaveHcf(XDocument doc, string hcfPath)
        {
            var settings = new System.Xml.XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            };
            const int MaxAttempts = 8;
            int delayMs = 50;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(hcfPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    using var w = System.Xml.XmlWriter.Create(fs, settings);
                    doc.Save(w);
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 800);
                }
                catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                {
                    System.Threading.Thread.Sleep(delayMs);
                    delayMs = Math.Min(delayMs * 2, 800);
                }
            }
        }
    }
}
