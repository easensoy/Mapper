using System;
using System.IO;
using System.Threading.Tasks;
using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Translation;
using CodeGen.Validation;
using CodeGen.Models;

namespace MapperUI.Services
{
    public class MapperService
    {
        private readonly MapperConfig _config;

        public MapperService()
        {
            _config = MapperConfig.Load();
        }

        public async Task<MapperResult> RunMapping()
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Process actuator
                    var actuatorResult = ProcessComponent(
                        _config.ActuatorXmlPath,
                        _config.ActuatorTemplatePath,
                        "Five_State_Actuator");

                    if (!actuatorResult.Success)
                    {
                        return actuatorResult;
                    }

                    // Process sensors
                    var hopperResult = ProcessComponent(_config.SensorXmlPathHopper,
                                   _config.SensorTemplatePath,
                                   "Sensor_Bool");

                    if (!hopperResult.Success)
                    {
                        return hopperResult;
                    }

                    var checkerResult = ProcessComponent(_config.SensorXmlPathChecker,
                                   _config.SensorTemplatePath,
                                   "Sensor_Bool");

                    if (!checkerResult.Success)
                    {
                        return checkerResult;
                    }

                    return actuatorResult;
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

        private MapperResult ProcessComponent(string xmlPath, string templatePath, string templateBaseName)
        {
            var xmlReader = new ControlXmlReader();
            var component = xmlReader.ReadComponent(xmlPath);

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
            var generatedFB = generator.GenerateFromTemplate(component, templateContent, templateBaseName);

            Directory.CreateDirectory(_config.OutputDirectory);

            var modifiedContent = generator.GetModifiedTemplateContent(component, templateContent);
            var outputPath = Path.Combine(_config.OutputDirectory, generatedFB.FbtFile);
            File.WriteAllText(outputPath, modifiedContent);
            File.WriteAllText(Path.Combine(_config.OutputDirectory, generatedFB.CompositeFile), generator.GetCompositeXml());
            File.WriteAllText(Path.Combine(_config.OutputDirectory, generatedFB.DocFile), generator.GetDocXml(generatedFB.FBName));

            if (Directory.Exists(_config.EAEDeployPath))
            {
                File.Copy(outputPath, Path.Combine(_config.EAEDeployPath, generatedFB.FbtFile), true);
            }

            return new MapperResult
            {
                Success = true,
                ComponentName = component.Name,
                GeneratedFB = generatedFB,
                OutputPath = _config.OutputDirectory,
                DeployPath = _config.EAEDeployPath
            };
        }
    }
}
