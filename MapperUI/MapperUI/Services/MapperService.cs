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

namespace MapperUI.Services
{
    public class MapperService
    {
        public MapperResult GeneratePusherFB(
            VueOneComponent component,
            MapperConfig config,
            ValidationResult validationResult)
        {
            // ── 1. Validate template exists ───────────────────────────────────
            if (!File.Exists(config.ActuatorTemplatePath))
                return Fail(component.Name, validationResult,
                    $"Template not found:\n{config.ActuatorTemplatePath}\n\n" +
                    "Check ActuatorTemplatePath in mapper_config.json.");

            var templateContent = File.ReadAllText(config.ActuatorTemplatePath);
            var templateBaseName = Path.GetFileNameWithoutExtension(config.ActuatorTemplatePath);

            // ── 2. Generate FB ────────────────────────────────────────────────
            var generator = new FBGenerator();
            var generatedFB = generator.GenerateFromTemplate(
                component, templateContent, templateBaseName);

            if (!generatedFB.IsValid)
                return Fail(component.Name, validationResult, "FB generation failed.");

            MapperLogger.Info($"[PusherFB] Component    : {component.Name} ({component.Type}, {component.States.Count} states)");
            MapperLogger.Info($"[PusherFB] Template      : {templateBaseName}");
            MapperLogger.Info($"[PusherFB] Output dir    : {config.OutputDirectory}");
            MapperLogger.Info($"[PusherFB] FB name       : {generatedFB.FBName}");
            MapperLogger.Info($"[PusherFB] GUID          : {generatedFB.GUID}");

            // ── 3. Write to local Output\<FBName>\ ────────────────────────────
            var localSubDir = Path.Combine(config.OutputDirectory, generatedFB.FBName);
            Directory.CreateDirectory(localSubDir);

            var modifiedContent = generator.GetModifiedTemplateContent(
                component, templateContent, templateBaseName);

            WriteFile(localSubDir, generatedFB.FbtFile, modifiedContent);
            WriteFile(localSubDir, generatedFB.CompositeFile,
                generator.ResolveCompositeXml(config.ActuatorTemplatePath));
            WriteFile(localSubDir, generatedFB.DocFile,
                generator.GetDocXml(generatedFB.FBName));
            WriteFile(localSubDir, generatedFB.MetaFile,
                generator.GetMetaXml(generatedFB.FBName, generatedFB.GUID));

            // Copy _CAT.offline.xml, _CAT.opcua.xml, _HMI.offline.xml, _HMI.opcua.xml
            var copiedCompanions = generator.CopyCatCompanionFiles(
                config.ActuatorTemplatePath, localSubDir, generatedFB.FBName);
            foreach (var f in copiedCompanions)
                MapperLogger.Info($"[PusherFB] Copied      {f}");

            // ── 4. Deploy to <EAEDeployPath>\<FBName>\ ───────────────────────
            // EAEDeployPath must be the IEC61499 root — mapper creates the subfolder.
            if (string.IsNullOrWhiteSpace(config.EAEDeployPath) ||
                !Directory.Exists(config.EAEDeployPath))
            {
                MapperLogger.Warn(
                    $"[PusherFB] EAEDeployPath not found — skipping EAE deploy.\n" +
                    $"           Path: {config.EAEDeployPath}\n" +
                    $"           Set EAEDeployPath = IEC61499 root in mapper_config.json.");

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

            // Create <IEC61499>\<FBName>\ subfolder
            var deploySubDir = Path.Combine(config.EAEDeployPath, generatedFB.FBName);
            Directory.CreateDirectory(deploySubDir);

            CopyFile(localSubDir, deploySubDir, generatedFB.FbtFile);
            CopyFile(localSubDir, deploySubDir, generatedFB.CompositeFile);
            CopyFile(localSubDir, deploySubDir, generatedFB.DocFile);
            CopyFile(localSubDir, deploySubDir, generatedFB.MetaFile);
            foreach (var companion in copiedCompanions)
                CopyFile(localSubDir, deploySubDir, companion);

            MapperLogger.Info($"[PusherFB] Deployed to EAEDeployPath: {deploySubDir}");

            // ── 5. Find IEC61499.dfbproj in EAEDeployPath root ───────────────
            var dfbproj = Directory
                .GetFiles(config.EAEDeployPath, "*.dfbproj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (dfbproj == null)
            {
                MapperLogger.Warn(
                    "[PusherFB] IEC61499.dfbproj NOT found in EAEDeployPath.\n" +
                    "           Ensure EAEDeployPath is the IEC61499 folder (same level as .dfbproj).\n" +
                    "           EAE will NOT show Reload Solution.");
            }
            else
            {
                // ── 6. Register all Feeder files in dfbproj ──────────────────
                int added = RegisterInDfbproj(
                    dfbprojPath: dfbproj,
                    subfolderName: generatedFB.FBName,
                    generatedFB: generatedFB,
                    companions: copiedCompanions,
                    deploySubDir: deploySubDir);

                MapperLogger.Info($"[PusherFB] dfbproj     : {Path.GetFileName(dfbproj)}");
                MapperLogger.Info($"[PusherFB] dfbproj new entries : {added}" +
                                  $" (0 = already registered, no duplicates added)");

                // ── 7. Touch dfbproj → triggers EAE "Reload Solution" ────────
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

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void WriteFile(string dir, string fileName, string content)
        {
            File.WriteAllText(Path.Combine(dir, fileName), content, Encoding.UTF8);
            MapperLogger.Info($"[PusherFB] Written    {fileName}");
        }

        private static void CopyFile(string srcDir, string dstDir, string fileName)
        {
            var src = Path.Combine(srcDir, fileName);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(dstDir, fileName), overwrite: true);
        }

        /// <summary>
        /// Adds entries for all generated Feeder files to IEC61499.dfbproj.
        ///
        ///   .fbt  →  &lt;Compile Include="Five_State_Actuator_CAT_Feeder\filename.fbt" /&gt;
        ///   rest  →  &lt;None    Include="Five_State_Actuator_CAT_Feeder\filename.ext" /&gt;
        ///
        /// Idempotent: re-running never duplicates entries.
        /// Returns the count of NEW entries added.
        /// </summary>
        private static int RegisterInDfbproj(
            string dfbprojPath,
            string subfolderName,
            GeneratedFB generatedFB,
            IReadOnlyList<string> companions,
            string deploySubDir)
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();

            // Locate or create the <ItemGroup> that holds <Compile> entries (.fbt files)
            var compileGroup = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "Compile").Any());
            if (compileGroup == null)
            {
                compileGroup = new XElement(ns + "ItemGroup");
                xml.Root.Add(compileGroup);
            }

            // Locate or create the <ItemGroup> that holds <None> entries (companion files)
            var noneGroup = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "None").Any());
            if (noneGroup == null)
            {
                noneGroup = new XElement(ns + "ItemGroup");
                xml.Root.Add(noneGroup);
            }

