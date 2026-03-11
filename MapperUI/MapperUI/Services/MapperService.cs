using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using CodeGen.Configuration;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Validation;

// Rewritten for deterministic CAT companion handling and dfbproj registration.
namespace MapperUI.Services
{
    public class MapperService
    {
        public MapperResult GeneratePusherFB(
            VueOneComponent component,
            MapperConfig config,
            ValidationResult validationResult)
        {
            if (component == null)
                return Fail("Unknown", validationResult, "Component is null.");

            if (config == null)
                return Fail(component.Name, validationResult, "Mapper config is null.");

            if (string.IsNullOrWhiteSpace(config.ActuatorTemplatePath) || !File.Exists(config.ActuatorTemplatePath))
            {
                return Fail(component.Name, validationResult,
                    $"Template not found:\n{config.ActuatorTemplatePath}\n\n" +
                    "Check ActuatorTemplatePath in mapper_config.json.");
            }

            var templateContent = File.ReadAllText(config.ActuatorTemplatePath);
            var templateBaseName = Path.GetFileNameWithoutExtension(config.ActuatorTemplatePath);

            var generator = new FBGenerator();
            var generatedFB = generator.GenerateFromTemplate(component, templateContent, templateBaseName);
            if (!generatedFB.IsValid)
                return Fail(component.Name, validationResult, "FB generation failed.");

            MapperLogger.Info($"[PusherFB] Component    : {component.Name} ({component.Type}, {component.States.Count} states)");
            MapperLogger.Info($"[PusherFB] Template      : {templateBaseName}");
            MapperLogger.Info($"[PusherFB] Output dir    : {config.OutputDirectory}");
            MapperLogger.Info($"[PusherFB] FB name       : {generatedFB.FBName}");
            MapperLogger.Info($"[PusherFB] GUID          : {generatedFB.GUID}");

            var outputRoot = string.IsNullOrWhiteSpace(config.OutputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "Output")
                : config.OutputDirectory;

            var localSubDir = Path.Combine(outputRoot, generatedFB.FBName);
            Directory.CreateDirectory(localSubDir);

            var modifiedContent = generator.GetModifiedTemplateContent(component, templateContent, templateBaseName);
            WriteFile(localSubDir, generatedFB.FbtFile, modifiedContent);
            WriteFile(localSubDir, generatedFB.CompositeFile, generator.ResolveCompositeXml(config.ActuatorTemplatePath));
            WriteFile(localSubDir, generatedFB.DocFile, generator.GetDocXml(generatedFB.FBName));
            WriteFile(localSubDir, generatedFB.MetaFile, generator.GetMetaXml(generatedFB.FBName, generatedFB.GUID));

            var copiedCompanions = generator.CopyCatCompanionFiles(config.ActuatorTemplatePath, localSubDir, generatedFB.FBName);
            foreach (var f in copiedCompanions)
                MapperLogger.Info($"[PusherFB] Copied      {f}");

            if (string.IsNullOrWhiteSpace(config.EAEDeployPath) || !Directory.Exists(config.EAEDeployPath))
            {
                MapperLogger.Warn(
                    $"[PusherFB] EAEDeployPath not found — skipping EAE deploy.\n" +
                    $"           Path: {config.EAEDeployPath}\n" +
                    "           Set EAEDeployPath = IEC61499 root in mapper_config.json.");

                return new MapperResult
                {
                    Success = true,
                    ComponentName = component.Name,
                    GeneratedFB = generatedFB,
                    OutputPath = localSubDir,
                    DeployPath = string.Empty,
                    ValidationResult = validationResult
                };
            }

            var deployRoot = NormalizeDeployRoot(config.EAEDeployPath, generatedFB.FBName);
            var deploySubDir = Path.Combine(deployRoot, generatedFB.FBName);
            Directory.CreateDirectory(deploySubDir);

            CopyFile(localSubDir, deploySubDir, generatedFB.FbtFile);
            CopyFile(localSubDir, deploySubDir, generatedFB.CompositeFile);
            CopyFile(localSubDir, deploySubDir, generatedFB.DocFile);
            CopyFile(localSubDir, deploySubDir, generatedFB.MetaFile);
            foreach (var companion in copiedCompanions)
                CopyFile(localSubDir, deploySubDir, companion);

            MapperLogger.Info($"[PusherFB] Deployed to EAEDeployPath: {deploySubDir}");

            var dfbproj = FindDfbproj(deployRoot);

            if (dfbproj == null)
            {
                MapperLogger.Warn(
                    "[PusherFB] IEC61499.dfbproj NOT found in EAEDeployPath.\n" +
                    "           Ensure EAEDeployPath is the IEC61499 folder (same level as .dfbproj).\n" +
                    "           EAE will NOT show Reload Solution.");
            }
            else
            {
                var added = RegisterInDfbproj(
                    dfbprojPath: dfbproj,
                    subfolderName: generatedFB.FBName,
                    generatedFB: generatedFB,
                    companions: copiedCompanions,
                    deploySubDir: deploySubDir);

                MapperLogger.Info($"[PusherFB] dfbproj     : {Path.GetFileName(dfbproj)}");
                MapperLogger.Info($"[PusherFB] dfbproj new entries : {added} (0 = already registered, no duplicates added)");

                File.SetLastWriteTime(dfbproj, DateTime.Now);
                MapperLogger.Touch($"Touched   {Path.GetFileName(dfbproj)}");
                MapperLogger.Info("EAE will show 'Reload Solution' — click Yes.");
            }

            MapperLogger.Info(
                $"Generated: {generatedFB.FBName}" +
                $"  Component: {component.Name}  ({component.States.Count} states)" +
                $"  GUID: {generatedFB.GUID}" +
                $"  Output folder: {localSubDir}" +
                $"  Files: {generatedFB.FbtFile}");

            return new MapperResult
            {
                Success = true,
                ComponentName = component.Name,
                GeneratedFB = generatedFB,
                OutputPath = localSubDir,
                DeployPath = deploySubDir,
                ValidationResult = validationResult
            };
        }

