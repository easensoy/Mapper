// MapperUI/MapperUI/Services/PusherFBGenerator.cs
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Translation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MapperUI.Services
{
    public static class PusherFBGenerator
    {
        private const int IoRetryCount = 6;
        private const int IoRetryDelayMs = 200;

        /// <summary>
        /// Finds the first five-state actuator in the loaded components,
        /// runs it through FBGenerator, and writes the output folder.
        /// Returns a human-readable result message.
        /// </summary>
        public static string Generate(MapperConfig cfg, List<VueOneComponent> loadedComponents)
        {
            if (loadedComponents == null || loadedComponents.Count == 0)
            {
                throw new InvalidOperationException(
                    "No components loaded.\n" +
                    "Please Browse and load a Control.xml file first.");
            }

            if (cfg == null)
                throw new InvalidOperationException("Mapper config is null.");

            var component = ResolveTargetComponent(loadedComponents);
            MapperLogger.Info($"[PusherFB] Component   : {component.Name} ({component.Type}, {component.States.Count} states)");

            if (string.IsNullOrWhiteSpace(cfg.ActuatorTemplatePath))
            {
                throw new InvalidOperationException(
                    "ActuatorTemplatePath is empty in mapper_config.json.\n" +
                    "Set it to: ...\\IEC61499\\Five_State_Actuator_CAT\\Five_State_Actuator_CAT.fbt");
            }

            if (!File.Exists(cfg.ActuatorTemplatePath))
                throw new FileNotFoundException($"ActuatorTemplatePath not found:\n{cfg.ActuatorTemplatePath}");

            var templateContent = File.ReadAllText(cfg.ActuatorTemplatePath);
            var templateBaseName = Path.GetFileNameWithoutExtension(cfg.ActuatorTemplatePath);
            MapperLogger.Info($"[PusherFB] Template    : {templateBaseName}");

            var generator = new FBGenerator();
            var generatedFB = generator.GenerateFromTemplate(component, templateContent, templateBaseName);
            if (!generatedFB.IsValid)
            {
                throw new InvalidOperationException(
                    $"FBGenerator failed for component '{component.Name}'.\n" +
                    "Check the Debug Console for details.");
            }

            var outputRoot = string.IsNullOrWhiteSpace(cfg.OutputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "Output")
                : cfg.OutputDirectory;

            var outputDir = PrepareOutputDirectory(outputRoot, generatedFB.FBName);

            MapperLogger.Info($"[PusherFB] Output dir  : {outputDir}");
            MapperLogger.Info($"[PusherFB] FB name     : {generatedFB.FBName}");
            MapperLogger.Info($"[PusherFB] GUID        : {generatedFB.GUID}");

            var modifiedFbt = generator.GetModifiedTemplateContent(component, templateContent, templateBaseName);
            File.WriteAllText(Path.Combine(outputDir, generatedFB.FbtFile), modifiedFbt);
            MapperLogger.Info($"[PusherFB]   Written   {generatedFB.FbtFile}");

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

            var companions = generator.CopyCatCompanionFiles(
                cfg.ActuatorTemplatePath,
                outputDir,
                generatedFB.FBName);
            foreach (var file in companions)
                MapperLogger.Info($"[PusherFB]   Copied    {Path.GetFileName(file)}");

            var deployed = false;
            if (!string.IsNullOrWhiteSpace(cfg.EAEDeployPath) && Directory.Exists(cfg.EAEDeployPath))
            {
                try
                {
                    var deployTarget = Path.Combine(cfg.EAEDeployPath, generatedFB.FBName);
                    CopyDirectory(outputDir, deployTarget);
                    MapperLogger.Info($"[PusherFB] Deployed to EAEDeployPath: {deployTarget}");
                    deployed = true;
                }
                catch (Exception ex)
                {
                    MapperLogger.Warn($"[PusherFB] EAEDeployPath copy failed (non-fatal): {ex.Message}");
                }
            }

            var summary = new StringBuilder();
            summary.AppendLine($"Generated: {generatedFB.FBName}");
            summary.AppendLine($"Component: {component.Name}  ({component.States.Count} states)");
            summary.AppendLine($"GUID: {generatedFB.GUID}");
            summary.AppendLine();
            summary.AppendLine($"Output folder: {outputDir}");
            summary.AppendLine($"Files: {generatedFB.FbtFile}");
            summary.AppendLine($"       {generatedFB.CompositeFile}");
            summary.AppendLine($"       {generatedFB.DocFile}");
            summary.AppendLine($"       {generatedFB.MetaFile}");
            foreach (var file in companions)
                summary.AppendLine($"       {Path.GetFileName(file)}");

            if (deployed)
                summary.AppendLine($"\nAlso deployed to: {cfg.EAEDeployPath}");

            summary.AppendLine();
            summary.AppendLine("What Jyotsna should do:");
            summary.AppendLine($"  1. Copy the {generatedFB.FBName}/ folder into her EAE project's IEC61499 folder");
            summary.AppendLine("  2. Open EAE → Reload Solution");
            summary.AppendLine($"  3. Confirm {generatedFB.FBName}.fbt appears and loads without errors");

            return summary.ToString();
        }

        private static VueOneComponent ResolveTargetComponent(List<VueOneComponent> loadedComponents)
        {
            var fiveStateActuators = loadedComponents
                .Where(c => string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) && c.States.Count == 5)
                .ToList();

            if (fiveStateActuators.Count == 0)
            {
                throw new InvalidOperationException(
                    "No five-state actuator found in the loaded Control.xml.\n" +
                    "Expected a component with Type=Actuator and 5 states (e.g. Feeder, Pusher).");
            }

            var feeder = fiveStateActuators.FirstOrDefault(c =>
                string.Equals(c.Name, "Feeder", StringComparison.OrdinalIgnoreCase));

            if (feeder != null)
                return feeder;

            return fiveStateActuators
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        private static string PrepareOutputDirectory(string outputRoot, string fbName)
        {
            Directory.CreateDirectory(outputRoot);

            var outputDir = Path.Combine(outputRoot, fbName);
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                return outputDir;
            }

            try
            {
                ClearDirectory(outputDir);
                return outputDir;
            }
            catch (Exception ex)
            {
                throw new IOException(
                    "Unable to refresh the existing output folder because one or more files are locked.\n" +
                    $"Folder: {outputDir}\n" +
                    "Close EAE, Explorer preview windows, and any editor using this folder, then try again.",
                    ex);
            }
        }

        private static void ClearDirectory(string directoryPath)
        {
            foreach (var file in Directory.GetFiles(directoryPath))
            {
                ExecuteWithRetries(() => File.Delete(file));
            }

            foreach (var subDir in Directory.GetDirectories(directoryPath))
            {
                ExecuteWithRetries(() => Directory.Delete(subDir, recursive: true));
            }
        }

        private static void ExecuteWithRetries(Action action)
        {
            Exception lastException = null;

            for (var attempt = 1; attempt <= IoRetryCount; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException ex)
                {
                    lastException = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastException = ex;
                }

                if (attempt < IoRetryCount)
                    Thread.Sleep(IoRetryDelayMs);
            }

            throw lastException ?? new IOException("I/O operation failed.");
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source))
            {
                var destinationPath = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, destinationPath, overwrite: true);
            }

            foreach (var subDir in Directory.GetDirectories(source))
            {
                var destinationPath = Path.Combine(destination, Path.GetFileName(subDir));
                CopyDirectory(subDir, destinationPath);
            }
        }
    }
}