using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using CodeGen.Configuration;
using CodeGen.Models;

namespace MapperUI.Services
{
    public static class EaeImportService
    {
        static readonly Dictionary<string, string[]> CatDependencies = new()
        {
            { "Five_State_Actuator_CAT", new[] { "FiveStateActuator" } },
            { "Sensor_Bool_CAT",         new[] { "Sensor_Bool" } },
            { "Actuator_Fault_CAT",      new[] { "FaultLatch" } },
            { "Robot_Task_CAT",          new[] { "Robot_Task_Core" } },
            { "Seven_State_Actuator_CAT",new[] { "SevenStateActuator2" } },
            { "Station_CAT",             new[] { "Station_Core", "Station_Fault", "Station_Status" } },
        };

        static readonly Dictionary<string, string> ComponentTypeToCat = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Actuator_5",  "Five_State_Actuator_CAT" },
            { "Actuator_7",  "Seven_State_Actuator_CAT" },
            { "Sensor_2",    "Sensor_Bool_CAT" },
        };

        public static EaeImportResult Import(MapperConfig cfg, List<VueOneComponent> components)
        {
            var result = new EaeImportResult();
            var libPath = cfg.TemplateLibraryPath;

            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var stagingDir = Path.Combine(Path.GetTempPath(), "VueOneMapper_Import");
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            var neededCats = ResolveNeededCats(components);
            var neededBasics = ResolveNeededBasics(neededCats);
            var exportFiles = new List<string>();

            int step = 1;
            foreach (var basic in neededBasics.OrderBy(b => b))
            {
                var zipPath = FindPackage(libPath, "Basic", basic, ".Basic");
                if (zipPath == null) { result.Warnings.Add($"Basic package not found: {basic}"); continue; }

                var stepDir = Path.Combine(stagingDir, $"{step:D2}_Basic_{basic}");
                Directory.CreateDirectory(stepDir);
                ExtractZip(zipPath, stepDir);

                var exportFile = Directory.GetFiles(stepDir, "*.export", SearchOption.AllDirectories).FirstOrDefault();
                if (exportFile != null)
                {
                    exportFiles.Add(exportFile);
                    MapperLogger.Info($"[Import] Staged Basic: {basic}");
                }
                step++;
            }

            foreach (var cat in neededCats.OrderBy(c => c))
            {
                var zipPath = FindPackage(libPath, "CAT", cat, ".cat");
                if (zipPath == null) { result.Warnings.Add($"CAT package not found: {cat}"); continue; }

                var stepDir = Path.Combine(stagingDir, $"{step:D2}_CAT_{cat}");
                Directory.CreateDirectory(stepDir);
                ExtractZip(zipPath, stepDir);

                var exportFile = Directory.GetFiles(stepDir, "*.export", SearchOption.AllDirectories).FirstOrDefault();
                if (exportFile != null)
                {
                    exportFiles.Add(exportFile);
                    MapperLogger.Info($"[Import] Staged CAT: {cat}");
                }
                step++;
            }

            if (exportFiles.Count == 0)
            {
                result.Warnings.Add("No importable templates found.");
                return result;
            }

            result.ImportFiles = exportFiles;
            result.StagingDirectory = stagingDir;

            foreach (var exportFile in exportFiles)
            {
                MapperLogger.Info($"[Import] Opening: {Path.GetFileName(exportFile)}");
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exportFile,
                        UseShellExecute = true
                    });
                    result.ImportedCount++;
                    Thread.Sleep(3000);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Failed to open {Path.GetFileName(exportFile)}: {ex.Message}");
                }
            }

            result.Success = result.ImportedCount > 0;
            return result;
        }

        static HashSet<string> ResolveNeededCats(List<VueOneComponent> components)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in components)
            {
                var key = $"{c.Type}_{c.States.Count}";
                if (ComponentTypeToCat.TryGetValue(key, out var cat))
                    cats.Add(cat);
            }
            return cats;
        }

        static HashSet<string> ResolveNeededBasics(HashSet<string> cats)
        {
            var basics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in cats)
            {
                if (CatDependencies.TryGetValue(cat, out var deps))
                    foreach (var dep in deps)
                        basics.Add(dep);
            }
            return basics;
        }

        static string? FindPackage(string libPath, string subfolder, string name, string extension)
        {
            var dir = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(dir)) return null;

            foreach (var file in Directory.GetFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                    fileName.Contains(extension, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }

        static void ExtractZip(string zipPath, string targetDir)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var targetPath = Path.Combine(targetDir, entry.FullName);
                var targetFolder = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(targetFolder))
                    Directory.CreateDirectory(targetFolder);
                entry.ExtractToFile(targetPath, true);
            }
        }
    }

    public class EaeImportResult
    {
        public bool Success { get; set; }
        public int ImportedCount { get; set; }
        public List<string> ImportFiles { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string? StagingDirectory { get; set; }
    }
}
