using System;
using System.IO;

namespace CodeGen.Devices.Core
{
    /// <summary>
    /// Owns the APPLICATION lifecycle the same way the Mapper now owns the DEVICE
    /// lifecycle (DemonstratorWiper.DeleteLogicalDevices + M262SysdevEmitter bootstrap):
    /// Clean DELETES the application (<c>.sysapp</c> + its content folder) so EAE shows
    /// nothing under "Applications" — exactly like Devices — and this emitter RECREATES
    /// it (create-if-absent) on the next Generate so the project is whole again.
    ///
    /// Why a bootstrap is required: Generate's first step
    /// (<c>SystemLayoutInjector.PrepareDemonstratorForGeneration</c>) THROWS if
    /// <c>SyslayPath2</c> (the application's <c>000000.syslay</c>) is missing, and
    /// <c>M262SysdevEmitter.AlignApplicationName</c> only RENAMES an existing
    /// <c>.sysapp</c> — it never creates one. So deleting the application on Clean needs
    /// this shell restored BEFORE Prepare runs.
    ///
    /// Behaviour-preserving by construction:
    ///   • <see cref="EnsureApplicationShell"/> is create-IF-ABSENT — when the
    ///     <c>.sysapp</c> already exists (the normal case, every gate run) it is a strict
    ///     no-op, so an existing tree is byte-identical to before this code existed.
    ///   • The recreated <c>000000.syslay</c> is a throwaway placeholder —
    ///     <c>GenerateStation1TestSyslay</c> overwrites it with the real layout (and the
    ///     layer ID is deterministic via <c>FBIdGenerator.GenerateFBId(fileName)</c>, so
    ///     the regenerated layer matches the always-present path exactly).
    ///   • The <c>.sysapp</c> is written name-blank; <c>AlignApplicationName</c> sets it
    ///     to WMG during the same Generate — identical to the Clean+Generate flow today.
    /// The one EAE-managed file NOT recreated is the layer-ID-named sub-app file
    /// (<c>&lt;layerId&gt;.syslay</c>): EAE writes that on its own save, and recreates it
    /// from <c>000000.syslay</c> on the next Reload — so its absence is correct, not a gap.
    /// </summary>
    public static class ApplicationShellEmitter
    {
        // Mapper conventions (the same zero/one UUIDs the project ships with).
        public const string SystemId = "00000000-0000-0000-0000-000000000000";
        public const string AppId    = "00000000-0000-0000-0000-000000000001";

        // Tiny static skeletons. The .sysapp is written name-blank; the later
        // M262SysdevEmitter.AlignApplicationName pass (loads with PreserveWhitespace,
        // re-saves) sets the WMG name — exactly the path a normal Clean+Generate already
        // takes. (The recreated .sysapp can differ from the always-present file by a few
        // bytes of XML whitespace; that is EAE-immaterial and never touches the layout,
        // sysres, hcf, recipes or MQTT the rig deploys — proven by the gate diff.)
        const string SysappXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<Application xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns=\"https://www.se.com/LibraryElements\" Name=\"\" " +
            $"ID=\"{AppId}\" />";

        // Throwaway placeholder so PrepareDemonstratorForGeneration's File.Exists check
        // passes; GenerateStation1TestSyslay overwrites this with the real layout.
        const string EmptySyslayXml =
            "﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<Layer ID=\"2240693B1370B496\" Name=\"\" Comment=\"\" IsDefault=\"true\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns=\"https://www.se.com/LibraryElements\">\r\n  <SubAppNetwork />\r\n</Layer>";

        const string AspmapXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<ASPMapping xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" />";

        static string OpcuaXml(string uid) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<OPCUAComplexObject xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
            $"  <OPCUAComplexObject UID=\"{uid}\" />\r\n</OPCUAComplexObject>";

