using System;
using System.IO;
using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Translation;
using CodeGen.Validation;

namespace CodeGen
{
    class Program
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
                Console.WriteLine($"\nControl.xml: {config.ControlXmlPath}");
                Console.WriteLine($"Template: {config.TemplatePath}");

                var xmlReader = new ControlXmlReader();
                var component = xmlReader.ReadComponent(config.ControlXmlPath);
                Console.WriteLine($"\nComponent: {component.Name} ({component.Type}, {component.States.Count} states)");

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

                if (!File.Exists(config.TemplatePath))
                    throw new FileNotFoundException($"Template not found: {config.TemplatePath}");

                var templateContent = File.ReadAllText(config.TemplatePath);
                Console.WriteLine($"\nTemplate loaded: {templateContent.Length} chars");

                var generator = new FBGenerator();
                var generatedFB = generator.GenerateFromTemplate(component, templateContent, "Five_State_Actuator");

                if (!generatedFB.IsValid)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Generation FAILED");
                    Console.ForegroundColor = ConsoleColor.White;
                    return;
                }

                Directory.CreateDirectory(config.OutputDirectory);

                var modifiedContent = generator.GetModifiedTemplateContent(component, templateContent);
                File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.FbtFile), modifiedContent);
                File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.CompositeFile), generator.GetCompositeXml());
                File.WriteAllText(Path.Combine(config.OutputDirectory, generatedFB.DocFile), generator.GetDocXml(generatedFB.FBName));

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n✓ Generated: {generatedFB.FBName}");
                Console.WriteLine($"✓ GUID: {generatedFB.GUID}");
                Console.WriteLine($"✓ Files: .fbt, .composite.offline.xml, .doc.xml");
                Console.ForegroundColor = ConsoleColor.White;

                if (Directory.Exists(config.EAEDeployPath))
                {
                    File.Copy(Path.Combine(config.OutputDirectory, generatedFB.FbtFile),
                              Path.Combine(config.EAEDeployPath, generatedFB.FbtFile), true);
                    File.Copy(Path.Combine(config.OutputDirectory, generatedFB.CompositeFile),
                              Path.Combine(config.EAEDeployPath, generatedFB.CompositeFile), true);
                    File.Copy(Path.Combine(config.OutputDirectory, generatedFB.DocFile),
                              Path.Combine(config.EAEDeployPath, generatedFB.DocFile), true);

                    Console.WriteLine($"✓ Deployed to: {config.EAEDeployPath}");
                }

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\nNext: Open EAE → Refresh → Check Build");
                Console.ForegroundColor = ConsoleColor.White;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nERROR: {ex.Message}");
                Console.ForegroundColor = ConsoleColor.White;
            }

            Console.WriteLine("\nPress any key...");
            Console.ReadKey();
        }
    }
}