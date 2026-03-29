using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CodeGen.Configuration;
using CodeGen.Models;

namespace MapperUI.Services
{
    public static class ImportInstructionGenerator
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

        public static ImportInstructions Generate(MapperConfig cfg, List<VueOneComponent> components)
        {
            var result = new ImportInstructions();
            var libPath = cfg.TemplateLibraryPath;

            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var outputDir = Path.Combine(Environment.CurrentDirectory, "Output");
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);

            var neededCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in components)
            {
                var key = $"{c.Type}_{c.States.Count}";
                if (ComponentTypeToCat.TryGetValue(key, out var cat))
                {
                    neededCats.Add(cat);
                    result.ComponentMappings.Add(new ComponentMapping
                    {
                        ComponentName = c.Name,
                        ComponentType = c.Type,
                        StateCount = c.States.Count,
                        AssignedCAT = cat
                    });
                }
                else
                {
                    result.ComponentMappings.Add(new ComponentMapping
                    {
                        ComponentName = c.Name,
                        ComponentType = c.Type,
                        StateCount = c.States.Count,
                        AssignedCAT = "(unsupported)"
                    });
                }
            }

            var neededBasics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in neededCats)
            {
                if (CatDependencies.TryGetValue(cat, out var deps))
                    foreach (var dep in deps)
                        neededBasics.Add(dep);
            }

            int stepNumber = 1;

            var basicDir = Path.Combine(outputDir, "1_Basic");
            Directory.CreateDirectory(basicDir);
            foreach (var basic in neededBasics.OrderBy(b => b))
            {
                var src = FindPackage(libPath, "Basic", basic, ".Basic");
                if (src != null)
                {
                    var dst = Path.Combine(basicDir, Path.GetFileName(src));
                    File.Copy(src, dst, true);
                    result.ImportSteps.Add(new ImportStep
                    {
                        StepNumber = stepNumber++,
                        Folder = "1_Basic",
                        FileName = Path.GetFileName(src),
                        Description = $"Basic FB: {basic} (dependency)"
                    });
                }
                else
                {
                    result.Warnings.Add($"Missing Basic package: {basic}");
                }
            }

            var catDir = Path.Combine(outputDir, "2_CAT");
            Directory.CreateDirectory(catDir);
            foreach (var cat in neededCats.OrderBy(c => c))
            {
                var src = FindPackage(libPath, "CAT", cat, ".cat");
                if (src != null)
                {
                    var dst = Path.Combine(catDir, Path.GetFileName(src));
                    File.Copy(src, dst, true);
                    result.ImportSteps.Add(new ImportStep
                    {
                        StepNumber = stepNumber++,
                        Folder = "2_CAT",
                        FileName = Path.GetFileName(src),
                        Description = $"CAT: {cat}"
                    });
                }
                else
                {
                    result.Warnings.Add($"Missing CAT package: {cat}");
                }
            }

            var manifestPath = Path.Combine(outputDir, "IMPORT_ORDER.txt");
            WriteManifest(manifestPath, cfg, components, result);
            result.OutputDirectory = outputDir;
            result.ManifestPath = manifestPath;

            return result;
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

        static void WriteManifest(string path, MapperConfig cfg, List<VueOneComponent> components,
            ImportInstructions result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine("  VueOne Mapper — Manual Import Instructions");
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"  Source:    {cfg.SystemXmlPath}");
            sb.AppendLine();

            sb.AppendLine("───────────────────────────────────────────────────────────");
            sb.AppendLine("  COMPONENTS DETECTED FROM CONTROL.XML");
            sb.AppendLine("───────────────────────────────────────────────────────────");
            foreach (var m in result.ComponentMappings)
            {
                sb.AppendLine($"  {m.ComponentName,-20} {m.ComponentType,-10} {m.StateCount} states → {m.AssignedCAT}");
            }
            sb.AppendLine();

            var supported = result.ComponentMappings.Count(m => m.AssignedCAT != "(unsupported)");
            var unsupported = result.ComponentMappings.Count(m => m.AssignedCAT == "(unsupported)");
            sb.AppendLine($"  Supported: {supported}  |  Unsupported: {unsupported}");
            sb.AppendLine($"  Types needed: {result.ImportSteps.Count(s => s.Folder == "2_CAT")} CAT(s), " +
                          $"{result.ImportSteps.Count(s => s.Folder == "1_Basic")} Basic FB(s)");
            sb.AppendLine();

            sb.AppendLine("───────────────────────────────────────────────────────────");
            sb.AppendLine("  IMPORT INTO EAE — FOLLOW THIS ORDER");
            sb.AppendLine("───────────────────────────────────────────────────────────");
            sb.AppendLine("  Open any blank EAE project.");
            sb.AppendLine("  Right-click project → Import → select each file below.");
            sb.AppendLine();

            foreach (var step in result.ImportSteps)
            {
                sb.AppendLine($"  Step {step.StepNumber}: {step.Folder}/{step.FileName}");
                sb.AppendLine($"         {step.Description}");
            }
            sb.AppendLine();

            sb.AppendLine("───────────────────────────────────────────────────────────");
            sb.AppendLine("  AFTER IMPORT — CREATE INSTANCES MANUALLY");
            sb.AppendLine("───────────────────────────────────────────────────────────");
            sb.AppendLine("  Drag CAT types into the Application layer and name them:");
            sb.AppendLine();

            foreach (var m in result.ComponentMappings.Where(m => m.AssignedCAT != "(unsupported)"))
            {
                sb.AppendLine($"  - {m.ComponentName} (type: {m.AssignedCAT})");
            }
            sb.AppendLine();
            sb.AppendLine("  Then wire manually and validate with Alex.");
            sb.AppendLine();

            if (result.Warnings.Any())
            {
                sb.AppendLine("───────────────────────────────────────────────────────────");
                sb.AppendLine("  WARNINGS");
                sb.AppendLine("───────────────────────────────────────────────────────────");
                foreach (var w in result.Warnings)
                    sb.AppendLine($"  !! {w}");
                sb.AppendLine();
            }

            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine("  Next step: Once types are validated, the Mapper will");
            sb.AppendLine("  automate instance injection + wiring via syslay/sysres.");
            sb.AppendLine("═══════════════════════════════════════════════════════════");

            File.WriteAllText(path, sb.ToString());
        }
    }

    public class ImportInstructions
    {
        public List<ComponentMapping> ComponentMappings { get; set; } = new();
        public List<ImportStep> ImportSteps { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string OutputDirectory { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
    }

    public class ComponentMapping
    {
        public string ComponentName { get; set; } = string.Empty;
        public string ComponentType { get; set; } = string.Empty;
        public int StateCount { get; set; }
        public string AssignedCAT { get; set; } = string.Empty;
    }

    public class ImportStep
    {
        public int StepNumber { get; set; }
        public string Folder { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}