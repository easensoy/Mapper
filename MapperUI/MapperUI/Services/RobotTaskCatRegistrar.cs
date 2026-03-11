// MapperUI/MapperUI/Services/RobotTaskCatRegistrar.cs
// ─────────────────────────────────────────────────────────────────────────────
// Generates the Robot_Task_CAT type folder (all 11 companion files) plus the
// companion Robot_Task_Core.fbt (Basic FB) at the IEC61499 root, then
// registers every file in IEC61499.dfbproj.
//
// Template files are OPTIONAL:
//   - If cfg.RobotTemplatePath points to an existing file, all 11 CAT files
//     are copied from the template folder alongside it.
//   - If it doesn't exist (or is empty), built-in fallback generators are used
//     for every file — no template project needed at all.
//   - Same logic applies to cfg.RobotBasicTemplatePath / Robot_Task_Core.fbt.
//
// Called by MainForm.btnGenerateRobotWrapper_Click.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeGen.Configuration;

// ── UTF-8 WITHOUT BOM ─────────────────────────────────────────────────────────
// EAE's XML parser rejects files that start with a BOM (0xEF 0xBB 0xBF).
// System.Text.Encoding.UTF8 (the static property) emits a BOM.
// Use Utf8NoBom (defined in the class) everywhere instead.

namespace MapperUI.Services
{
    public static class RobotTaskCatRegistrar
    {
        // ── Encoding: NO BOM — EAE XML parser dies on BOM at position (0,0) ──────
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // ── Names ─────────────────────────────────────────────────────────────
        private const string CatName = "Robot_Task_CAT";
        private const string HmiName = "Robot_Task_CAT_HMI";
        private const string BasicFbName = "Robot_Task_Core";   // Basic FB at IEC61499 root

        // ── Entry point ───────────────────────────────────────────────────────

        /// <summary>
        /// Copies / generates Robot_Task_CAT files into the target project,
        /// copies / generates Robot_Task_Core.fbt at the IEC61499 root, then
        /// ensures every file is registered in IEC61499.dfbproj.
        /// Returns a human-readable result message.
        /// </summary>
        public static string Register(MapperConfig cfg, string dfbprojPath)
        {
            // ── 1. Basic guards ───────────────────────────────────────────────
            if (!File.Exists(dfbprojPath))
                throw new FileNotFoundException(
                    $"IEC61499.dfbproj not found:\n{dfbprojPath}");

            // ── 2. Derive paths ───────────────────────────────────────────────
            // Template dirs — may be empty strings if not configured
            string? templateCatDir = null;
            string? templateRootDir = null;

            if (!string.IsNullOrWhiteSpace(cfg.RobotTemplatePath) &&
                File.Exists(cfg.RobotTemplatePath))
            {
                templateCatDir = Path.GetDirectoryName(cfg.RobotTemplatePath);
                MapperLogger.Info($"[RobotCAT] Using template CAT dir  : {templateCatDir}");
            }
            else
            {
                MapperLogger.Info("[RobotCAT] RobotTemplatePath not found or empty — using fallback generators for all CAT files.");
            }

            if (!string.IsNullOrWhiteSpace(cfg.RobotBasicTemplatePath) &&
                File.Exists(cfg.RobotBasicTemplatePath))
            {
                templateRootDir = Path.GetDirectoryName(cfg.RobotBasicTemplatePath);
                MapperLogger.Info($"[RobotCAT] Using template root dir : {templateRootDir}");
            }
            else
            {
                MapperLogger.Info("[RobotCAT] RobotBasicTemplatePath not found or empty — will generate Robot_Task_Core.fbt from built-in template.");
            }

            var iec61499Dir = Path.GetDirectoryName(dfbprojPath)!;   // …/IEC61499/
            var targetCatDir = Path.Combine(iec61499Dir, CatName);

            Directory.CreateDirectory(targetCatDir);

            MapperLogger.Info($"[RobotCAT] Target IEC61499 dir : {iec61499Dir}");
            MapperLogger.Info($"[RobotCAT] Target CAT dir      : {targetCatDir}");

            // ── 3. Copy / generate CAT folder files ───────────────────────────
            int copied = 0;
            int skipped = 0;
            int generated = 0;

            // 3a. Robot_Task_CAT.fbt  — copy or generate composite wrapper
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{CatName}.fbt",
                GenerateCatFbt,
                ref copied, ref skipped, ref generated);

