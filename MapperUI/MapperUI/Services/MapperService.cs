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
                    // -------------------------------------------------
                    // 1. Process actuator FIRST (primary generation)
                    // -------------------------------------------------
                    var actuatorResult = ProcessComponent(
                        _config.ActuatorXmlPath,
                        _config.ActuatorTemplatePath,
                        "Five_State_Actuator");

                    if (!actuatorResult.Success)
                    {
                        return actuatorResult;
                    }

                    // -------------------------------------------------
                    // 2. Process Hopper sensor (secondary)
                    // -------------------------------------------------
                    var hopperResult = ProcessComponent(
                        _config.SensorXmlPathHopper,
                        _config.SensorTemplatePath,
                        "Sensor_Bool");

                    if (!hopperResult.Success)
                    {
                        return hopperResult;
                    }

                    // -------------------------------------------------
                    // 3. Process Checker sensor (secondary)
                    // -------------------------------------------------
                    var checkerResult = ProcessComponent(
                        _config.SensorXmlPathChecker,
                        _config.SensorTemplatePath,
                        "Sensor_Bool");

                    if (!checkerResult.Success)
                    {
                        return checkerResult;
                    }

                    // -------------------------------------------------
                    // 4. RETURN actuator result (contains GeneratedFB)
                    // -------------------------------------------------
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

        private MapperResult ProcessComponent(
            string xmlPath,
            string templatePath,
            string templateBaseName)
        {
            // ------------------------------
            // Read VueOne Control.xml
            // ------------------------------
            var xmlReader = new ControlXmlReader();
            var component = xmlReader.ReadComponent(xmlPath);

            // ------------------------------
            // Validate component semantics
            // ------------------------------
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

            // ------------------------------
            // Load template
            // ------------------------------
            var templateContent = File.ReadAllText(templatePath);

            // ------------------------------
            // Generate FB artifacts
            // ------------------------------
            var generator = new FBGenerator();
            var generatedFB = generator.GenerateFromTemplate(
                component,
                templateContent,
                templateBaseName);

            // ------------------------------
            // Write output files
            // ------------------------------
            Directory.CreateDirectory(_config.OutputDirectory);

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

            // ------------------------------
            // Optional deploy to EAE library
            // ------------------------------
            if (Directory.Exists(_config.EAEDeployPath))
            {
                File.Copy(
                    fbtPath,
                    Path.Combine(_config.EAEDeployPath, generatedFB.FbtFile),
                    overwrite: true);
            }

            // ------------------------------
            // Return full result
            // ------------------------------
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
