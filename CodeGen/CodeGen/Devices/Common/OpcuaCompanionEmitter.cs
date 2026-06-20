using System;
using System.Collections.Generic;
using System.IO;

namespace CodeGen.Artefacts
{
    /// <summary>
    /// Emits the <c>opcua.xml</c> companion files EAE's Solution Integrity
    /// check requires. Every deployed artefact at
    /// <c>IEC61499/System/{sysGuid}/{containerGuid}/{stem}.{sysres|syslay|sysdev}</c>
    /// expects a sibling folder <c>{stem}/</c> containing at least an
    /// <c>opcua.xml</c> whose <c>UID</c> attribute equals the parent folder
    /// GUID (i.e. <c>{containerGuid}</c>) — the same convention the reference
    /// <c>SMC_Rig_Expo_withClamp</c> project uses. A valid resource folder also
    /// carries EAE-generated <c>offline.xml</c>/<c>opcuaclient.xml</c>, but only
    /// <c>opcua.xml</c> is Mapper-written; the rest are produced by EAE on open.
    /// </summary>
    public static class OpcuaCompanionEmitter
    {
        /// <summary>
        /// Writes an <c>opcua.xml</c> into a folder named after the artefact's
        /// stem, beside the artefact. The file's <c>UID</c> equals the parent
        /// folder name (the syslay/sysres/sysdev container GUID), matching the
        /// reference project's convention. Idempotent — overwrites any existing
        /// file. Best-effort: a transient lock is retried a few times because
        /// EAE re-checks Solution Integrity at open time, so a missed write is
        /// retried by the next regen rather than being fatal.
        /// </summary>
        public static void EmitForArtefact(string artefactPath)
        {
            if (string.IsNullOrWhiteSpace(artefactPath)) return;
            var parentDir = Path.GetDirectoryName(artefactPath);
            if (string.IsNullOrEmpty(parentDir)) return;
            var stem = Path.GetFileNameWithoutExtension(artefactPath);
            if (string.IsNullOrEmpty(stem)) return;

            // Folder named after the artefact's stem — opcua.xml lives inside.
            var opcuaDir = Path.Combine(parentDir, stem);
            try { Directory.CreateDirectory(opcuaDir); }
            catch { return; }

            // UID = parent folder name (the syslay/sysres container GUID).
            // Reference project uses the immediate parent's GUID as the UID.
            var uid = Path.GetFileName(parentDir);

            WriteOpcuaFile(Path.Combine(opcuaDir, "opcua.xml"), uid);
        }

        /// <summary>
        /// Sweeps <c>{eaeRoot}\IEC61499\System</c> recursively and fills in any
        /// missing <c>opcua.xml</c> companion files so EAE's Solution Integrity
        /// "Missing Project Files" check passes. A companion folder is any
        /// immediate subfolder that sits beside a <c>.sysres</c>/<c>.syslay</c>/
        /// <c>.sysdev</c> file (i.e. its parent directory contains at least one
        /// such artefact) — that covers both the current resource folder and the
        /// stale leftovers from earlier deploys, all of which EAE flags when they
        /// lack an <c>opcua.xml</c>. Each created file gets <c>UID</c> = the
        /// subfolder's PARENT folder GUID, matching <see cref="EmitForArtefact"/>.
        /// Non-destructive: only fills missing files, never deletes or overwrites
        /// existing ones. Defensive: a failure on one folder is swallowed and the
        /// sweep continues. Returns the number of <c>opcua.xml</c> files created.
        /// </summary>
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
                    // Only consider folders that sit beside a deployed artefact —
                    // i.e. their parent directory contains a .sysres/.syslay/.sysdev.
                    var parent = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(parent)) continue;
                    if (!ParentHasArtefact(parent)) continue;

                    var opcuaPath = Path.Combine(dir, "opcua.xml");
                    if (File.Exists(opcuaPath)) continue; // never overwrite

                    // UID = the companion folder's parent GUID (the container GUID).
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

        /// <summary>
        /// True when <paramref name="dir"/> contains at least one deployed
        /// artefact file (<c>.sysres</c>/<c>.syslay</c>/<c>.sysdev</c>), marking
        /// its immediate subfolders as EAE companion folders.
        /// </summary>
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

        /// <summary>
        /// Writes the canonical <c>opcua.xml</c> body with the given
        /// <paramref name="uid"/>. Best-effort with a short retry loop on
        /// transient IO/permission errors. Returns true if the file was written.
        /// </summary>
        private static bool WriteOpcuaFile(string opcuaPath, string uid)
        {
            var content =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<OPCUAComplexObject xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
                "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
                $"  <OPCUAComplexObject UID=\"{uid}\" />\r\n" +
                "</OPCUAComplexObject>";

            // Best-effort write — EAE re-checks Solution Integrity at open
            // time, so a transient lock isn't fatal. The next regen retries.
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
