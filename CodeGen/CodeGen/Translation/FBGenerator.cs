using System;
using System.Xml.Linq;
using CodeGen.Models;

namespace CodeGen.Translation
{
    public class FBGenerator
    {
        public GeneratedFB GenerateFromTemplate(VueOneComponent component, string templateContent, string templateName)
        {
            try
            {
                var doc = XDocument.Parse(templateContent);
                var fbType = doc.Root ?? throw new Exception("Invalid template XML");

                var baseName = fbType.Attribute("Name")?.Value ?? "Function_Block";
                var newName = $"{baseName}_{component.Name}";

                UpdateFBAttributes(fbType, newName, component.Name);

                return new GeneratedFB
                {
                    FBName = newName,
                    GUID = Guid.NewGuid().ToString(),
                    ComponentName = component.Name,
                    FilePath = $"{newName}.fbt",
                    FbtFile = $"{newName}.fbt",
                    CompositeFile = $"{newName}.composite.offline.xml",
                    DocFile = $"{newName}.doc.xml",
                    IsValid = true
                };
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n✗ ERROR during generation: {ex.Message}");
                Console.ForegroundColor = ConsoleColor.White;

                return new GeneratedFB { IsValid = false };
            }
        }

        public string GetModifiedTemplateContent(VueOneComponent component, string templateContent)
        {
            var doc = XDocument.Parse(templateContent);
            var fbType = doc.Root!;

            var baseName = fbType.Attribute("Name")?.Value ?? "Function_Block";
            var newName = $"{baseName}_{component.Name}";

            UpdateFBAttributes(fbType, newName, component.Name);

            return doc.ToString();
        }

        public string GetCompositeXml()
        {
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
                <CompositeFBTypeCompileInfo>
                  <Signature />
                </CompositeFBTypeCompileInfo>";
        }

        public string GetDocXml(string fbName)
        {
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <FBTypeDocumentation>
                  <Name>{fbName}</Name>
                  <Description />
                  <Author>alper_sensoy</Author>
                  <Date>{DateTime.Now:yyyy-MM-dd}</Date>
                  <Version>1.0</Version>
                </FBTypeDocumentation>";
        }

        private void UpdateFBAttributes(XElement fbType, string newName, string componentName)
        {
            fbType.SetAttributeValue("Name", newName);
            fbType.SetAttributeValue("GUID", Guid.NewGuid().ToString());
            fbType.SetAttributeValue("Comment", $"Function Block for {componentName}");

            var versionInfo = fbType.Element("VersionInfo");
            if (versionInfo != null)
            {
                versionInfo.SetAttributeValue("Author", "alper_sensoy");
                versionInfo.SetAttributeValue("Date", DateTime.Now.ToString("M/d/yyyy"));
                versionInfo.SetAttributeValue("Remarks", $"Generated from VueOne component: {componentName}");
            }
        }
    }
}
