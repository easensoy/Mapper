using System;
using System.Xml.Linq;
using VueOneMapper.Models;

namespace VueOneMapper.Translation
{
    /// <summary>
    /// Generates IEC 61499 Function Block files by modifying templates
    /// This is the core translation logic
    /// </summary>
    public class FBGenerator
    {
        public GeneratedFB GenerateFromTemplate(
            VueOneComponent component,
            string templateContent,
            string templateName)
        {
            try
            {
                // Parse template XML
                XDocument doc = XDocument.Parse(templateContent);
                XElement fbType = doc.Root;

                if (fbType == null || fbType.Name.LocalName != "FBType")
                {
                    throw new Exception("Template does not contain valid FBType element");
                }

                // 1. Change Name: Five_State_Actuator → Five_State_Actuator_Pusher
                string baseName = fbType.Attribute("Name")?.Value ?? "Five_State_Actuator";
                string newName = $"{baseName}_{component.Name}";
                fbType.SetAttributeValue("Name", newName);

                // 2. Generate new GUID (unique identifier for this FB)
                string newGuid = Guid.NewGuid().ToString();
                fbType.SetAttributeValue("GUID", newGuid);

                // 3. Update VersionInfo metadata
                XElement versionInfo = fbType.Element("VersionInfo");
                if (versionInfo != null)
                {
                    versionInfo.SetAttributeValue("Author", "VueOne_Mapper");
                    versionInfo.SetAttributeValue("Date", DateTime.Now.ToString("M/d/yyyy"));
                    versionInfo.SetAttributeValue("Remarks", $"Generated from VueOne component: {component.Name}");
                }

                // 4. Update Comment (optional)
                fbType.SetAttributeValue("Comment", $"Function Block for {component.Name}");

                // Generate output file name
                string outputFileName = $"{newName}.fbt";

                return new GeneratedFB
                {
                    FBName = newName,
                    GUID = newGuid,
                    ComponentName = component.Name,
                    FilePath = outputFileName,
                    IsValid = true
                };
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n✗ ERROR during generation: {ex.Message}");
                Console.ForegroundColor = ConsoleColor.White;

                return new GeneratedFB
                {
                    IsValid = false
                };
            }
        }

        public string GetModifiedTemplateContent(
            VueOneComponent component,
            string templateContent)
        {
            XDocument doc = XDocument.Parse(templateContent);
            XElement fbType = doc.Root;

            // Apply same modifications as above
            string baseName = fbType.Attribute("Name")?.Value ?? "Five_State_Actuator";
            string newName = $"{baseName}_{component.Name}";
            fbType.SetAttributeValue("Name", newName);

            string newGuid = Guid.NewGuid().ToString();
            fbType.SetAttributeValue("GUID", newGuid);

            XElement versionInfo = fbType.Element("VersionInfo");
            if (versionInfo != null)
            {
                versionInfo.SetAttributeValue("Author", "VueOne_Mapper");
                versionInfo.SetAttributeValue("Date", DateTime.Now.ToString("M/d/yyyy"));
                versionInfo.SetAttributeValue("Remarks", $"Generated from VueOne component: {component.Name}");
            }

            fbType.SetAttributeValue("Comment", $"Function Block for {component.Name}");

            return doc.ToString();
        }
    }
}