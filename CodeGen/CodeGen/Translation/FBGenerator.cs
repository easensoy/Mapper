// CodeGen/CodeGen/Translation/FBGenerator.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            "_HMI.meta.xml",
            "_HMI.fbt"
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

                UpdateFBAttributes(fbType, newName, deterministicGuid);

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
                Console.ResetColor();
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

            UpdateFBAttributes(fbType, newName, deterministicGuid);

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
                    continue;

                var destinationFileName = $"{generatedFbName}{suffix}";
                var destinationPath = Path.Combine(outputDirectory, destinationFileName);

                if (suffix.Equals(".cfg", StringComparison.OrdinalIgnoreCase))
                {
                    var cfgContent = GenerateCfgForInstance(sourcePath, templateBaseName, generatedFbName);
                    File.WriteAllText(destinationPath, cfgContent);
                }
                else if (suffix.Equals("_HMI.fbt", StringComparison.OrdinalIgnoreCase))
                {
                    var hmiContent = File.ReadAllText(sourcePath);
                    var hmiUpdated = UpdateHmiFbtContent(hmiContent, templateBaseName, generatedFbName);
                    File.WriteAllText(destinationPath, hmiUpdated);
                }
                else
                {
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                }

                copiedFiles.Add(destinationFileName);
            }

            return copiedFiles;
        }

        private static string GenerateCfgForInstance(string sourceCfgPath, string templateBaseName, string generatedFbName)
        {
            var doc = XDocument.Load(sourceCfgPath);
            var root = doc.Root ?? throw new Exception($"Invalid .cfg XML: {sourceCfgPath}");
            XNamespace ns = root.GetDefaultNamespace();

            var nameAttr = root.Attribute("Name");
            if (nameAttr != null)
                nameAttr.Value = generatedFbName;

            var pluginElements = root.Elements(ns + "Plugin")
                .Concat(root.Elements("Plugin"));

            foreach (var plugin in pluginElements)
            {
                var valAttr = plugin.Attribute("Value");
                if (valAttr == null) continue;

                var flatFileName = Path.GetFileName(valAttr.Value);
                valAttr.Value = flatFileName.Replace(templateBaseName, generatedFbName, StringComparison.Ordinal);
            }

            return doc.ToString();
        }

        private static string UpdateHmiFbtContent(string hmiContent, string templateBaseName, string generatedFbName)
        {
            var doc = XDocument.Parse(hmiContent);
            var root = doc.Root;
            if (root == null)
                return hmiContent;

            var hmiName = $"{generatedFbName}_HMI";
            root.SetAttributeValue("Name", hmiName);

            var guidAttr = root.Attribute("GUID");
            if (guidAttr != null)
                guidAttr.Value = BuildDeterministicGuid(hmiName);

            foreach (var attr in root.Descendants().Attributes())
            {
                if (string.IsNullOrWhiteSpace(attr.Value))
                    continue;

                attr.Value = attr.Value.Replace(templateBaseName, generatedFbName, StringComparison.Ordinal);
            }

            return doc.ToString();
        }

        public string GetDocXml(string fbName)
        {
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <FBTypeDocumentation>
                  <n>{fbName}</n>
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
                  <n>{fbName}</n>
                  <Guid>{guid}</Guid>
                  <Version>1.0.0</Version>
                  <Classification>Generated/VueOneMapper</Classification>
                  <GeneratedAt>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</GeneratedAt>
                </FBTypeMetadata>";
        }

        private static string ResolveBaseName(string templateName, XElement fbType)
        {
            if (!string.IsNullOrWhiteSpace(templateName))
                return templateName;

            var fromTemplate = fbType.Attribute("Name")?.Value;
            if (!string.IsNullOrWhiteSpace(fromTemplate))
                return fromTemplate;

            return "Function_Block";
        }

        private static string SanitizeToken(string name) =>
            new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

        private static void UpdateFBAttributes(XElement fbType, string newName, string guid)
        {
            fbType.SetAttributeValue("Name", newName);
            fbType.SetAttributeValue("GUID", guid);
        }

        private static string BuildDeterministicGuid(string name)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(name));
            var b = hash;
            return $"{b[0]:x2}{b[1]:x2}{b[2]:x2}{b[3]:x2}-" +
                   $"{b[4]:x2}{b[5]:x2}-" +
                   $"{b[6]:x2}{b[7]:x2}-" +
                   $"{b[8]:x2}{b[9]:x2}-" +
                   $"{b[10]:x2}{b[11]:x2}{b[12]:x2}{b[13]:x2}{b[14]:x2}{b[15]:x2}";
        }
    }
}