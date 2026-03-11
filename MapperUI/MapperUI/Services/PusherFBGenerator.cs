// MapperUI/MapperUI/Services/PusherFBGenerator.cs
// ─────────────────────────────────────────────────────────────────────────────
// Creates a folder called Five_State_Actuator_CAT_Pusher inside OutputDirectory.
// Every file from the Five_State_Actuator_CAT template folder is copied in,
// with ALL occurrences of "Five_State_Actuator_CAT" renamed to
// "Five_State_Actuator_CAT_Pusher" — in both the filename and the file content.
//
// Output is ONLY the folder + its files. No dfbproj. No system files.
// Jyotsna drops this folder into her EAE project and opens it.
// ─────────────────────────────────────────────────────────────────────────────

using CodeGen.Configuration;
using System;
using System.IO;
using System.Text;

namespace MapperUI.Services
{
    public static class PusherFBGenerator
    {
        private const string SourceName = "Five_State_Actuator_CAT";
        private const string TargetName = "Five_State_Actuator_CAT_Pusher";

        /// <summary>
        /// Copies the Five_State_Actuator_CAT template folder into OutputDirectory,
        /// renaming every file and all content references to Five_State_Actuator_CAT_Pusher.
        /// Returns a human-readable result message.
        /// </summary>
        public static string Generate(MapperConfig cfg)
        {
            // ── 1. Validate config ────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(cfg.ActuatorTemplatePath))
                throw new InvalidOperationException(
                    "ActuatorTemplatePath is empty in mapper_config.json.\n" +
                    $"Set it to the full path of {SourceName}.fbt in your EAE project.");

            if (!File.Exists(cfg.ActuatorTemplatePath))
                throw new FileNotFoundException(
                    $"ActuatorTemplatePath not found:\n{cfg.ActuatorTemplatePath}");

            string? sourceCatDir = Path.GetDirectoryName(cfg.ActuatorTemplatePath);
            if (sourceCatDir == null || !Directory.Exists(sourceCatDir))
                throw new DirectoryNotFoundException(
                    $"Cannot find source CAT folder at:\n{sourceCatDir}");

            // ── 2. Set up output folder ───────────────────────────────────────
            string outputRoot = string.IsNullOrWhiteSpace(cfg.OutputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "Output")
                : cfg.OutputDirectory;

            string targetCatDir = Path.Combine(outputRoot, TargetName);

            // If folder already exists, wipe it so output is always fresh
            if (Directory.Exists(targetCatDir))
                Directory.Delete(targetCatDir, recursive: true);

            Directory.CreateDirectory(targetCatDir);
            MapperLogger.Info($"[PusherFB] Source folder : {sourceCatDir}");
            MapperLogger.Info($"[PusherFB] Target folder : {targetCatDir}");

            // ── 3. Copy every file, renaming and patching content ─────────────
            int copied = 0;
            int skipped = 0;

            foreach (string srcPath in Directory.GetFiles(sourceCatDir, "*", SearchOption.TopDirectoryOnly))
            {
                string srcFileName = Path.GetFileName(srcPath);

                // Rename the file itself
                string destFileName = srcFileName.Replace(SourceName, TargetName);
                string destPath = Path.Combine(targetCatDir, destFileName);

                // Read as text (all CAT companion files are XML/text)
                string content;
                try
                {
                    content = File.ReadAllText(srcPath, Encoding.UTF8);
                }
                catch
                {
                    // Binary file (shouldn't be any, but skip safely)
                    MapperLogger.Warn($"[PusherFB]   Skipped binary : {srcFileName}");
                    skipped++;
                    continue;
                }

                // Replace all references to the source CAT name with the target name
                string patched = content.Replace(SourceName, TargetName);

                File.WriteAllText(destPath, patched, Encoding.UTF8);
                MapperLogger.Info($"[PusherFB]   {srcFileName}  →  {destFileName}");
                copied++;
            }

            // ── 4. Build result message ───────────────────────────────────────
            var sb = new StringBuilder();
            sb.AppendLine($"Generated: {TargetName}");
            sb.AppendLine();
            sb.AppendLine($"  Location : {targetCatDir}");
            sb.AppendLine($"  Files    : {copied} copied, {skipped} skipped");
            sb.AppendLine();
            sb.AppendLine("What Jyotsna should do:");
            sb.AppendLine($"  1. Copy the {TargetName} folder into her EAE project's IEC61499 folder");
            sb.AppendLine("  2. Open EAE → Reload Solution");
            sb.AppendLine($"  3. Confirm {TargetName}.fbt appears and loads without errors");

            return sb.ToString();
        }
    }
}