// MapperUI/MapperUI/Services/PusherFBGenerator.cs
// ─────────────────────────────────────────────────────────────────────────────
// Generates a minimal EAE-loadable project folder containing:
//   - Five_State_Actuator_CAT/ (copied verbatim from ActuatorTemplatePath)
//   - IEC61499.dfbproj (registers the CAT type folder)
//   - System/ with a .syslay declaring one Pusher FB instance
//
// This is Wajid's "fundamental check" task:
//   You produce the folder → Alex opens it in EAE → confirms it loads.
//
// No new .fbt type is created. The CAT folder is shared, not cloned.
// The Pusher identity lives only in the .syslay instance (Name + actuator_name).
// ─────────────────────────────────────────────────────────────────────────────

using CodeGen.Configuration;
using System;
using System.IO;
using System.Text;

namespace MapperUI.Services
{
    public static class PusherFBGenerator
    {
        private const string CatName = "Five_State_Actuator_CAT";

        // Fixed GUIDs matching the FiveAct2Sens reference project structure.
        // EAE is fine with all-zeros GUIDs in a minimal validation project.
        private const string SystemGuid = "00000000-0000-0000-0000-000000000000";
        private const string AppGuid = "00000000-0000-0000-0000-000000000001";
        private const string LayerGuid = "00000000-0000-0000-0000-000000000000";
        private const string DevGuid = "00000000-0000-0000-0000-000000000002";

        // Pusher FB instance ID in the syslay — fixed, arbitrary, valid hex
        private const string PusherFbId = "8717D1B6C68FFDEB";

