using CodeGen.IO;
using CodeGen.Validation;
using System;
using System.IO;
using VueOneMapper.IO;
using VueOneMapper.Mapping;
using VueOneMapper.Translation;
using VueOneMapper.Validation;

namespace VueOneMapper
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Clear();
            PrintHeader();

            // Configuration
            string controlXmlPath = "Control.xml";  // Your Pusher XML
            string templateDirectory = "Templates"; // Where five_state_actuator.fbt is
            string outputDirectory = "Output";      // Where generated files go

            try
            {
                // ============================================================
                // PHASE 1: Read VueOne Control.xml
                // ============================================================
                Console.WriteLine("\n[PHASE 1] Reading VueOne Control.xml...");
                Console.WriteLine(new string('-', 60));

                var xmlReader = new ControlXmlReader();
                var component = xmlReader.ReadComponent(controlXmlPath);

                Console.WriteLine($"Component Name: {component.Name}");
                Console.WriteLine($"Component Type: {component.Type}");
                Console.WriteLine($"State Count: {component.States.Count}");

                // ============================================================
                // PHASE 2: Validate Component (INPUT VALIDATION)
                // ============================================================
                Console.WriteLine("\n[PHASE 2] Validating VueOne Component...");
                Console.WriteLine(new string('-', 60));

                var validator = new ComponentValidator();
                var validationResult = validator.Validate(component);

                validationResult.PrintToConsole();

                if (!validationResult.IsValid)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Translation REJECTED due to validation errors.");
                    Console.ForegroundColor = ConsoleColor.White;
                    return;
                }

                // ============================================================
                // PHASE 3: Select Template
                // ============================================================
                Console.WriteLine("\n[PHASE 3] Selecting IEC 61499 Template...");
                Console.WriteLine(new string('-', 60));

                var templateSelector = new TemplateSelector();
                var template = templateSelector.SelectTemplate(component);

                if (template == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"✗ ERROR: No template found for {component.Type} with {component.States.Count} states");
                    Console.ForegroundColor = ConsoleColor.White;
                    return;
                }

                Console.WriteLine($"✓ Selected Template: {template.TemplateName}");
                Console.WriteLine($"✓ Expected States: {template.ExpectedStateCount}");

                // ============================================================
                // PHASE 4: Load Template
                // ============================================================
                Console.WriteLine("\n[PHASE 4] Loading Template File...");
                Console.WriteLine(new string('-', 60));

                var templateLoader = new TemplateLoader(templateDirectory);

                if (!templateLoader.TemplateExists(template.TemplateFilePath))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"✗ ERROR: Template file not found: {template.TemplateFilePath}");
                    Console.WriteLine($"Expected location: {Path.Combine(templateDirectory, template.TemplateFilePath)}");
                    Console.ForegroundColor = ConsoleColor.White;
                    return;
                }

                string templateContent = templateLoader.LoadTemplate(template.TemplateFilePath);
                Console.WriteLine($"✓ Template loaded ({templateContent.Length} characters)");

                // ============================================================
                // PHASE 5: Generate Function Block
                // ============================================================
                Console.WriteLine("\n[PHASE 5] Generating IEC 61499 Function Block...");
                Console.WriteLine(new string('-', 60));

                var generator = new FBGenerator();
                var generatedFB = generator.GenerateFromTemplate(component, templateContent, template.TemplateName);

                if (!generatedFB.IsValid)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("✗ Generation FAILED");
                    Console.ForegroundColor = ConsoleColor.White;
                    return;
                }

                Console.WriteLine($"✓ FB Name: {generatedFB.FBName}");
                Console.WriteLine($"✓ GUID: {generatedFB.GUID}");
                Console.WriteLine($"✓ Output File: {generatedFB.FilePath}");

                // ============================================================
                // PHASE 6: Write Output File
                // ============================================================
                Console.WriteLine("\n[PHASE 6] Writing Output File...");
                Console.WriteLine(new string('-', 60));

                var fbWriter = new FBWriter(outputDirectory);
                string modifiedContent = generator.GetModifiedTemplateContent(component, templateContent);
                fbWriter.WriteFile(generatedFB.FilePath, modifiedContent);

                string fullOutputPath = fbWriter.GetOutputPath(generatedFB.FilePath);
                Console.WriteLine($"✓ File written to: {fullOutputPath}");

                // ============================================================
                // SUCCESS SUMMARY
                // ============================================================
                PrintSuccess(component.Name, generatedFB.FilePath, fullOutputPath);

            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n✗ FATAL ERROR: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"   Inner: {ex.InnerException.Message}");
                }
                Console.ForegroundColor = ConsoleColor.White;
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
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

        static void PrintSuccess(string componentName, string fileName, string fullPath)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine("SUCCESS - Translation Complete!");
            Console.WriteLine(new string('=', 60));
            Console.ForegroundColor = ConsoleColor.White;

            Console.WriteLine($"\nComponent: {componentName}");
            Console.WriteLine($"Generated: {fileName}");
            Console.WriteLine($"Location: {fullPath}");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\nNext Steps:");
            Console.WriteLine("1. Copy this file to: SMC_Rig_Expo/Composite/");
            Console.WriteLine("2. Open EAE");
            Console.WriteLine("3. Right-click Solution → Refresh");
            Console.WriteLine("4. Check console for 'Build successful'");
            Console.ForegroundColor = ConsoleColor.White;
        }
    }
}