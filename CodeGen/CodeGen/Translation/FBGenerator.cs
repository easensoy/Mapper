using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeGen.Models;

namespace CodeGen.Translation
{
    public class FBGenerator
    {
        private static readonly string[] CatCompanionSuffixes =
        {
            ".cfg",
            "_CAT.offline.xml",
            "_CAT.opcua.xml",
            "_HMI.offline.xml",
            "_HMI.opcua.xml",
            ".dfbproj"
        };

        public GeneratedFB GenerateFromTemplate(VueOneComponent component, string templateContent, string templateName)
        {
            try
            {
                var doc = XDocument.Parse(templateContent);
                var fbType = doc.Root ?? throw new Exception("Invalid template XML");

                var componentToken = SanitizeToken(component.Name);
                var baseName = ResolveBaseName(templateName, fbType);
                var newName = $"{baseName}_{componentToken}";
                var deterministicGuid = BuildDeterministicGuid(newName);

                UpdateFBAttributes(fbType, newName, component.Name, deterministicGuid);

                return new GeneratedFB
                {
                    FBName = newName,
                    GUID = deterministicGuid,
                    ComponentName = component.Name,
                    FilePath = $"{newName}.fbt",
                    FbtFile = $"{newName}.fbt",
                    CompositeFile = $"{newName}.composite.offline.xml",
                    DocFile = $"{newName}.doc.xml",
                    MetaFile = $"{newName}.meta.xml",
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
            var fbType = doc.Root ?? throw new Exception("Invalid template XML");
            var fallbackTemplateName = fbType.Attribute("Name")?.Value ?? "Function_Block";
            return GetModifiedTemplateContent(component, templateContent, fallbackTemplateName);
        }

        public string GetModifiedTemplateContent(VueOneComponent component, string templateContent, string templateName)
        {
            var doc = XDocument.Parse(templateContent);
            var fbType = doc.Root ?? throw new Exception("Invalid template XML");

            var componentToken = SanitizeToken(component.Name);
            var baseName = ResolveBaseName(templateName, fbType);
            var newName = $"{baseName}_{componentToken}";
            var deterministicGuid = BuildDeterministicGuid(newName);

            UpdateFBAttributes(fbType, newName, component.Name, deterministicGuid);

            return doc.ToString();
        }

        public string GetCompositeXml()
        {
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
                <CompositeFBTypeCompileInfo>
                  <Signature />
                </CompositeFBTypeCompileInfo>";
        }

        public string ResolveCompositeXml(string templatePath)
        {
            var templateBaseName = Path.GetFileNameWithoutExtension(templatePath);
            var templateDir = Path.GetDirectoryName(templatePath) ?? string.Empty;
            var companionComposite = Path.Combine(templateDir, $"{templateBaseName}.composite.offline.xml");

            return File.Exists(companionComposite)
                ? File.ReadAllText(companionComposite)
                : GetCompositeXml();
        }

        public IReadOnlyList<string> CopyCatCompanionFiles(string templatePath, string outputDirectory, string generatedFbName)
        {
            var copiedFiles = new List<string>();
            var templateBaseName = Path.GetFileNameWithoutExtension(templatePath);
            var templateDir = Path.GetDirectoryName(templatePath) ?? string.Empty;

            foreach (var suffix in CatCompanionSuffixes)
            {
                var sourcePath = Path.Combine(templateDir, $"{templateBaseName}{suffix}");
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var destinationFileName = $"{generatedFbName}{suffix}";
                var destinationPath = Path.Combine(outputDirectory, destinationFileName);

                if (suffix.Equals(".cfg", StringComparison.OrdinalIgnoreCase))
                {
                    var cfgContent = File.ReadAllText(sourcePath);
                    cfgContent = cfgContent.Replace(templateBaseName, generatedFbName, StringComparison.Ordinal);
                    File.WriteAllText(destinationPath, cfgContent);
                }
                else
                {
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                }

                copiedFiles.Add(destinationFileName);
            }

            return copiedFiles;
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

        public string GetMetaXml(string fbName, string guid)
        {
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <FBTypeMetadata>
                  <Name>{fbName}</Name>
                  <Guid>{guid}</Guid>
                  <Version>1.0.0</Version>
                  <Classification>Generated/VueOneMapper</Classification>
                  <GeneratedAt>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</GeneratedAt>
                </FBTypeMetadata>";
        }

        private static string ResolveBaseName(string templateName, XElement fbType)
        {
            if (!string.IsNullOrWhiteSpace(templateName))
            {
                return templateName;
            }

            var fromTemplate = fbType.Attribute("Name")?.Value;
            if (!string.IsNullOrWhiteSpace(fromTemplate))
            {
                return fromTemplate;
            }

            return "Function_Block";
        }

        private static void UpdateFBAttributes(XElement fbType, string newName, string componentName, string guid)
        {
            fbType.SetAttributeValue("Name", newName);
            fbType.SetAttributeValue("GUID", guid);
            fbType.SetAttributeValue("Comment", $"Function Block for {componentName}");

            var versionInfo = fbType.Element("VersionInfo");
            if (versionInfo != null)
            {
                versionInfo.SetAttributeValue("Author", "alper_sensoy");
                versionInfo.SetAttributeValue("Date", DateTime.Now.ToString("M/d/yyyy"));
                versionInfo.SetAttributeValue("Remarks", $"Generated from VueOne component: {componentName}");
            }
        }

        private static string SanitizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Component";
            }

            var builder = new StringBuilder(value.Length);

            foreach (var ch in value)
            {
                builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }

            return builder.ToString();
        }

        private static string BuildDeterministicGuid(string value)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
            return new Guid(hash).ToString();
        }
    }
}
