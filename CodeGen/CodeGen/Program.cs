using System;
using System.IO;
using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Translation;
using CodeGen.Validation;

namespace CodeGen
{
    internal static class Program
    {
        static void Main(string[] args)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("VueOne to IEC 61499 Mapper - Tuesday Demo");
            Console.ForegroundColor = ConsoleColor.White;

            try
            {
                var config = MapperConfig.Load();

                ProcessComponent(config.ActuatorXmlPath, config.ActuatorTemplatePath,
                                "Five_State_Actuator", config);

                ProcessComponent(config.SensorXmlPathHopper, config.SensorTemplatePath,
                                "Sensor_Bool", config);

                ProcessComponent(config.SensorXmlPathChecker, config.SensorTemplatePath,
                                "Sensor_Bool", config);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n✓ All components generated successfully!");
                Console.ForegroundColor = ConsoleColor.White;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n✗ ERROR: {ex.Message}");
                Console.ForegroundColor = ConsoleColor.White;
            }
        }

        private static void ProcessComponent(string xmlPath, string templatePath,
                                             string templateBaseName, MapperConfig config)
        {
            Console.WriteLine($"\n{new string('=', 60)}");
            Console.WriteLine($"Processing: {Path.GetFileName(xmlPath)}");
            Console.WriteLine($"Template: {Path.GetFileName(templatePath)}");
            Console.WriteLine(new string('=', 60));

            var xmlReader = new ControlXmlReader();
            var component = xmlReader.ReadComponent(xmlPath);
            Console.WriteLine($"Component: {component.Name} ({component.Type}, {component.States.Count} states)");

            var validator = new ComponentValidator();
            var result = validator.Validate(component);
            result.PrintToConsole();

            if (!result.IsValid)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Translation REJECTED");
                Console.ForegroundColor = ConsoleColor.White;
                return;
            }

            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException($"Template not found: {templatePath}");
            }

            var templateContent = File.ReadAllText(templatePath);
            var generator = new FBGenerator();
            var generatedFB = generator.GenerateFromTemplate(component, templateContent, templateBaseName);

            if (!generatedFB.IsValid)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Generation FAILED");
                Console.ForegroundColor = ConsoleColor.White;
                return;
            }

            Directory.CreateDirectory(config.OutputDirectory);

            var modifiedContent = generator.GetModifiedTemplateContent(component, templateContent, templateBaseName);
            File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.FbtFile), modifiedContent);
            File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.CompositeFile), generator.ResolveCompositeXml(templatePath));
            File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.DocFile), generator.GetDocXml(generatedFB.FBName));
            File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.MetaFile), generator.GetMetaXml(generatedFB.FBName, generatedFB.GUID));
            var copiedCompanions = generator.CopyCatCompanionFiles(templatePath, config.OutputDirectory, generatedFB.FBName);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Generated: {generatedFB.FBName}");
            Console.WriteLine($"✓ GUID: {generatedFB.GUID}");
            Console.WriteLine($"✓ Files: {generatedFB.FbtFile}, {generatedFB.CompositeFile}, {generatedFB.DocFile}, {generatedFB.MetaFile}");
            if (copiedCompanions.Count > 0)
            {
                Console.WriteLine($"✓ CAT Companions: {string.Join(", ", copiedCompanions)}");
            }
            Console.ForegroundColor = ConsoleColor.White;
        }
    }
}
