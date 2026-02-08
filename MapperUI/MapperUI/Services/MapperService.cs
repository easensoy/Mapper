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
                    var xmlReader = new ControlXmlReader();
                    var component = xmlReader.ReadComponent(_config.ControlXmlPath);

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

                    var templateContent = File.ReadAllText(_config.TemplatePath);
                    var generator = new FBGenerator();
                    var generatedFB = generator.GenerateFromTemplate(component, templateContent, "Five_State_Actuator");

                    Directory.CreateDirectory(_config.OutputDirectory);

                    var modifiedContent = generator.GetModifiedTemplateContent(component, templateContent);
                    File.WriteAllText(Path.Combine(_config.OutputDirectory, generatedFB.FbtFile), modifiedContent);
                    File.WriteAllText(Path.Combine(_config.OutputDirectory, generatedFB.CompositeFile), generator.GetCompositeXml());
                    File.WriteAllText(Path.Combine(_config.OutputDirectory, generatedFB.DocFile), generator.GetDocXml(generatedFB.FBName));

                    if (Directory.Exists(_config.EAEDeployPath))
                    {
                        File.Copy(Path.Combine(_config.OutputDirectory, generatedFB.FbtFile),
                                  Path.Combine(_config.EAEDeployPath, generatedFB.FbtFile), true);
                        File.Copy(Path.Combine(_config.OutputDirectory, generatedFB.CompositeFile),
                                  Path.Combine(_config.EAEDeployPath, generatedFB.CompositeFile), true);
                        File.Copy(Path.Combine(_config.OutputDirectory, generatedFB.DocFile),
                                  Path.Combine(_config.EAEDeployPath, generatedFB.DocFile), true);
                    }

                    return new MapperResult
                    {
                        Success = true,
                        ComponentName = component.Name,
                        GeneratedFB = generatedFB,
                        ValidationResult = validationResult,
                        OutputPath = Path.Combine(_config.OutputDirectory, generatedFB.FbtFile),
                        DeployPath = Path.Combine(_config.EAEDeployPath, generatedFB.FbtFile)
                    };
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
    }

    public class MapperResult
    {
        public bool Success { get; set; }
        public string ComponentName { get; set; }
        public GeneratedFB GeneratedFB { get; set; }
        public ValidationResult ValidationResult { get; set; }
        public string OutputPath { get; set; }
        public string DeployPath { get; set; }
        public string ErrorMessage { get; set; }
    }
}