        /// <summary>
        /// Recreates the application shell (<c>.sysapp</c> + content folder + the layer
        /// placeholder + the aspmap/opcua companions) and re-registers it in the
        /// <c>.dfbproj</c> — ONLY when the <c>.sysapp</c> is absent. Returns true if it
        /// bootstrapped, false if the application was already present (no-op). Runs at the
        /// very start of Generate, before PrepareDemonstratorForGeneration needs the layout.
        /// </summary>
        public static bool EnsureApplicationShell(string? eaeRoot, Action<string>? log = null)
        {
            if (string.IsNullOrEmpty(eaeRoot)) return false;
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return false;

            var containerDir = Path.Combine(systemDir, SystemId);          // System/000000/
            var sysappPath   = Path.Combine(containerDir, AppId + ".sysapp");
            if (File.Exists(sysappPath)) return false;                     // present -> strict no-op

            var appDir       = Path.Combine(containerDir, AppId);          // System/000000/000001/
            var companionDir = Path.Combine(appDir, SystemId);            // System/000000/000001/000000/
            try
            {
                Directory.CreateDirectory(companionDir);
                File.WriteAllText(sysappPath, SysappXml);

                var syslayPath = Path.Combine(appDir, SystemId + ".syslay"); // == SyslayPath2
                if (!File.Exists(syslayPath)) File.WriteAllText(syslayPath, EmptySyslayXml);

                File.WriteAllText(Path.Combine(companionDir, "aspmap.xml"), AspmapXml);
                File.WriteAllText(Path.Combine(companionDir, "opcua.xml"),  OpcuaXml(AppId));
                File.WriteAllText(Path.Combine(appDir, "opcua.xml"),        OpcuaXml(SystemId));

                var dfbproj = Path.Combine(eaeRoot, "IEC61499", "IEC61499.dfbproj");
                int reg = DfbprojRegistrar.RegisterApplicationShell(dfbproj);
                log?.Invoke($"[AppShell] Bootstrapped application shell ({AppId}.sysapp + layer + " +
                            $"aspmap/opcua companions); {reg} dfbproj entr(y/ies) ensured.");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[AppShell][Warn] could not bootstrap application shell: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes the application — the <c>.sysapp</c> file plus its content folder
        /// (every layer file + the aspmap/opcua companions). Mirrors
        /// <c>DemonstratorWiper.DeleteLogicalDevices</c> for devices. The parent
        /// <c>System/000000/</c> container, the <c>.system</c> root and the dfbproj shell
        /// stay; the now-missing dfbproj entries are pruned by the wiper's StripDfbproj,
        /// and <see cref="EnsureApplicationShell"/> rebuilds the lot on the next Generate.
        /// Returns the number of top-level items removed. Best-effort per item (a file EAE
        /// holds locked is logged, not fatal — close EAE before Clean, as with devices).
        /// </summary>
        public static int DeleteApplicationShell(string iecDir, Action<string>? log = null)
        {
            var systemDir = Path.Combine(iecDir, "System");
            if (!Directory.Exists(systemDir)) return 0;
            var containerDir = Path.Combine(systemDir, SystemId);
            if (!Directory.Exists(containerDir)) return 0;

            int removed = 0;
            var sysappPath = Path.Combine(containerDir, AppId + ".sysapp");
            if (File.Exists(sysappPath))
            {
                try { File.Delete(sysappPath); removed++; }
                catch (Exception ex) { log?.Invoke($"[AppShell][Warn] could not delete {Path.GetFileName(sysappPath)}: {ex.Message}"); }
            }
            var appDir = Path.Combine(containerDir, AppId);
            if (Directory.Exists(appDir))
            {
                try { Directory.Delete(appDir, recursive: true); removed++; }
                catch (Exception ex) { log?.Invoke($"[AppShell][Warn] could not delete application folder: {ex.Message}"); }
            }
            if (removed > 0)
                log?.Invoke($"[AppShell] Deleted the application (.sysapp + content folder) — " +
                            "Mapper recreates it on the next Generate (Applications now empty, like Devices).");
            return removed;
        }
    }
}
