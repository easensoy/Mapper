using System;
using System.IO;

namespace CodeGen.Devices.Core
{
    // Clean deletes the application (.sysapp + content folder); this recreates it
    // create-if-absent on the next Generate. Bootstrap is required BEFORE Generate's
    // PrepareDemonstratorForGeneration (it throws if the .syslay is missing, and
    // AlignApplicationName only renames an existing .sysapp).
    public static class ApplicationShellEmitter
    {
        public const string SystemId = "00000000-0000-0000-0000-000000000000";
        public const string AppId    = "00000000-0000-0000-0000-000000000001";

        // Written name-blank; M262SysdevEmitter.AlignApplicationName sets the WMG name later.
        const string SysappXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<Application xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns=\"https://www.se.com/LibraryElements\" Name=\"\" " +
            $"ID=\"{AppId}\" />";

        // Placeholder so PrepareDemonstratorForGeneration's File.Exists check passes;
        // GenerateStation1TestSyslay overwrites this with the real layout.
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

        // Recreates the application shell + re-registers it in the .dfbproj, only when the
        // .sysapp is absent. Runs at the start of Generate, before Prepare needs the layout.
        public static bool EnsureApplicationShell(string? eaeRoot, Action<string>? log = null)
        {
            if (string.IsNullOrEmpty(eaeRoot)) return false;
            var systemDir = Path.Combine(eaeRoot, "IEC61499", "System");
            if (!Directory.Exists(systemDir)) return false;

            var containerDir = Path.Combine(systemDir, SystemId);          // System/000000/
            var sysappPath   = Path.Combine(containerDir, AppId + ".sysapp");
            if (File.Exists(sysappPath)) return false;                     // present -> no-op

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

        // Deletes the .sysapp + its content folder; keeps the container, .system root and
        // dfbproj shell. Best-effort per item — an EAE-locked file is logged, not fatal
        // (close EAE before Clean).
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