        private static void WriteFile(string dir, string fileName, string content)
        {
            File.WriteAllText(Path.Combine(dir, fileName), content, Encoding.UTF8);
            MapperLogger.Info($"[PusherFB] Written    {fileName}");
        }

        private static void CopyFile(string srcDir, string dstDir, string fileName)
        {
            var src = Path.Combine(srcDir, fileName);
            if (!File.Exists(src))
                return;

            File.Copy(src, Path.Combine(dstDir, fileName), overwrite: true);
        }

        private static int RegisterInDfbproj(
            string dfbprojPath,
            string subfolderName,
            GeneratedFB generatedFB,
            IReadOnlyList<string> companions,
            string deploySubDir)
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();

            var compileGroup = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "Compile").Any());
            if (compileGroup == null)
            {
                compileGroup = new XElement(ns + "ItemGroup");
                xml.Root.Add(compileGroup);
            }

            var noneGroup = xml.Descendants(ns + "ItemGroup").FirstOrDefault(g => g.Elements(ns + "None").Any());
            if (noneGroup == null)
            {
                noneGroup = new XElement(ns + "ItemGroup");
                xml.Root.Add(noneGroup);
            }

            var adds = 0;

            void UpsertChild(XElement parent, string childName, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var child = parent.Element(ns + childName);
                if (child == null)
                    parent.Add(new XElement(ns + childName, value));
                else
                    child.Value = value;
            }

            void EnsureCompile(string rel, string iecType = null, string dependentUpon = null, string usage = null)
            {
                var item = compileGroup.Elements(ns + "Compile")
                    .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), rel, StringComparison.OrdinalIgnoreCase));

                if (item == null)
                {
                    item = new XElement(ns + "Compile", new XAttribute("Include", rel));
                    compileGroup.Add(item);
                    adds++;
                    MapperLogger.Info($"[dfbproj]  + Compile  {rel}");
                }

                UpsertChild(item, "IEC61499Type", iecType);
                UpsertChild(item, "DependentUpon", dependentUpon);
                UpsertChild(item, "Usage", usage);
            }

            void EnsureNone(string rel, string dependentUpon = null, string plugin = null, string iecType = null)
            {
                var item = noneGroup.Elements(ns + "None")
                    .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), rel, StringComparison.OrdinalIgnoreCase));

                if (item == null)
                {
                    item = new XElement(ns + "None", new XAttribute("Include", rel));
                    noneGroup.Add(item);
                    adds++;
                    MapperLogger.Info($"[dfbproj]  + None     {rel}");
                }

                UpsertChild(item, "DependentUpon", dependentUpon);
                UpsertChild(item, "Plugin", plugin);
                UpsertChild(item, "IEC61499Type", iecType);
            }

            EnsureCompile($@"{subfolderName}\{generatedFB.FbtFile}", iecType: "CAT");

            EnsureNone($@"{subfolderName}\{generatedFB.CompositeFile}", dependentUpon: generatedFB.FbtFile);
            EnsureNone($@"{subfolderName}\{generatedFB.DocFile}", dependentUpon: generatedFB.FbtFile);
            EnsureNone($@"{subfolderName}\{generatedFB.MetaFile}", dependentUpon: generatedFB.FbtFile);

            var cfgName = $"{generatedFB.FBName}.cfg";
            if (File.Exists(Path.Combine(deploySubDir, cfgName)))
            {
                EnsureNone($@"{subfolderName}\{cfgName}", dependentUpon: generatedFB.FbtFile, iecType: "CAT");
            }

            foreach (var companion in companions)
            {
                var include = $@"{subfolderName}\{companion}";

                if (companion.EndsWith("_HMI.fbt", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureCompile(
                        include,
                        iecType: "CAT",
                        dependentUpon: $@"{subfolderName}\{generatedFB.FbtFile}",
                        usage: "Private");
                    continue;
                }

                if (companion.EndsWith("_HMI.meta.xml", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureNone(
                        include,
                        dependentUpon: companion.Replace(".meta.xml", ".fbt", StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                if (companion.EndsWith("_CAT.offline.xml", StringComparison.OrdinalIgnoreCase) ||
                    companion.EndsWith("_HMI.offline.xml", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureNone(include, generatedFB.FbtFile, "OfflineParametrizationEditor", "CAT_OFFLINE");
                    continue;
                }

                if (companion.EndsWith("_CAT.opcua.xml", StringComparison.OrdinalIgnoreCase) ||
                    companion.EndsWith("_HMI.opcua.xml", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureNone(include, generatedFB.FbtFile, "OPCUAConfigurator", "CAT_OPCUA");
                    continue;
                }

                EnsureNone(include, generatedFB.FbtFile);
            }

            var settings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            };

            using var writer = System.Xml.XmlWriter.Create(dfbprojPath, settings);
            xml.Save(writer);
            return adds;
        }

        private static string NormalizeDeployRoot(string configuredPath, string generatedFbName)
        {
            var fullPath = Path.GetFullPath(configuredPath);
            var leaf = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.Equals(leaf, generatedFbName, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(fullPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    MapperLogger.Warn($"[PusherFB] EAEDeployPath points to '{generatedFbName}' folder. Using parent IEC61499 root: {parent}");
                    return parent;
                }
            }

            return fullPath;
        }

        private static string FindDfbproj(string startDirectory)
        {
            var dir = new DirectoryInfo(startDirectory);
            while (dir != null)
            {
                var match = dir.GetFiles("*.dfbproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (match != null)
                    return match.FullName;

                dir = dir.Parent;
            }

            return null;
        }

        private static MapperResult Fail(string name, ValidationResult vr, string msg) =>
            new()
            {
                Success = false,
                ComponentName = name,
                ValidationResult = vr,
                ErrorMessage = msg
            };
    }
}