            int adds = 0;

            bool AlreadyPresent(XElement group, XName tag, string path) =>
                group.Elements(tag).Any(e =>
                    string.Equals((string?)e.Attribute("Include"), path,
                        StringComparison.OrdinalIgnoreCase));

            void EnsureCompile(string rel, string iecType = null, string dependentUpon = null, string usage = null)
            {
                var existing = compileGroup.Elements(ns + "Compile")
                    .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), rel, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    existing = new XElement(ns + "Compile", new XAttribute("Include", rel));
                    compileGroup.Add(existing);
                    MapperLogger.Info($"[dfbproj]  + Compile  {rel}");
                    adds++;
                }

                UpsertChild(existing, "IEC61499Type", iecType);
                UpsertChild(existing, "DependentUpon", dependentUpon);
                UpsertChild(existing, "Usage", usage);
            }

            void EnsureNone(string rel, string dependentUpon = null, string plugin = null, string iecType = null)
            {
                var existing = noneGroup.Elements(ns + "None")
                    .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), rel, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    existing = new XElement(ns + "None", new XAttribute("Include", rel));
                    noneGroup.Add(existing);
                    MapperLogger.Info($"[dfbproj]  + None     {rel}");
                    adds++;
                }

                UpsertChild(existing, "DependentUpon", dependentUpon);
                UpsertChild(existing, "Plugin", plugin);
                UpsertChild(existing, "IEC61499Type", iecType);
            }

            void UpsertChild(XElement parent, string childName, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;

                var child = parent.Element(ns + childName);
                if (child == null)
                {
                    parent.Add(new XElement(ns + childName, value));
                    return;
                }

                child.Value = value;
            }

            // Register .fbt as Compile
            EnsureCompile($@"{subfolderName}\{generatedFB.FbtFile}", iecType: "CAT");

            // Register companion files as None
            EnsureNone($@"{subfolderName}\{generatedFB.CompositeFile}", dependentUpon: generatedFB.FbtFile);
            EnsureNone($@"{subfolderName}\{generatedFB.DocFile}", dependentUpon: generatedFB.FbtFile);
            EnsureNone($@"{subfolderName}\{generatedFB.MetaFile}", dependentUpon: generatedFB.FbtFile);

            // .cfg (generated by CopyCatCompanionFiles, lives in deploySubDir)
            var cfgName = $"{generatedFB.FBName}.cfg";
            if (File.Exists(Path.Combine(deploySubDir, cfgName)))
            {
                EnsureNone(
                    $@"{subfolderName}\{cfgName}",
                    dependentUpon: generatedFB.FbtFile,
                    iecType: "CAT");
            }

            // _CAT.offline.xml, _CAT.opcua.xml, _HMI.offline.xml, _HMI.opcua.xml
            foreach (var companion in companions)
            {
                if (companion.EndsWith("_CAT.offline.xml", StringComparison.OrdinalIgnoreCase) ||
                    companion.EndsWith("_HMI.offline.xml", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureNone(
                        $@"{subfolderName}\{companion}",
                        dependentUpon: generatedFB.FbtFile,
                        plugin: "OfflineParametrizationEditor",
                        iecType: "CAT_OFFLINE");
                    continue;
                }

                if (companion.EndsWith("_CAT.opcua.xml", StringComparison.OrdinalIgnoreCase) ||
                    companion.EndsWith("_HMI.opcua.xml", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureNone(
                        $@"{subfolderName}\{companion}",
                        dependentUpon: generatedFB.FbtFile,
                        plugin: "OPCUAConfigurator",
                        iecType: "CAT_OPCUA");
                    continue;
                }

                if (companion.EndsWith(".dfbproj", StringComparison.OrdinalIgnoreCase))
                    continue;

                EnsureNone($@"{subfolderName}\{companion}", dependentUpon: generatedFB.FbtFile);
            }

            // Save with UTF-8 BOM to match EAE's existing dfbproj encoding
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