        /// <summary>
        /// Generates the Pusher validation folder.
        /// Returns a human-readable result message.
        /// Throws on unrecoverable errors (caller wraps in try/catch).
        /// </summary>
        public static string Generate(MapperConfig cfg)
        {
            // ── 1. Validate config ────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(cfg.ActuatorTemplatePath))
                throw new InvalidOperationException(
                    "ActuatorTemplatePath is empty in mapper_config.json.\n" +
                    "Set it to the full path of Five_State_Actuator_CAT.fbt in your EAE project.");

            if (!File.Exists(cfg.ActuatorTemplatePath))
                throw new FileNotFoundException(
                    $"ActuatorTemplatePath not found:\n{cfg.ActuatorTemplatePath}");

            string? templateCatDir = Path.GetDirectoryName(cfg.ActuatorTemplatePath);
            if (templateCatDir == null)
                throw new InvalidOperationException("Cannot determine CAT template folder.");

            // ── 2. Create output root ─────────────────────────────────────────
            string outputRoot = string.IsNullOrWhiteSpace(cfg.OutputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "Output")
                : cfg.OutputDirectory;

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string projectDir = Path.Combine(outputRoot, $"PusherValidation_{timestamp}");
            string iec61499Dir = Path.Combine(projectDir, "IEC61499");
            Directory.CreateDirectory(iec61499Dir);

            MapperLogger.Info($"[PusherFB] Output dir : {projectDir}");

            // ── 3. Copy Five_State_Actuator_CAT folder ────────────────────────
            string targetCatDir = Path.Combine(iec61499Dir, CatName);
            Directory.CreateDirectory(targetCatDir);

            int copied = 0, skipped = 0;
            foreach (string srcFile in Directory.GetFiles(templateCatDir, "*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(srcFile);
                string destFile = Path.Combine(targetCatDir, fileName);
                if (File.Exists(destFile)) { skipped++; continue; }
                File.Copy(srcFile, destFile, overwrite: false);
                MapperLogger.Info($"[PusherFB]   Copied  {fileName}");
                copied++;
            }

            // ── 4. Generate IEC61499.dfbproj ──────────────────────────────────
            string dfbprojPath = Path.Combine(iec61499Dir, "IEC61499.dfbproj");
            File.WriteAllText(dfbprojPath, GenerateDfbproj(), Encoding.UTF8);
            MapperLogger.Info("[PusherFB]   Generated IEC61499.dfbproj");

            // ── 5. Build System folder structure ──────────────────────────────
            //
            //  IEC61499/System/
            //    <SystemGuid>.system
            //    <SystemGuid>/
            //      <AppGuid>.sysapp
            //      <AppGuid>/
            //        <LayerGuid>.syslay   ← contains Pusher FB instance
            //        <LayerGuid>/
            //          offline.xml
            //      <DevGuid>.sysdev
            //      <DevGuid>/
            //        <LayerGuid>.sysres   ← stub, Alex wires manually
            //        <LayerGuid>/
            //          symlink.xml        ← empty stub

            string systemDir = Path.Combine(iec61499Dir, "System");
            string systemRoot = Path.Combine(systemDir, SystemGuid);
            string appDir = Path.Combine(systemRoot, AppGuid);
            string layerDir = Path.Combine(appDir, LayerGuid);
            string devDir = Path.Combine(systemRoot, DevGuid);
            string resDir = Path.Combine(devDir, LayerGuid);

            Directory.CreateDirectory(systemDir);
            Directory.CreateDirectory(systemRoot);
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(layerDir);
            Directory.CreateDirectory(devDir);
            Directory.CreateDirectory(resDir);

            // .system
            File.WriteAllText(
                Path.Combine(systemDir, $"{SystemGuid}.system"),
                GenerateSystemFile(), Encoding.UTF8);

            // .sysapp
            File.WriteAllText(
                Path.Combine(systemRoot, $"{AppGuid}.sysapp"),
                GenerateSysapp(), Encoding.UTF8);

            // .syslay  ← THE KEY FILE: declares Pusher as a Five_State_Actuator_CAT instance
            string syslayPath = Path.Combine(appDir, $"{LayerGuid}.syslay");
            File.WriteAllText(syslayPath, GenerateSyslay(), Encoding.UTF8);
            MapperLogger.Info($"[PusherFB]   Generated .syslay at {syslayPath}");

            // offline.xml (EAE expects this folder to exist)
            File.WriteAllText(
                Path.Combine(layerDir, "offline.xml"),
                GenerateOfflineXml(), Encoding.UTF8);

            // .sysdev
            File.WriteAllText(
                Path.Combine(systemRoot, $"{DevGuid}.sysdev"),
                GenerateSysdev(), Encoding.UTF8);

            // .sysres (minimal stub — Alex wires manually)
            File.WriteAllText(
                Path.Combine(devDir, $"{LayerGuid}.sysres"),
                GenerateSysres(), Encoding.UTF8);

            // symlink.xml (empty stub)
            File.WriteAllText(
                Path.Combine(resDir, "symlink.xml"),
                GenerateSymlinkStub(), Encoding.UTF8);

            MapperLogger.Info("[PusherFB]   Generated System/ structure");

            // ── 6. Also copy to EAEDeployPath if configured ───────────────────
            bool deployed = false;
            if (!string.IsNullOrWhiteSpace(cfg.EAEDeployPath) &&
                Directory.Exists(cfg.EAEDeployPath))
            {
                try
                {
                    string deployTarget = Path.Combine(cfg.EAEDeployPath, $"PusherValidation_{timestamp}");
                    CopyDirectory(projectDir, deployTarget);
                    MapperLogger.Info($"[PusherFB] Also deployed to EAEDeployPath: {deployTarget}");
                    deployed = true;
                }
                catch (Exception ex)
                {
                    MapperLogger.Warn($"[PusherFB] EAEDeployPath copy failed (non-fatal): {ex.Message}");
                }
            }

            // ── 7. Summary ────────────────────────────────────────────────────
            var sb = new StringBuilder();
            sb.AppendLine("Pusher FB validation folder generated successfully.");
            sb.AppendLine();
            sb.AppendLine($"  Output folder : {projectDir}");
            sb.AppendLine($"  CAT files copied  : {copied}  (skipped: {skipped})");
            sb.AppendLine();
            sb.AppendLine("What Alex should do:");
            sb.AppendLine("  1. In EAE: File → Open Solution");
            sb.AppendLine("  2. Navigate to the output folder → select IEC61499.dfbproj");
            sb.AppendLine("  3. Confirm Pusher FB instance appears as Five_State_Actuator_CAT");
            sb.AppendLine("  4. Confirm ports: pst_event, pst_out, current_state_to_process");
            if (deployed)
                sb.AppendLine($"\n  Also copied to: {cfg.EAEDeployPath}");

            return sb.ToString();
        }

        // ── XML Generators ────────────────────────────────────────────────────

        private static string GenerateDfbproj() => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <PropertyGroup>
    <NxtVersion>24.1.0.0</NxtVersion>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include=""System\{SystemGuid}.system"" />
    <Compile Include=""System\{SystemGuid}\{AppGuid}.sysapp"" />
    <Compile Include=""System\{SystemGuid}\{AppGuid}\{LayerGuid}.syslay"" />
    <Compile Include=""System\{SystemGuid}\{DevGuid}.sysdev"" />
    <Compile Include=""System\{SystemGuid}\{DevGuid}\{LayerGuid}.sysres"" />
    <Content Include=""{CatName}\{CatName}.fbt"" />
    <Content Include=""{CatName}\{CatName}.cfg"" />
    <Content Include=""{CatName}\{CatName}.meta.xml"" />
    <Content Include=""{CatName}\{CatName}_CAT.offline.xml"" />
    <Content Include=""{CatName}\{CatName}_CAT.opcua.xml"" />
    <Content Include=""{CatName}\{CatName}_HMI.fbt"" />
  </ItemGroup>
</Project>";

        private static string GenerateSystemFile() => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<SystemConfiguration xmlns=""https://www.se.com/LibraryElements""
                     ID=""{SystemGuid}""
                     Name=""PusherValidation""
                     Comment=""Minimal validation project — Pusher FB check"">
  <Applications>
    <Application ID=""{AppGuid}"" Name=""APP1"" />
  </Applications>
  <Devices>
    <Device ID=""{DevGuid}"" Name=""LocalDevice"" />
  </Devices>
</SystemConfiguration>";

        private static string GenerateSysapp() => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Application xmlns=""https://www.se.com/LibraryElements""
             ID=""{AppGuid}""
             Name=""APP1""
             Comment="""" />";

        private static string GenerateSyslay() => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Layer xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
       ID=""{LayerGuid}""
       Name=""Default""
       Comment=""""
       IsDefault=""true""
       xmlns=""https://www.se.com/LibraryElements"">
  <SubAppNetwork>
    <FB ID=""{PusherFbId}""
        Name=""Pusher""
        Type=""Five_State_Actuator_CAT""
        Namespace=""Main""
        x=""1300""
        y=""600"">
      <Parameter Name=""actuator_name"" Value=""'pusher'"" />
    </FB>
    <EventConnections />
    <DataConnections />
    <AdapterConnections />
  </SubAppNetwork>
</Layer>";

        private static string GenerateOfflineXml() =>
            @"<?xml version=""1.0"" encoding=""utf-8""?>
<OfflineParameterModel xmlns:xsd=""http://www.w3.org/2001/XMLSchema""
                       xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
                       IsDefaultEventSelectionDialogsHidden=""0"" />";

        private static string GenerateSysdev() => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Device xmlns=""https://www.se.com/LibraryElements""
        ID=""{DevGuid}""
        Name=""LocalDevice""
        Comment="""">
  <Resources>
    <Resource ID=""{LayerGuid}"" Name=""RES0"" />
  </Resources>
</Device>";

        private static string GenerateSysres() => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Resource xmlns=""https://www.se.com/LibraryElements""
          ID=""{LayerGuid}""
          Name=""RES0""
          Comment="""">
  <FBNetwork>
    <FB ID=""{PusherFbId}""
        Name=""Pusher""
        Type=""Five_State_Actuator_CAT""
        Namespace=""Main""
        Mapping=""{PusherFbId}"">
      <Parameter Name=""actuator_name"" Value=""'pusher'"" />
    </FB>
    <EventConnections />
    <DataConnections />
  </FBNetwork>
</Resource>";

        private static string GenerateSymlinkStub() =>
            @"<?xml version=""1.0"" encoding=""utf-8""?>
<!-- symlink.xml stub — populate with hardware I/O paths manually in EAE -->
<SymbolicVariableConfiguration />";

        // ── Directory copy helper ─────────────────────────────────────────────
        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}