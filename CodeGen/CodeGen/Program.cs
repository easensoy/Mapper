using System;
using System.IO;
using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Translation;
using CodeGen.Validation;

namespace CodeGen
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Clear();
            PrintHeader();

            try
            {
                var config = LoadConfiguration();
                var component = ReadComponent(config.ControlXmlPath);
                var validationResult = ValidateComponent(component);

                if (!validationResult.IsValid)
                {
                    PrintError("Translation REJECTED due to validation errors.");
                    return;
                }

                var template = SelectTemplate(component);
                if (template == null)
                {
                    PrintError($"No template found for {component.Type} with {component.States.Count} states");
                    return;
                }

                var templateContent = LoadTemplate(config.TemplateDirectory, template);
                var generatedFB = GenerateFB(component, templateContent, template.TemplateName);

                if (!generatedFB.IsValid)
                {
                    PrintError("Generation FAILED");
                    return;
                }

                var outputPath = WriteOutput(config.OutputDirectory, generatedFB, component, templateContent);
                DeployToEAE(config.EAEProjectPath, generatedFB.FilePath, outputPath);

                PrintSuccess(component.Name, generatedFB.FilePath, outputPath, config.EAEProjectPath);
            }
            catch (Exception ex)
            {
                PrintError($"FATAL ERROR: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"   Inner: {ex.InnerException.Message}");
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        static MapperConfig LoadConfiguration()
        {
            PrintPhase("CONFIGURATION", "Loading config.json...");
            var config = MapperConfig.Load();
            Console.WriteLine($"✓ Control.xml: {config.ControlXmlPath}");
            Console.WriteLine($"✓ Template Dir: {config.TemplateDirectory}");
            Console.WriteLine($"✓ Output Dir: {config.OutputDirectory}");
            if (!string.IsNullOrEmpty(config.EAEProjectPath))
                Console.WriteLine($"✓ EAE Project: {config.EAEProjectPath}");
            return config;
        }

        static Models.VueOneComponent ReadComponent(string xmlPath)
        {
            PrintPhase("PHASE 1", "Reading VueOne Control.xml...");

            if (!File.Exists(xmlPath))
                throw new FileNotFoundException($"Control.xml not found: {xmlPath}");

            var xmlReader = new ControlXmlReader();
            var component = xmlReader.ReadComponent(xmlPath);

            Console.WriteLine($"Component Name: {component.Name}");
            Console.WriteLine($"Component Type: {component.Type}");
            Console.WriteLine($"State Count: {component.States.Count}");

            return component;
        }

        static ValidationResult ValidateComponent(Models.VueOneComponent component)
        {
            PrintPhase("PHASE 2", "Validating VueOne Component...");
            var validator = new ComponentValidator();
            var result = validator.Validate(component);
            result.PrintToConsole();
            return result;
        }

        static Models.FBTemplate? SelectTemplate(Models.VueOneComponent component)
        {
            PrintPhase("PHASE 3", "Selecting IEC 61499 Template...");
            var selector = new TemplateSelector();
            var template = selector.SelectTemplate(component);

            if (template != null)
            {
                Console.WriteLine($"✓ Selected Template: {template.TemplateName}");
                Console.WriteLine($"✓ Expected States: {template.ExpectedStateCount}");
            }

            return template;
        }

        static string LoadTemplate(string templateDir, Models.FBTemplate template)
        {
            PrintPhase("PHASE 4", "Loading Template File...");
            var loader = new TemplateLoader(templateDir);

            if (!loader.TemplateExists(template.TemplateFilePath))
                throw new FileNotFoundException($"Template not found: {Path.Combine(templateDir, template.TemplateFilePath)}");

            var content = loader.LoadTemplate(template.TemplateFilePath);
            Console.WriteLine($"✓ Template loaded ({content.Length} characters)");
            return content;
        }

        static Models.GeneratedFB GenerateFB(Models.VueOneComponent component, string templateContent, string templateName)
        {
            PrintPhase("PHASE 5", "Generating IEC 61499 Function Block...");
            var generator = new FBGenerator();
            var fb = generator.GenerateFromTemplate(component, templateContent, templateName);

            if (fb.IsValid)
            {
                Console.WriteLine($"✓ FB Name: {fb.FBName}");
                Console.WriteLine($"✓ GUID: {fb.GUID}");
                Console.WriteLine($"✓ Output File: {fb.FilePath}");
            }

            return fb;
        }

        static string WriteOutput(string outputDir, Models.GeneratedFB fb, Models.VueOneComponent component, string templateContent)
        {
            PrintPhase("PHASE 6", "Writing Output File...");
            var writer = new FBWriter(outputDir);
            var generator = new FBGenerator();
            var modifiedContent = generator.GetModifiedTemplateContent(component, templateContent);

            writer.WriteFile(fb.FilePath, modifiedContent);
            var fullPath = writer.GetOutputPath(fb.FilePath);
            Console.WriteLine($"✓ File written to: {fullPath}");

            return fullPath;
        }

        static void DeployToEAE(string eaePath, string fileName, string sourcePath)
        {
            if (string.IsNullOrEmpty(eaePath) || !Directory.Exists(eaePath)) return;

            PrintPhase("DEPLOYMENT", "Copying to EAE Project...");
            var destination = Path.Combine(eaePath, fileName);
            File.Copy(sourcePath, destination, overwrite: true);
            Console.WriteLine($"✓ Copied to: {destination}");
        }

        static void PrintHeader()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(new string('=', 60));
            Console.WriteLine("       VueOne to IEC 61499 Mapper");
            Console.WriteLine("       Tuesday Demonstration Build");
            Console.WriteLine(new string('=', 60));
            Console.ForegroundColor = ConsoleColor.White;
        }

        static void PrintPhase(string phase, string description)
        {
            Console.WriteLine($"\n[{phase}] {description}");
            Console.WriteLine(new string('-', 60));
        }

        static void PrintError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ {message}");
            Console.ForegroundColor = ConsoleColor.White;
        }

        static void PrintSuccess(string componentName, string fileName, string fullPath, string eaePath)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("SUCCESS - Translation Complete!");
            Console.WriteLine(new string('=', 60));
            Console.ForegroundColor = ConsoleColor.White;

            Console.WriteLine($"\nComponent: {componentName}");
            Console.WriteLine($"Generated: {fileName}");
            Console.WriteLine($"Location: {fullPath}");

            if (!string.IsNullOrEmpty(eaePath))
                Console.WriteLine($"Deployed to EAE: {Path.Combine(eaePath, fileName)}");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nNext Steps:");
            Console.WriteLine("1. Open EAE");
            Console.WriteLine("2. Right-click Solution → Refresh");
            Console.WriteLine("3. Check console for 'Build successful'");
            Console.WriteLine("4. Find your FB in Composite tree");
            Console.ForegroundColor = ConsoleColor.White;
        }
    }
}