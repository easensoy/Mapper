using System;
using System.Collections.Generic;
using System.IO;
using CodeGen.Configuration;
using CodeGen.Services;

namespace CodeGen.Artefacts
{
    // EAE Solution Integrity requires each deployed artefact ({stem}.sysres/syslay/sysdev)
    // to have a sibling {stem}/ folder with an opcua.xml whose UID = the parent folder GUID.
    // Only opcua.xml is Mapper-written; EAE produces offline.xml/opcuaclient.xml on open.
    public static class OpcuaCompanionEmitter
    {
        // Writes opcua.xml into a {stem}/ folder beside the artefact (UID = container GUID).
        public static void EmitForArtefact(string artefactPath)
        {
            if (string.IsNullOrWhiteSpace(artefactPath)) return;
            var parentDir = Path.GetDirectoryName(artefactPath);
            if (string.IsNullOrEmpty(parentDir)) return;
            var stem = Path.GetFileNameWithoutExtension(artefactPath);
            if (string.IsNullOrEmpty(stem)) return;

            var opcuaDir = Path.Combine(parentDir, stem);
            try { Directory.CreateDirectory(opcuaDir); }
            catch { return; }

            // UID = parent folder name (the container GUID).
            var uid = Path.GetFileName(parentDir);

            WriteOpcuaFile(Path.Combine(opcuaDir, "opcua.xml"), uid);
        }

        // Fills any missing opcua.xml (UID = parent container GUID) in every companion folder
        // (a subfolder beside a .sysres/.syslay/.sysdev) so EAE's "Missing Project Files"
        // check passes. Non-destructive — only fills missing files.
        public static int EnsureOpcuaInAllResourceFolders(string eaeRoot)
        {
            if (string.IsNullOrWhiteSpace(eaeRoot)) return 0;

            string systemDir;
            try { systemDir = Path.Combine(eaeRoot, "IEC61499", "System"); }
            catch { return 0; }
            if (!Directory.Exists(systemDir)) return 0;

            int created = 0;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(systemDir, "*", SearchOption.AllDirectories); }
            catch { return 0; }

            foreach (var dir in dirs)
            {
                try
                {
                    var parent = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(parent)) continue;
                    if (!ParentHasArtefact(parent)) continue;

                    var opcuaPath = Path.Combine(dir, "opcua.xml");
                    if (File.Exists(opcuaPath)) continue; // never overwrite

                    var uid = Path.GetFileName(parent);
                    if (WriteOpcuaFile(opcuaPath, uid)) created++;
                }
                catch
                {
                    // Skip this folder on any error; keep sweeping the rest.
                }
            }

            return created;
        }

        private static bool ParentHasArtefact(string dir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f);
                    if (string.Equals(ext, ".sysres", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".syslay", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".sysdev", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* unreadable dir — treat as no artefact */ }
            return false;
        }

        // The two public entry points are linked by the VueOne hidden runner, so they keep their
        // signatures and the template root is resolved here instead of being threaded through.
        internal static string BuildOpcuaCompanion(string uid)
        {
            MapperConfig? cfg = null;
            try { cfg = MapperConfig.Load(); } catch { /* fall back to the default template root */ }
            return TemplateDocument.Load(cfg, @"Companion\opcua.xml",
                new Dictionary<string, string> { ["Uid"] = uid });
        }

        private static bool WriteOpcuaFile(string opcuaPath, string uid)
        {
            var content = BuildOpcuaCompanion(uid);

            // Best-effort — a transient lock isn't fatal; the next regen retries.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    File.WriteAllText(opcuaPath, content);
                    return true;
                }
                catch (IOException) { System.Threading.Thread.Sleep(50 * (attempt + 1)); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(50 * (attempt + 1)); }
            }
            return false;
        }
    }
}
