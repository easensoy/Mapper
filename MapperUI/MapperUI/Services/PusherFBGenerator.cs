// MapperUI/MapperUI/Services/PusherFBGenerator.cs
// ─────────────────────────────────────────────────────────────────────────────
// Scoped single-component generation for validation with Jyotsna.
//
// Uses the SAME pipeline as the main "Generate Code" button:
//   loaded VueOneComponent (from Control.xml)
//   → FBGenerator.GetModifiedTemplateContent()   (injects Name + GUID)
//   → FBGenerator.CopyCatCompanionFiles()         (copies .cfg, offline, opcua etc.)
//   → writes Five_State_Actuator_CAT_Pusher/ folder
//
// No Control.xml re-read. No parallel system. No invented logic.
// The components are already in memory from the Browse/Load step.
// ─────────────────────────────────────────────────────────────────────────────

using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Translation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MapperUI.Services
{
    public static class PusherFBGenerator
    {
        /// <summary>
        /// Finds the first five-state actuator in the loaded components,
        /// runs it through FBGenerator, and writes the output folder.
        /// Returns a human-readable result message.
        /// </summary>
        public static string Generate(MapperConfig cfg, List<VueOneComponent> loadedComponents)
        {
            // ── 1. Guard: need a loaded Control.xml ───────────────────────────
            if (loadedComponents == null || loadedComponents.Count == 0)
                throw new InvalidOperationException(
                    "No components loaded.\n" +
                    "Please Browse and load a Control.xml file first.");

            // ── 2. Find first five-state actuator (Pusher, Feeder, etc.) ──────
            var component = loadedComponents.FirstOrDefault(c =>
                string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) &&
                c.States.Count == 5);

            if (component == null)
                throw new InvalidOperationException(
                    "No five-state actuator found in the loaded Control.xml.\n" +
                    "Expected a component with Type=Actuator and 5 states (e.g. Pusher, Feeder).");

            MapperLogger.Info($"[PusherFB] Component   : {component.Name} ({component.Type}, {component.States.Count} states)");

            // ── 3. Validate template path ─────────────────────────────────────
            if (string.IsNullOrWhiteSpace(cfg.ActuatorTemplatePath))
                throw new InvalidOperationException(
                    "ActuatorTemplatePath is empty in mapper_config.json.\n" +
                    "Set it to: ...\\IEC61499\\Five_State_Actuator_CAT\\Five_State_Actuator_CAT.fbt");

            if (!File.Exists(cfg.ActuatorTemplatePath))
                throw new FileNotFoundException(
                    $"ActuatorTemplatePath not found:\n{cfg.ActuatorTemplatePath}");

            string templateContent = File.ReadAllText(cfg.ActuatorTemplatePath);
            string templateBaseName = Path.GetFileNameWithoutExtension(cfg.ActuatorTemplatePath);
            // e.g. "Five_State_Actuator_CAT"

            MapperLogger.Info($"[PusherFB] Template    : {templateBaseName}");

            // ── 4. Set up output folder: Five_State_Actuator_CAT_Pusher ───────
            string outputRoot = string.IsNullOrWhiteSpace(cfg.OutputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "Output")
                : cfg.OutputDirectory;

            var generator = new FBGenerator();
            var generatedFB = generator.GenerateFromTemplate(component, templateContent, templateBaseName);

            if (!generatedFB.IsValid)
                throw new InvalidOperationException(
                    $"FBGenerator failed for component '{component.Name}'.\nCheck the Debug Console for details.");

            // Output folder is named after the generated FB, e.g. Five_State_Actuator_CAT_Pusher
            string outputDir = Path.Combine(outputRoot, generatedFB.FBName);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
            Directory.CreateDirectory(outputDir);

            MapperLogger.Info($"[PusherFB] Output dir  : {outputDir}");
            MapperLogger.Info($"[PusherFB] FB name     : {generatedFB.FBName}");
            MapperLogger.Info($"[PusherFB] GUID        : {generatedFB.GUID}");

            // ── 5. Write .fbt (template content with Name + GUID injected) ────
            string modifiedFbt = generator.GetModifiedTemplateContent(component, templateContent, templateBaseName);
            File.WriteAllText(Path.Combine(outputDir, generatedFB.FbtFile), modifiedFbt);
            MapperLogger.Info($"[PusherFB]   Written   {generatedFB.FbtFile}");

            // ── 6. Write companion XML files ──────────────────────────────────
            File.WriteAllText(
                Path.Combine(outputDir, generatedFB.CompositeFile),
                generator.ResolveCompositeXml(cfg.ActuatorTemplatePath));
            MapperLogger.Info($"[PusherFB]   Written   {generatedFB.CompositeFile}");

            File.WriteAllText(
                Path.Combine(outputDir, generatedFB.DocFile),
                generator.GetDocXml(generatedFB.FBName));
            MapperLogger.Info($"[PusherFB]   Written   {generatedFB.DocFile}");

            File.WriteAllText(
                Path.Combine(outputDir, generatedFB.MetaFile),
                generator.GetMetaXml(generatedFB.FBName, generatedFB.GUID));
            MapperLogger.Info($"[PusherFB]   Written   {generatedFB.MetaFile}");

            // ── 7. Copy CAT companion files (.cfg, offline.xml, opcua.xml etc.) ─
            var companions = generator.CopyCatCompanionFiles(
                cfg.ActuatorTemplatePath, outputDir, generatedFB.FBName);
            foreach (var f in companions)
                MapperLogger.Info($"[PusherFB]   Copied    {Path.GetFileName(f)}");

            // ── 8. Also copy to EAEDeployPath if configured ───────────────────
            bool deployed = false;
            if (!string.IsNullOrWhiteSpace(cfg.EAEDeployPath) &&
                Directory.Exists(cfg.EAEDeployPath))
            {
                try
                {
                    string deployTarget = Path.Combine(cfg.EAEDeployPath, generatedFB.FBName);
                    CopyDirectory(outputDir, deployTarget);
                    MapperLogger.Info($"[PusherFB] Deployed to EAEDeployPath: {deployTarget}");
                    deployed = true;
                }
                catch (Exception ex)
                {
                    MapperLogger.Warn($"[PusherFB] EAEDeployPath copy failed (non-fatal): {ex.Message}");
                }
            }

            // ── 9. Summary ────────────────────────────────────────────────────
            var sb = new StringBuilder();
            sb.AppendLine($"Generated: {generatedFB.FBName}");
            sb.AppendLine($"Component: {component.Name}  ({component.States.Count} states)");
            sb.AppendLine($"GUID: {generatedFB.GUID}");
            sb.AppendLine();
            sb.AppendLine($"Output folder: {outputDir}");
            sb.AppendLine($"Files: {generatedFB.FbtFile}");
            sb.AppendLine($"       {generatedFB.CompositeFile}");
            sb.AppendLine($"       {generatedFB.DocFile}");
            sb.AppendLine($"       {generatedFB.MetaFile}");
            foreach (var f in companions)
                sb.AppendLine($"       {Path.GetFileName(f)}");
            if (deployed)
                sb.AppendLine($"\nAlso deployed to: {cfg.EAEDeployPath}");
            sb.AppendLine();
            sb.AppendLine("What Jyotsna should do:");
            sb.AppendLine($"  1. Copy the {generatedFB.FBName}/ folder into her EAE project's IEC61499 folder");
            sb.AppendLine("  2. Open EAE → Reload Solution");
            sb.AppendLine($"  3. Confirm {generatedFB.FBName}.fbt appears and loads without errors");

            return sb.ToString();
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}