            // 3b. Robot_Task_CAT.meta.xml  (always empty)
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{CatName}.meta.xml",
                () => string.Empty,
                ref copied, ref skipped, ref generated);

            // 3c. Robot_Task_CAT.doc.xml
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{CatName}.doc.xml",
                GenerateDocXml,
                ref copied, ref skipped, ref generated);

            // 3d. Robot_Task_CAT.cfg  (XML-aware: update Name + Plugin paths)
            CopyOrGenerateCfg(templateCatDir, targetCatDir,
                ref copied, ref skipped, ref generated);

            // 3e. Robot_Task_CAT_CAT.offline.xml
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{CatName}_CAT.offline.xml",
                GenerateOfflineXml,
                ref copied, ref skipped, ref generated);

            // 3f. Robot_Task_CAT_CAT.opcua.xml
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{CatName}_CAT.opcua.xml",
                GenerateCatOpcuaXml,
                ref copied, ref skipped, ref generated);

            // 3g. Robot_Task_CAT_HMI.fbt
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{HmiName}.fbt",
                GenerateHmiFbt,
                ref copied, ref skipped, ref generated);

            // 3h. Robot_Task_CAT_HMI.meta.xml  (always empty)
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{HmiName}.meta.xml",
                () => string.Empty,
                ref copied, ref skipped, ref generated);

            // 3i. Robot_Task_CAT_HMI.doc.xml
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{HmiName}.doc.xml",
                GenerateDocXml,
                ref copied, ref skipped, ref generated);

            // 3j. Robot_Task_CAT_HMI.offline.xml
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{HmiName}.offline.xml",
                GenerateOfflineXml,
                ref copied, ref skipped, ref generated);

            // 3k. Robot_Task_CAT_HMI.opcua.xml
            CopyOrGenerate(templateCatDir, targetCatDir,
                $"{HmiName}.opcua.xml",
                GenerateHmiOpcuaXml,
                ref copied, ref skipped, ref generated);

            // ── 4. Copy / generate Robot_Task_Core.fbt at IEC61499 root ──────
            CopyOrGenerate(templateRootDir, iec61499Dir,
                $"{BasicFbName}.fbt",
                GenerateBasicFbt,
                ref copied, ref skipped, ref generated);

            MapperLogger.Info($"[RobotCAT] Robot_Task_Core.fbt placed at IEC61499 root.");

            // ── 5. Register all files in dfbproj ─────────────────────────────
            int registered = RegisterInDfbproj(dfbprojPath, iec61499Dir);

            var sb = new StringBuilder();
            sb.AppendLine($"Robot_Task_CAT registered successfully.");
            sb.AppendLine();
            sb.AppendLine($"  CAT folder   : {targetCatDir}");
            sb.AppendLine($"  Basic FB     : {Path.Combine(iec61499Dir, BasicFbName + ".fbt")}");
            sb.AppendLine();
            sb.AppendLine($"  Files copied    : {copied}");
            sb.AppendLine($"  Files generated : {generated}");
            sb.AppendLine($"  Files skipped   : {skipped} (already present)");
            sb.AppendLine($"  dfbproj entries : {registered} added (0 = all already present)");
            sb.AppendLine();
            sb.AppendLine("Switch to EAE and click Reload Solution.");

            return sb.ToString();
        }

        // ── File helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Copies fileName from templateDir if it exists there, otherwise runs
        /// the fallback generator. Skips if target already exists.
        /// templateDir may be null (no template available).
        /// </summary>
        private static void CopyOrGenerate(
            string? templateDir, string targetDir, string fileName,
            Func<string>? generator,
            ref int copied, ref int skipped, ref int generated)
        {
            var target = Path.Combine(targetDir, fileName);
            if (File.Exists(target)) { skipped++; return; }

            var source = templateDir != null ? Path.Combine(templateDir, fileName) : null;

            if (source != null && File.Exists(source))
            {
                File.Copy(source, target, overwrite: false);
                copied++;
                MapperLogger.Info($"[RobotCAT]  Copied    {fileName}");
            }
            else if (generator != null)
            {
                File.WriteAllText(target, generator(), Utf8NoBom);
                generated++;
                MapperLogger.Info($"[RobotCAT]  Generated {fileName}");
            }
            else
            {
                MapperLogger.Warn($"[RobotCAT]  WARN: {fileName} not in template dir and no generator — skipping.");
            }
        }

        /// <summary>
        /// Special handler for the .cfg file: copy if available (patching paths),
        /// otherwise generate from scratch.
        /// </summary>
        private static void CopyOrGenerateCfg(
            string? templateDir, string targetDir,
            ref int copied, ref int skipped, ref int generated)
        {
            var target = Path.Combine(targetDir, $"{CatName}.cfg");
            if (File.Exists(target)) { skipped++; return; }

            var source = templateDir != null
                ? Path.Combine(templateDir, $"{CatName}.cfg")
                : null;

            if (source != null && File.Exists(source))
            {
                PatchAndWriteCfg(source, target);
                copied++;
                MapperLogger.Info($"[RobotCAT]  Copied+patched {CatName}.cfg (UTF-8 no BOM)");
            }
            else
            {
                File.WriteAllText(target, GenerateCfgXml(), Utf8NoBom);
                generated++;
                MapperLogger.Info($"[RobotCAT]  Generated {CatName}.cfg (fallback, UTF-8 no BOM)");
            }
        }

        // ── dfbproj registration ──────────────────────────────────────────────

        /// <summary>
        /// Inserts entries for Robot_Task_Core.fbt (BFB at root) and all 11
        /// Robot_Task_CAT files into the dfbproj. Idempotent.
        /// </summary>
        private static int RegisterInDfbproj(string dfbprojPath, string iec61499Dir)
        {
            var xml = XDocument.Load(dfbprojPath);
            var ns = xml.Root!.GetDefaultNamespace();

            // ── Find or create ItemGroup containers ───────────────────────────
            // Compile group: where .fbt files go
            var compileGroup = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "Compile").Any());
            if (compileGroup == null)
            {
                compileGroup = new XElement(ns + "ItemGroup");
                xml.Root.Add(compileGroup);
            }

            // None group: where companion files (.cfg, .offline.xml etc.) go
            var noneGroup = xml.Descendants(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "None").Any());
            if (noneGroup == null)
            {
                noneGroup = new XElement(ns + "ItemGroup");
                xml.Root.Add(noneGroup);
            }

            int adds = 0;
            void EnsureEntry(XElement group, XElement entry)
            {
                var include = (string?)entry.Attribute("Include") ?? "";
                bool exists = group.Elements()
                    .Any(e => string.Equals(
                        (string?)e.Attribute("Include"), include,
                        StringComparison.OrdinalIgnoreCase));
                if (!exists) { group.Add(entry); adds++; }
            }

            // ── Robot_Task_Core.fbt — Basic FB at root (BFB) ─────────────────
            EnsureEntry(compileGroup,
                new XElement(ns + "Compile",
                    new XAttribute("Include", $"{BasicFbName}.fbt"),
                    new XElement(ns + "IEC61499Type", "BFB")));

            // ── Robot_Task_CAT.fbt — Composite CAT wrapper ────────────────────
            EnsureEntry(compileGroup,
                new XElement(ns + "Compile",
                    new XAttribute("Include", $@"{CatName}\{CatName}.fbt"),
                    new XElement(ns + "IEC61499Type", "CAT")));

            // ── Robot_Task_CAT_HMI.fbt — HMI SIFB (private, depends on CAT) ──
            EnsureEntry(compileGroup,
                new XElement(ns + "Compile",
                    new XAttribute("Include", $@"{CatName}\{HmiName}.fbt"),
                    new XElement(ns + "IEC61499Type", "CAT"),
                    new XElement(ns + "DependentUpon", $@"{CatName}\{CatName}.fbt"),
                    new XElement(ns + "Usage", "Private")));

            // ── None entries for companion files ──────────────────────────────
            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{CatName}.cfg"),
                    new XElement(ns + "DependentUpon", $"{CatName}.fbt"),
                    new XElement(ns + "IEC61499Type", "CAT")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{CatName}_CAT.offline.xml"),
                    new XElement(ns + "DependentUpon", $"{CatName}.fbt"),
                    new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                    new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{CatName}_CAT.opcua.xml"),
                    new XElement(ns + "DependentUpon", $"{CatName}.fbt"),
                    new XElement(ns + "Plugin", "OPCUAConfigurator"),
                    new XElement(ns + "IEC61499Type", "CAT_OPCUA")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{HmiName}.meta.xml"),
                    new XElement(ns + "DependentUpon", $"{HmiName}.fbt")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{HmiName}.offline.xml"),
                    new XElement(ns + "DependentUpon", $"{CatName}.fbt"),
                    new XElement(ns + "Plugin", "OfflineParametrizationEditor"),
                    new XElement(ns + "IEC61499Type", "CAT_OFFLINE")));

            EnsureEntry(noneGroup,
                new XElement(ns + "None",
                    new XAttribute("Include", $@"{CatName}\{HmiName}.opcua.xml"),
                    new XElement(ns + "DependentUpon", $"{CatName}.fbt"),
                    new XElement(ns + "Plugin", "OPCUAConfigurator"),
                    new XElement(ns + "IEC61499Type", "CAT_OPCUA")));

            xml.Save(dfbprojPath);
            MapperLogger.Info($"[RobotCAT] dfbproj saved — {adds} new entries added.");
            return adds;
        }

        // ── .cfg patch: ensure Plugin paths use folder-relative paths ─────────

        /// <summary>
        /// Loads the template .cfg, patches Name + Plugin Value paths, then writes
        /// directly to <paramref name="targetPath"/> using UTF-8 WITHOUT BOM.
        ///
        /// NEVER use StringWriter here — StringWriter.Encoding = UTF-16,
        /// so XDocument.Save(stringWriter) emits encoding="utf-16" in the XML
        /// declaration, which conflicts with the actual UTF-8 bytes on disk and
        /// causes EAE to report "There is an error in XML document (0, 0)".
        /// </summary>
        private static void PatchAndWriteCfg(string sourceCfgPath, string targetPath)
        {
            var doc = XDocument.Load(sourceCfgPath);
            var root = doc.Root!;

            root.SetAttributeValue("Name", CatName);

            XNamespace ns = root.GetDefaultNamespace();
            foreach (var plugin in root.Elements(ns + "Plugin").Concat(root.Elements("Plugin")))
            {
                var val = (string?)plugin.Attribute("Value");
                if (val == null) continue;
                var flat = Path.GetFileName(val);
                plugin.SetAttributeValue("Value", $@"{CatName}\{flat}");
            }

            // Write directly with XmlWriter so encoding="utf-8" (no BOM) is correct
            var settings = new XmlWriterSettings
            {
                Encoding = Utf8NoBom,
                Indent = true,
                IndentChars = "  ",
                OmitXmlDeclaration = false
            };
            using var writer = XmlWriter.Create(targetPath, settings);
            doc.Save(writer);
        }

        // ── Content generators ────────────────────────────────────────────────
        // These produce correct, EAE-loadable XML when the template project files
        // are not available on the current machine.

        /// <summary>
        /// Generates Robot_Task_CAT.fbt — the Composite CAT wrapper.
        /// Mirrors the real file from SMC_Rig_Expo exactly.
        /// Robot_Task_Core (StateMachine), Robot_Task_CAT_HMI (IThis),
        /// RobotCommands / RobotStatus SYMLINK sockets, Signal_Pulse.
        /// </summary>
        private static string GenerateCatFbt() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE FBType SYSTEM "../LibraryElement.dtd">
            <FBType GUID="65cef328-d161-4394-ae5d-e7bc0ec9d5b6"
                    Name="Robot_Task_CAT"
                    Comment="Composite Function Block Type"
                    Namespace="Main">
              <Attribute Name="HMI.Alias" Value="" />
              <Attribute Name="Configuration.FB.IDCounter" Value="9" />
              <Identification Standard="61499-2" />
              <VersionInfo Organization="Schneider Electric" Version="0.0"
                           Author=" " Date="10/8/2025" Remarks="template" />
              <InterfaceList>
                <EventInputs>
                  <Event Name="INIT" Comment="Initialization Request">
                    <With Var="process_state_name" />
                    <With Var="state_val" />
                    <With Var="actuator_name" />
                  </Event>
                  <Event Name="pst_event">
                    <With Var="process_state_name" />
                    <With Var="state_val" />
                  </Event>
                </EventInputs>
                <EventOutputs>
                  <Event Name="INITO" Comment="Initialization Confirm" />
                  <Event Name="pst_out">
                    <With Var="current_state_to_process" />
                  </Event>
                </EventOutputs>
                <InputVars>
                  <VarDeclaration Name="process_state_name" Type="STRING[150]" />
                  <VarDeclaration Name="state_val" Type="INT" />
                  <VarDeclaration Name="actuator_name" Type="STRING[150]" />
                </InputVars>
                <OutputVars>
                  <VarDeclaration Name="current_state_to_process" Type="INT" />
                </OutputVars>
              </InterfaceList>
              <FBNetwork>
                <FB ID="5" Name="RobotCommands"
                    Type="SYMLINKMULTIVARSRC_1559B0FF8170C9BA0"
                    x="4620" y="660" Namespace="Main">
                  <Attribute Name="Configuration.GenericFBType.InterfaceParams"
                             Value="Runtime.System#I:=1;VALUE${I}:BOOL" />
                  <Parameter Name="NAME1" Value="'$${PATH}RobotCommands_StartTask'" />
                  <Parameter Name="QI"    Value="TRUE" />
                </FB>
                <FB ID="6" Name="RobotStatus"
                    Type="SYMLINKMULTIVARDST_1559B0FF8170C9BA0"
                    x="500" y="660" Namespace="Main">
                  <Attribute Name="Configuration.GenericFBType.InterfaceParams"
                             Value="Runtime.System#I:=1;VALUE${I}:BOOL" />
                  <Parameter Name="NAME1" Value="'$${PATH}RobotStatus_Task_Complete'" />
                  <Parameter Name="QI"    Value="TRUE" />
                </FB>
                <FB ID="7" Name="StateMachine"
                    Type="Robot_Task_Core"
                    x="1980" y="660" Namespace="Main" />
                <FB ID="8" Name="Signal_Pulse"
                    Type="pulse"
                    x="3460" y="779.9999" Namespace="SE.AppBase">
                  <Parameter Name="PulseTime" Value="T#5000ms" />
                </FB>
                <FB ID="9" Name="IThis"
                    Type="Robot_Task_CAT_HMI"
                    x="4660" y="1660" Namespace="Main">
                  <Parameter Name="QI" Value="TRUE" />
                </FB>
                <Input  Name="INIT"                x="60"       y="672"    Type="Event" />
                <Input  Name="process_state_name"  x="79.99999" y="1252"   Type="Data"  />
                <Input  Name="state_val"           x="80.00002" y="1332"   Type="Data"  />
                <Input  Name="pst_event"           x="1360"     y="791.9999" Type="Event" />
                <Input  Name="actuator_name"       x="79.99999" y="1432"   Type="Data"  />
                <Output Name="INITO"               x="5560"     y="1672"   Type="Event" />
                <Output Name="pst_out"             x="5280"     y="1432"   Type="Event" />
                <Output Name="current_state_to_process" x="4860" y="2152"  Type="Data"  />
                <EventConnections>
                  <Connection Source="INIT"                  Destination="RobotStatus.INIT" />
                  <Connection Source="RobotStatus.INITO"     Destination="StateMachine.INIT" />
                  <Connection Source="StateMachine.INITO"    Destination="RobotCommands.INIT" />
                  <Connection Source="RobotCommands.INITO"   Destination="IThis.INIT" dx1="40" dx2="50" dy="470" />
                  <Connection Source="IThis.INITO"           Destination="INITO" dx1="40" />
                  <Connection Source="RobotStatus.CNF"       Destination="StateMachine.REQ" />
                  <Connection Source="StateMachine.CNF"      Destination="Signal_Pulse.START" dx1="80" />
                  <Connection Source="pst_event"             Destination="StateMachine.pst_event" dx1="40" />
                  <Connection Source="StateMachine.pst_out"  Destination="pst_out" dx1="123.396" />
                  <Connection Source="StateMachine.pst_out"  Destination="IThis.pst_out" dx1="123.396" />
                  <Connection Source="Signal_Pulse.CNF_CHANGE" Destination="RobotCommands.REQ" dx1="54.188" />
                  <Connection Source="Signal_Pulse.CNF_CHANGE" Destination="IThis.CNF_CHANGE" dx1="60" />
                </EventConnections>
                <DataConnections>
                  <Connection Source="RobotStatus.VALUE1"                     Destination="StateMachine.task_complete" dx1="90" />
                  <Connection Source="StateMachine.current_state_to_process"  Destination="current_state_to_process" dx1="40" />
                  <Connection Source="StateMachine.current_state_to_process"  Destination="IThis.current_state_to_process" dx1="40" />
                  <Connection Source="process_state_name"                     Destination="StateMachine.process_state_name" dx1="377" />
                  <Connection Source="state_val"                              Destination="StateMachine.state_val" dx1="969" />
                  <Connection Source="actuator_name"                          Destination="StateMachine.actuator_name" dx1="788" />
                  <Connection Source="Signal_Pulse.PulseActive"               Destination="RobotCommands.VALUE1" dx1="74.188" />
                  <Connection Source="Signal_Pulse.PulseActive"               Destination="IThis.PulseActive" dx1="40" />
                </DataConnections>
              </FBNetwork>
            </FBType>
            """;

        /// <summary>
        /// Generates Robot_Task_Core.fbt — the Basic FB state machine.
        /// </summary>
        private static string GenerateBasicFbt() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE FBType SYSTEM "../LibraryElement.dtd">
            <FBType GUID="874b03d8-4114-40ae-86bd-e41b900704ad"
                    Name="Robot_Task_Core"
                    Comment="Basic Function Block Type"
                    Namespace="Main">
              <Identification Standard="61499-2" />
              <VersionInfo Organization="Schneider Electric" Version="0.0"
                           Author="Evans_A" Date="8/21/2025" Remarks="template" />
              <InterfaceList>
                <EventInputs>
                  <Event Name="INIT" Comment="Initialization Request">
                    <With Var="process_state_name" />
                    <With Var="state_val" />
                    <With Var="actuator_name" />
                  </Event>
                  <Event Name="REQ" Comment="Normal Execution Request">
                    <With Var="task_complete" />
                  </Event>
                  <Event Name="pst_event">
                    <With Var="process_state_name" />
                    <With Var="state_val" />
                  </Event>
                </EventInputs>
                <EventOutputs>
                  <Event Name="INITO" Comment="Initialization Confirm" />
                  <Event Name="CNF"   Comment="Execution Confirmation" />
                  <Event Name="pst_out">
                    <With Var="current_state_to_process" />
                  </Event>
                </EventOutputs>
                <InputVars>
                  <VarDeclaration Name="process_state_name" Type="STRING[150]" />
                  <VarDeclaration Name="state_val"          Type="INT" />
                  <VarDeclaration Name="actuator_name"      Type="STRING[150]" />
                  <VarDeclaration Name="task_complete"      Type="BOOL" />
                </InputVars>
                <OutputVars>
                  <VarDeclaration Name="current_state_to_process" Type="INT" />
                </OutputVars>
              </InterfaceList>
              <BasicFB>
                <ECC>
                  <ECState Name="START"  Comment="Initial State" x="560"  y="400" />
                  <ECState Name="INIT"   x="560"  y="800">
                    <ECAction Algorithm="initialize" Output="INITO" />
                  </ECState>
                  <ECState Name="IDLE"   x="560"  y="1200" />
                  <ECState Name="ACTIVE" x="560"  y="1600">
                    <ECAction Algorithm="on_pst_event" Output="pst_out" />
                  </ECState>
                  <ECState Name="WAIT"   x="1200" y="1600" />
                  <ECState Name="DONE"   x="1200" y="1200">
                    <ECAction Algorithm="on_complete" Output="CNF" />
                  </ECState>
                  <ECTransition Source="START"  Destination="INIT"   Condition="INIT"         x="660" y="600" />
                  <ECTransition Source="INIT"   Destination="IDLE"   Condition="1"            x="660" y="1000" />
                  <ECTransition Source="IDLE"   Destination="ACTIVE" Condition="pst_event"    x="660" y="1400" />
                  <ECTransition Source="ACTIVE" Destination="WAIT"   Condition="1"            x="880" y="1400" />
                  <ECTransition Source="WAIT"   Destination="DONE"   Condition="task_complete" x="1000" y="1400" />
                  <ECTransition Source="DONE"   Destination="IDLE"   Condition="1"            x="880" y="1000" />
                </ECC>
                <Algorithm Name="initialize">
                  <ST><![CDATA[current_state_to_process := 0;]]></ST>
                </Algorithm>
                <Algorithm Name="on_pst_event">
                  <ST><![CDATA[current_state_to_process := state_val;]]></ST>
                </Algorithm>
                <Algorithm Name="on_complete">
                  <ST><![CDATA[;]]></ST>
                </Algorithm>
              </BasicFB>
            </FBType>
            """;

        /// <summary>Generates Robot_Task_CAT_HMI.fbt — SIFB.</summary>
        private static string GenerateHmiFbt() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE FBType SYSTEM "../LibraryElement.dtd">
            <FBType GUID="65269706-6e22-4c8d-ab24-a730e4e7b641"
                    Name="Robot_Task_CAT_HMI"
                    Comment="Service Interface Function Block Type"
                    Namespace="Main">
              <Attribute Name="Configuration.FB.IDCounter" Value="0" />
              <Identification Standard="61499-2" />
              <VersionInfo Organization="Schneider Electric" Version="0.0"
                           Author=" " Date="10/8/2025" Remarks="template" />
              <InterfaceList>
                <EventInputs>
                  <Event ID="959F2046353E6F94" Name="INIT">
                    <With Var="QI" />
                  </Event>
                  <Event ID="92F1BFF097721B8D" Name="pst_out">
                    <With Var="current_state_to_process" />
                  </Event>
                  <Event ID="79CD58813EF52AE9" Name="CNF_CHANGE">
                    <With Var="PulseActive" />
                  </Event>
                </EventInputs>
                <EventOutputs>
                  <Event ID="DD17A7BEBF980B33" Name="INITO">
                    <With Var="QO" />
                    <With Var="STATUS" />
                  </Event>
                </EventOutputs>
                <InputVars>
                  <VarDeclaration ID="F709B444FA216077" Name="QI"                      Type="BOOL" />
                  <VarDeclaration ID="F0E4B21755073D3D" Name="current_state_to_process" Type="INT" />
                  <VarDeclaration ID="6FD83A23C876DAD1" Name="PulseActive"              Type="BOOL" />
                </InputVars>
                <OutputVars>
                  <VarDeclaration ID="ACECD28E6625BB31" Name="QO"     Type="BOOL"   Comment="Event Output Qualifier" />
                  <VarDeclaration ID="3A151F21B887B5C1" Name="STATUS" Type="STRING" Comment="Service Status" />
                </OutputVars>
              </InterfaceList>
              <Service RightInterface="" LeftInterface="">
                <ServiceSequence Name="" />
              </Service>
            </FBType>
            """;

        private static string GenerateDocXml() =>
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <section xmlns="http://docbook.org/ns/docbook"
                     xmlns:xi="http://www.w3.org/2001/XInclude"
                     xmlns:xlink="http://www.w3.org/1999/xlink">
              <info>
                <author>
                  <personname>
                    <firstname>Firstname</firstname>
                    <surname>Surname</surname>
                  </personname>
                  <email>name@company.com</email>
                </author>
                <abstract><para>Summary</para></abstract>
              </info>
              <para></para>
            </section>
            """;

        private static string GenerateOfflineXml() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <OfflineParameterModel xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                                   xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                                   IsDefaultEventSelectionDialogsHidden="0" />
            """;

        private static string GenerateCatOpcuaXml() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <OPCUAComplexObject xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <OPCUAAttribute Name="Exposed" Value="True" Locked="false"
                              AttributeMask="True;True|False;True"
                              Context="9.F0E4B21755073D3D" />
            </OPCUAComplexObject>
            """;

        private static string GenerateHmiOpcuaXml() =>
            """
            <?xml version="1.0" encoding="utf-8"?>
            <OPCUAObject xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <OPCUAVariable UID="F0E4B21755073D3D" Enabled="false">
                <OPCUAAttribute Name="Exposed"     Value="True" Locked="false"
                                AttributeMask="True;True|False;True" />
                <OPCUAAttribute Name="AccessLevel" Value="1"    Locked="true"
                                AttributeMask="CurrentRead;True" />
                <Extensions>
                  <Extension><RTAddress>V1;${VariableFullPath}</RTAddress></Extension>
                </Extensions>
              </OPCUAVariable>
            </OPCUAObject>
            """;

        private static string GenerateCfgXml() =>
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CAT xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                 xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                 Name="{CatName}"
                 CATFile="{CatName}\{CatName}.fbt"
                 SymbolDefFile="..\HMI\{CatName}\{CatName}.def.cs"
                 SymbolEventFile="..\HMI\{CatName}\{CatName}.event.cs"
                 DesignFile="..\HMI\{CatName}\{CatName}.Design.resx"
                 xmlns="http://www.nxtcontrol.com/IEC61499.xsd">
              <HMIInterface Name="IThis"
                            FileName="{CatName}\{HmiName}.fbt"
                            UsedInCAT="true"
                            Usage="Private">
                <Symbol Name="sDefault"
                        FileName="..\HMI\{CatName}\{CatName}_sDefault.cnv.cs">
                  <DependentFiles>..\HMI\{CatName}\{CatName}_sDefault.cnv.Designer.cs</DependentFiles>
                  <DependentFiles>..\HMI\{CatName}\{CatName}_sDefault.cnv.resx</DependentFiles>
                  <DependentFiles>..\HMI\{CatName}\{CatName}_sDefault.cnv.xml</DependentFiles>
                </Symbol>
              </HMIInterface>
              <Plugin Name="Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"
                      Project="IEC61499" Value="{CatName}\{CatName}_CAT.offline.xml" />
              <Plugin Name="Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"
                      Project="IEC61499" Value="{CatName}\{CatName}_CAT.opcua.xml" />
              <Plugin Name="Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"
                      Project="IEC61499" Value="{CatName}\{HmiName}.offline.xml" />
              <Plugin Name="Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"
                      Project="IEC61499" Value="{CatName}\{HmiName}.opcua.xml" />
              <HWConfiguration xsi:nil="true" />
            </CAT>
            """;
    }
}