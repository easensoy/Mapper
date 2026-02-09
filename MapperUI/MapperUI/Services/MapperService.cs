using System;
using System.IO;
using System.Threading.Tasks;
using CodeGen.Configuration;
using CodeGen.Translation;
using CodeGen.Validation;
using CodeGen.Models;

namespace MapperUI.Services
{
    public class MapperService
    {
        private MapperConfig? _config;

        public MapperService()
        {
        }

        public async Task<MapperResult> RunMapping(VueOneComponent component)
        {
            return await Task.Run(() =>
            {
                try
                {
                    _config = LoadConfig();
                    var templatePath = ResolveTemplatePath(component);

                    return ProcessComponent(
                        component,
                        templatePath);
                }
                catch (Exception ex)
                {
                    return new MapperResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                }
            });
        }

        private MapperConfig LoadConfig()
        {
            if (_config != null)
            {
                return _config;
            }

            return MapperConfig.Load();
        }

        private string ResolveTemplatePath(VueOneComponent component)
        {
            if (_config == null)
            {
                _config = LoadConfig();
            }

            return component.Type == "Actuator"
                ? _config.ActuatorTemplatePath
                : _config.SensorTemplatePath;
        }

        private MapperResult ProcessComponent(
            VueOneComponent component,
            string templatePath)
        {
            var validator = new ComponentValidator();
            var validationResult = validator.Validate(component);

            if (!validationResult.IsValid)
            {
                return new MapperResult
                {
                    Success = false,
                    ComponentName = component.Name,
                    ValidationResult = validationResult
                };
            }

            var templateContent = File.ReadAllText(templatePath);

            var generator = new FBGenerator();
            var generatedFB = generator.GenerateFromTemplate(
                component,
                templateContent,
                Path.GetFileNameWithoutExtension(templatePath));

            Directory.CreateDirectory(_config!.OutputDirectory);

            var modifiedContent = generator.GetModifiedTemplateContent(
                component,
                templateContent);

            var fbtPath = Path.Combine(
                _config.OutputDirectory,
                generatedFB.FbtFile);

            File.WriteAllText(fbtPath, modifiedContent);
            File.WriteAllText(
                Path.Combine(_config.OutputDirectory, generatedFB.CompositeFile),
                generator.GetCompositeXml());

            File.WriteAllText(
                Path.Combine(_config.OutputDirectory, generatedFB.DocFile),
                generator.GetDocXml(generatedFB.FBName));

            if (Directory.Exists(_config.EAEDeployPath))
            {
                File.Copy(
                    fbtPath,
                    Path.Combine(_config.EAEDeployPath, generatedFB.FbtFile),
                    overwrite: true);
            }

            return new MapperResult
            {
                Success = true,
                ComponentName = component.Name,
                GeneratedFB = generatedFB,
                OutputPath = _config.OutputDirectory,
                DeployPath = _config.EAEDeployPath,
                ValidationResult = validationResult
            };
        }
    }
}
