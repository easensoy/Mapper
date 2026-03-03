using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Validation;

namespace MapperUI.Services
{
    public class MapperService
    {
        private MapperConfig? _config;

        public MapperService() { }

        public async Task<MapperResult> RunMapping(VueOneComponent component)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (component == null) throw new ArgumentNullException(nameof(component));
                    var config = LoadConfig();
                    return ProcessComponent(component, ResolveTemplatePath(component, config), config);
                }
                catch (Exception ex)
                {
                    return new MapperResult { Success = false, ComponentName = component?.Name ?? string.Empty, ErrorMessage = ex.Message };
                }
            });
        }

        /// <summary>Phase 2 — uses already-loaded components, never re-reads Control.xml.</summary>
        public async Task<SystemInjectionResult> RunSystemInjection(List<VueOneComponent> loadedComponents)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var config = LoadConfig();
                    return new SystemInjector().Inject(config, loadedComponents);
                }
                catch (Exception ex)
                {
                    return new SystemInjectionResult { Success = false, ErrorMessage = ex.Message };
                }
            });
        }

        private MapperConfig LoadConfig()
        {
            if (_config != null) return _config;
            _config = MapperConfig.Load();
            return _config;
        }

        private string ResolveTemplatePath(VueOneComponent component, MapperConfig config) =>
            string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase)
                ? config.ActuatorTemplatePath : config.SensorTemplatePath;

        private MapperResult ProcessComponent(VueOneComponent component, string templatePath, MapperConfig config)
        {
            var validator = new ComponentValidator();
            var validationResult = validator.Validate(component);
            if (!validationResult.IsValid)
                return new MapperResult { Success = false, ComponentName = component.Name, ValidationResult = validationResult, ErrorMessage = "Validation failed." };
            if (!File.Exists(templatePath))
                return new MapperResult { Success = false, ComponentName = component.Name, ValidationResult = validationResult, ErrorMessage = $"Template not found: {templatePath}" };

            var templateContent = File.ReadAllText(templatePath);
            var rawName = Path.GetFileNameWithoutExtension(templatePath);
            var templateBaseName = string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase) && rawName.EndsWith("_CAT", StringComparison.OrdinalIgnoreCase) ? rawName[..^4] : rawName;

            var generator = new FBGenerator();
            var generatedFB = generator.GenerateFromTemplate(component, templateContent, templateBaseName);
            if (!generatedFB.IsValid)
                return new MapperResult { Success = false, ComponentName = component.Name, ValidationResult = validationResult, ErrorMessage = "FB generation failed." };

            Directory.CreateDirectory(config.OutputDirectory);
            var modifiedContent = generator.GetModifiedTemplateContent(component, templateContent, templateBaseName);
            var fbtPath = Path.Combine(config.OutputDirectory, generatedFB.FbtFile);
            File.WriteAllText(fbtPath, modifiedContent);
            File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.CompositeFile), generator.ResolveCompositeXml(templatePath));
            File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.DocFile), generator.GetDocXml(generatedFB.FBName));
            File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.MetaFile), generator.GetMetaXml(generatedFB.FBName, generatedFB.GUID));
            var companions = generator.CopyCatCompanionFiles(templatePath, config.OutputDirectory, generatedFB.FBName);

            if (Directory.Exists(config.EAEDeployPath))
            {
                File.Copy(fbtPath, Path.Combine(config.EAEDeployPath, generatedFB.FbtFile), true);
                File.Copy(Path.Combine(config.OutputDirectory, generatedFB.CompositeFile), Path.Combine(config.EAEDeployPath, generatedFB.CompositeFile), true);
                File.Copy(Path.Combine(config.OutputDirectory, generatedFB.DocFile), Path.Combine(config.EAEDeployPath, generatedFB.DocFile), true);
                File.Copy(Path.Combine(config.OutputDirectory, generatedFB.MetaFile), Path.Combine(config.EAEDeployPath, generatedFB.MetaFile), true);
                foreach (var c in companions)
                    File.Copy(Path.Combine(config.OutputDirectory, c), Path.Combine(config.EAEDeployPath, c), true);
            }

            return new MapperResult { Success = true, ComponentName = component.Name, GeneratedFB = generatedFB, OutputPath = config.OutputDirectory, DeployPath = config.EAEDeployPath, ValidationResult = validationResult };
        }
    }
}