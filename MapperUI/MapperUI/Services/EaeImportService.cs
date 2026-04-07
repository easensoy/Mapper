using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

        public static EaeImportResult Import(MapperConfig cfg, List<VueOneComponent> components,
            Action<string>? onProgress = null)
        {
            var result = new EaeImportResult();
            var libPath = cfg.TemplateLibraryPath;
            void Log(string msg) { onProgress?.Invoke(msg); MapperLogger.Info(msg); }

            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var eaeProjectDir = DeriveEaeProjectDir(cfg);
            if (string.IsNullOrWhiteSpace(eaeProjectDir))
                throw new InvalidOperationException("Cannot determine EAE project directory.");

            var neededCats = ResolveNeededCats(components);
            var neededBasics = ResolveNeededBasics(neededCats);

            Log("Importing templates...");

            foreach (var basic in neededBasics.OrderBy(b => b))
            {
                var zipPath = FindPackage(libPath, "Basic", basic, ".Basic");
                if (zipPath == null) { result.Warnings.Add($"Basic not found: {basic}"); continue; }

                ExtractToProject(zipPath, eaeProjectDir);
                result.ImportedCount++;
                Log($"  Basic: {basic}");
            }

            foreach (var cat in neededCats.OrderBy(c => c))
            {
                var zipPath = FindPackage(libPath, "CAT", cat, ".cat");
                if (zipPath == null) { result.Warnings.Add($"CAT not found: {cat}"); continue; }

                ExtractToProject(zipPath, eaeProjectDir);
                result.ImportedCount++;
                Log($"  CAT: {cat}");
            }

            result.Success = result.ImportedCount > 0;
            if (result.Success)
                Log($"Done. {result.ImportedCount} template(s) imported. Build in EAE to register.");
            return result;
        }

        static void ExtractToProject(string zipPath, string eaeProjectDir)
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };
            string? prefixToStrip = null;

            var firstFile = zip.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
            if (firstFile != null)
            {
                var parts = firstFile.FullName.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    prefixToStrip = parts[0] + "/";
            }

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var relativePath = entry.FullName;
                if (prefixToStrip != null && relativePath.StartsWith(prefixToStrip, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(prefixToStrip.Length);

                var targetPath = Path.Combine(eaeProjectDir, relativePath);
                var targetDir = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
                if (!File.Exists(targetPath))
                    entry.ExtractToFile(targetPath);
            }
        }

        static string? DeriveEaeProjectDir(MapperConfig cfg)
        {
            var syslayPath = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(syslayPath)) return null;
            var dir = Path.GetDirectoryName(syslayPath);
            while (dir != null)
            {
                var checkDir = dir;
                while (checkDir != null)
                {
                    if (Directory.GetFiles(checkDir, "*.dfbproj").Any())
                        return Path.GetDirectoryName(checkDir);
                    checkDir = Path.GetDirectoryName(checkDir);
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static HashSet<string> ResolveNeededCats(List<VueOneComponent> components)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in components)
            {
                var key = $"{c.Type}_{c.States.Count}";
                if (ComponentTypeToCat.TryGetValue(key, out var cat)) cats.Add(cat);
            }
            return cats;
        }

        static HashSet<string> ResolveNeededBasics(HashSet<string> cats)
        {
            var basics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in cats)
                if (CatDependencies.TryGetValue(cat, out var deps))
                    foreach (var dep in deps) basics.Add(dep);
            return basics;
        }

        static string? FindPackage(string libPath, string subfolder, string name, string extension)
        {
            var dir = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(dir)) return null;
            foreach (var file in Directory.GetFiles(dir))
            {
                var fn = Path.GetFileName(file);
                if (fn.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                    fn.Contains(extension, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }
    }

    public class EaeImportResult
    {
        public bool Success { get; set; }
        public int ImportedCount { get; set; }
        public List<string> Warnings { get; set; } = new();
    }
}
