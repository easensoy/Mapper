using CodeGen.Configuration;
using CodeGen.IO;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Validation;
using MapperUI.Services;
using MapperUI.Services.MapperUI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MapperUI.Services
{
    public partial class MainForm : Form
    {
        private readonly MapperService _mapperService;
        private MapperConfig? _mapperConfig;
        private List<VueOneComponent> _loadedComponents = new List<VueOneComponent>();
        private SystemXmlReader? _lastReader;

        public MainForm()
        {
            InitializeComponent();
            _mapperService = new MapperService();
            MapperLogger.OnEntry += OnLogEntry;
            btnGenerate.Enabled = false;
        }

        private async void btnBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*";
                dialog.Title = "Select VueOne Control.xml";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtModelPath.Text = dialog.FileName;
                    lblStatus.Text = "Validating...";
                    await LoadAndValidate(dialog.FileName);
                }
            }
        }

        private void btnMappingRules_Click(object sender, EventArgs e)
        {
            if (_loadedComponents.Count == 0)
            {
                MessageBox.Show("Load a Control.xml file first", "No Data", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            PopulateMappingRules();
            UpdateDetectedInfo();
        }

        private void PopulateMappingRules()
        {
            dgvMappingRules.Rows.Clear();

            foreach (var component in _loadedComponents)
            {
                var templateSelector = new TemplateSelector();
                var template = templateSelector.SelectTemplate(component);

                if (template == null || !TemplateMatchesStateCount(component, template))
                    continue;

                bool isProcess = string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase);
                bool isActuator = string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase);
                bool isSensor = string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase);

                var resolvedTemplateName = ResolveTemplateName(component);
                var templateNameToShow = string.IsNullOrWhiteSpace(resolvedTemplateName)
                    ? template.TemplateName
                    : resolvedTemplateName;

                bool isCatSensorTemplate = templateNameToShow.Contains("_CAT", StringComparison.OrdinalIgnoreCase)
                                           && isSensor;

                // ── Name row ────────────────────────────────────────────────
                AddMappingRow(
                    $"<Name>{component.Name}</Name>",
                    $"FBType Name=\"{templateNameToShow.Replace(".fbt", "")}_{component.Name}\"",
                    "TRANSLATED",
                    "Component naming convention",
                    validated: true,
                    Color.LightGreen);

                // ── Type row ─────────────────────────────────────────────────
                AddMappingRow(
                    $"<Type>{component.Type}</Type>",
                    $"Template: {templateNameToShow}",
                    "TRANSLATED",
                    "Template selection",
                    validated: true,
                    Color.LightGreen);

                // ── GUID row ─────────────────────────────────────────────────
                AddMappingRow(
                    "<ComponentID>",
                    $"GUID: {System.Guid.NewGuid()}",
                    "TRANSLATED",
                    "Component ID mapped to GUID",
                    validated: true,
                    Color.LightGreen);

                // ── Version row ──────────────────────────────────────────────
                AddMappingRow(
                    "Version=\"1.0.0\"",
                    "VueOne metadata only",
                    "DISCARDED",
                    "Versioning not used",
                    validated: false,
                    Color.LightSalmon);

                // ── State count row ──────────────────────────────────────────
                AddMappingRow(
                    $"State Count: {component.States.Count}",
                    isActuator ? "Five-state actuator (CAT)" :
                    isSensor ? "Two-state sensor (ON/OFF)" :
                                  $"Process: {component.States.Count} states → Text[] array",
                    "TRANSLATED",
                    isActuator ? "Actuator state count" :
                    isSensor ? "Sensor state count" :
                                  "Process state count → Text parameter",
                    validated: true,
                    Color.LightGreen);

                if (isProcess)
                {
                    // ── Process-specific rows ─────────────────────────────────
                    var stateNames = component.States
                        .OrderBy(s => s.StateNumber)
                        .Select(s => $"'{s.Name}'");
                    AddMappingRow(
                        "States[]",
                        $"Text=[{string.Join(", ", stateNames)}]",
                        "TRANSLATED",
                        "State names → Process1_CAT Text parameter",
                        validated: true,
                        Color.LightGreen);

                    AddMappingRow(
                        "Process FB instance",
                        "Injected as Process1_CAT in .syslay + .sysres",
                        "PHASE 2",
                        "Use Inject System button — not Generate FB",
                        validated: true,
                        Color.LightBlue);

                    AddMappingRow(
                        "Process wiring",
                        "state_change ← sensors/actuators.pst_out  |  state_update → actuator.pst_event",
                        "PHASE 2",
                        "EventConnections + DataConnections written to syslay/sysres",
                        validated: true,
                        Color.LightBlue);

                    AddMappingRow(
                        "Future: Rule Engine FB",
                        "Process1_CAT hardcoded ECC → data-driven step array (pending Alex/Jyotsna)",
                        "PENDING",
                        "Process1_CAT type redesign required for scalability",
                        validated: false,
                        Color.LightYellow);
                }
                else if (isActuator)
                {
                    AddMappingRow(
                        "actuator_name parameter",
                        $"actuator_name = '{component.Name.ToLower()}'",
                        "TRANSLATED",
                        "Actuator instance name parameter",
                        validated: true,
                        Color.LightGreen);
                    AddMappingRow(
                        "InterfaceList (complete)",
                        "INIT, pst_event, action_event, pst_out, INITO, current_state_to_process, state_val, process_state_name",
                        "HARDCODED",
                        "Template interface definition",
                        validated: true,
                        Color.LightGray);
                    AddMappingRow(
                        "FBNetwork (complete)",
                        "FB1:FiveStateActuator, IThis:HMI, Inputs/Output symlinks",
                        "HARDCODED",
                        "Template FB network (composite wrapper)",
                        validated: true,
                        Color.LightGray);
                }
                else if (isSensor)
                {
                    if (isCatSensorTemplate)
                    {
                        AddMappingRow(
                            "Sensor input",
                            "FB2:SYMLINKMULTIVARDST (Input)",
                            "HARDCODED",
                            "Template input symlink",
                            validated: true,
                            Color.LightGray);
                        AddMappingRow(
                            "Status output",
                            "FB1.Status → Status",
                            "TRANSLATED",
                            "Sensor status output mapping",
                            validated: true,
                            Color.LightGreen);
                        AddMappingRow(
                            "InterfaceList (complete)",
                            "INIT/INITO/pst_out, Status",
                            "HARDCODED",
                            "Template interface definition",
                            validated: true,
                            Color.LightGray);
                        AddMappingRow(
                            "FBNetwork (complete)",
                            "FB1:Sensor_Bool, FB2:Input symlink",
                            "HARDCODED",
                            "Template FB network (CAT wrapper)",
                            validated: true,
                            Color.LightGray);
                    }
                    else
                    {
                        AddMappingRow(
                            "Sensor input",
                            "Input: BOOL",
                            "TRANSLATED",
                            "Basic sensor input mapping",
                            validated: true,
                            Color.LightGreen);
                        AddMappingRow(
                            "Sensor output",
                            "value: BOOL / CHANGED",
                            "TRANSLATED",
                            "Basic sensor output mapping",
                            validated: true,
                            Color.LightGreen);
                        AddMappingRow(
                            "InterfaceList (complete)",
                            "INIT, REQ, CHANGED, INITO, CNF",
                            "HARDCODED",
                            "Template interface definition",
                            validated: true,
                            Color.LightGray);
                        AddMappingRow(
                            "FB logic",
                            "BasicFB ECC: START/INIT/REQ",
                            "HARDCODED",
                            "Template FB network (basic sensor)",
                            validated: true,
                            Color.LightGray);
                    }
                }
            }
        }

        private MapperConfig GetMapperConfig()
        {
            _mapperConfig ??= MapperConfig.Load();
            return _mapperConfig;
        }

        private string ResolveTemplatePath(VueOneComponent component)
        {
            var config = GetMapperConfig();

            if (string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
                return config.ActuatorTemplatePath;

            if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                return config.SensorTemplatePath;

            // Process → Process1_CAT (Phase 2 only, not cloned by Generate FB)
            if (string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase))
                return config.ProcessCATTemplatePath;

            return string.Empty;
        }

        private string ResolveTemplateName(VueOneComponent component)
        {
            var path = ResolveTemplatePath(component);
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        }

        private void UpdateDetectedInfo()
        {
            if (_loadedComponents.Count == 0 || _lastReader == null) { /* reset labels */ return; }

            // For a single component file (Actuator/Sensor), show its state count
            if (_loadedComponents.Count == 1)
            {
                var c = _loadedComponents[0];
                lblDetectedType.Text = c.Type;
                lblDetectedName.Text = c.Name;
                lblDetectedStates.Text = c.States.Count.ToString();
                return;
            }

            // For a system file, show system name + component summary
            int a = _loadedComponents.Count(c => c.Type == "Actuator");
            int s = _loadedComponents.Count(c => c.Type == "Sensor");
            int p = _loadedComponents.Count(c => c.Type == "Process");

            lblDetectedType.Text = "System";
            lblDetectedName.Text = _lastReader.SystemName;
            lblDetectedStates.Text = $"{_loadedComponents.Count} ({a}A / {s}S / {p}P)";
        }

        private void AddMappingRow(
            string vueOne,
            string iec61499,
            string type,
            string rule,
            bool validated,
            Color backColor)
        {
            int index = dgvMappingRules.Rows.Add(vueOne, iec61499, type, rule, validated);
            dgvMappingRules.Rows[index].DefaultCellStyle.BackColor = backColor;
        }

        private async System.Threading.Tasks.Task LoadAndValidate(string xmlPath)
        {
            dgvComponents.Rows.Clear();
            dgvMappingRules.Rows.Clear();

            try
            {
                var systemReader = new SystemXmlReader();

                await System.Threading.Tasks.Task.Run(() =>
                {
                    _loadedComponents = systemReader.ReadAllComponents(xmlPath);
                });
                _lastReader = systemReader;
                if (_loadedComponents.Count == 0)
                {
                    MessageBox.Show(
                        "No components found in Control.xml.\nCheck that the XML file has Type='System' or Type='Component'.",
                        "Validation Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    btnGenerate.Enabled = false;
                    lblStatus.Text = "No components found";
                    return;
                }

                var templateSelector = new TemplateSelector();
                var validator = new ComponentValidator();
                bool phase1Valid = true;   // tracks only Actuator + Sensor
                bool hasInjectables = false; // tracks whether Inject System has anything to do

                foreach (var component in _loadedComponents)
                {
                    var template = templateSelector.SelectTemplate(component);
                    bool isProcess = string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase);
                    bool templateMatches = template != null && TemplateMatchesStateCount(component, template);
                    string resolvedTemplatePath = ResolveTemplatePath(component);
                    bool templateExists = !string.IsNullOrEmpty(resolvedTemplatePath) && File.Exists(resolvedTemplatePath);

                    string templateName = templateMatches
                        ? (templateExists ? Path.GetFileName(resolvedTemplatePath) : Path.GetFileName(resolvedTemplatePath) + " (not found)")
                        : "No Template Found";

                    dgvComponents.Rows.Add(component.Name, component.Type, templateName);

                    if (isProcess)
                    {
                        // Process never blocks Generate FB — it's Phase 2 only
                        hasInjectables = true;
                    }
                    else if (templateMatches && templateExists)
                    {
                        var validationResult = validator.Validate(component);
                        if (!validationResult.IsValid)
                            phase1Valid = false;

                        hasInjectables = true;
                    }
                    else
                    {
                        phase1Valid = false;
                    }
                }

                // Generate FB = enabled only when Actuators/Sensors all pass Phase 1 validation
                if (phase1Valid)
                {
                    btnGenerate.Enabled = true;
                    lblStatus.Text = "Validation passed";
                    lblValidationStatus.Text = "PASSED";
                    lblValidationStatus.ForeColor = Color.Green;
                }
                else
                {
                    btnGenerate.Enabled = false;
                    lblStatus.Text = "Validation failed";
                    lblValidationStatus.Text = "FAILED";
                    lblValidationStatus.ForeColor = Color.Red;
                }

                // Inject System is always enabled if we have any injectable components
                btnInjectSystem.Enabled = hasInjectables;

                UpdateDetectedInfo();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error loading file.\n{ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                btnGenerate.Enabled = false;
                lblStatus.Text = "Error";
            }
        }

        private bool TemplateMatchesStateCount(VueOneComponent component, FBTemplate template)
        {
            if (string.Equals(component.Type, "Actuator", StringComparison.OrdinalIgnoreCase))
                return component.States.Count == 5;

            if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                return component.States.Count == 2;

            // Process: state count varies, always matches for display purposes
            if (string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            try
            {
                lblStatus.Text = "Generating...";
                btnGenerate.Enabled = false;

                if (_loadedComponents.Count == 0)
                {
                    MessageBox.Show(
                        "Load a Control.xml file first.\n\nNote: Use 'Inject System' for Process components.",
                        "No Data",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    lblStatus.Text = "No data loaded";
                    return;
                }

                var generatedNames = new List<string>();
                var skippedProcess = new List<string>();

                foreach (var component in _loadedComponents)
                {
                    // Process components are Phase 2 only — skip in Generate FB
                    if (string.Equals(component.Type, "Process", StringComparison.OrdinalIgnoreCase))
                    {
                        skippedProcess.Add(component.Name);
                        continue;
                    }

                    var result = await _mapperService.RunMapping(component);

                    if (!result.Success || result.GeneratedFB == null)
                    {
                        MessageBox.Show(
                            $"Generation failed for {component.Name}.\n{result.ErrorMessage}",
                            "Generation Failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        lblStatus.Text = "Generation failed";
                        return;
                    }

                    generatedNames.Add(result.GeneratedFB.FBName);
                }

                var sb = new System.Text.StringBuilder();
                if (generatedNames.Count > 0)
                {
                    sb.AppendLine("Generated FBs:");
                    foreach (var n in generatedNames) sb.AppendLine($"  + {n}");
                    sb.AppendLine();
                    sb.AppendLine("Files: .fbt, .composite.offline.xml, .doc.xml, .meta.xml");
                    sb.AppendLine("Next: Open EAE → Refresh → Verify build");
                }
                if (skippedProcess.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Skipped (Phase 2 — use Inject System):");
                    foreach (var n in skippedProcess) sb.AppendLine($"  → {n} (Process1_CAT)");
                }

                MessageBox.Show(sb.ToString(), "Generate FB Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                lblStatus.Text = $"Generated {generatedNames.Count} FB(s)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error generating output.\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Error";
            }
            finally
            {
                btnGenerate.Enabled = true;
            }
        }

        /// <summary>
        /// Walks up from the syslay file path until it finds a folder containing a .dfbproj.
        /// That folder is the EAE project root (baseline).
        /// </summary>
        private static string? DeriveBaselineFolder(string syslayPath)
        {
            var dir = Path.GetDirectoryName(syslayPath);
            while (dir != null)
            {
                var match = Directory.GetFiles(dir, "*.dfbproj").FirstOrDefault();
                if (match != null) return match;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        /// <summary>
        /// Appends a timestamped line to the status log in the UI.
        /// Uses the existing status label as a simple log if no log box exists,
        /// or writes to txtLog if you add one.
        /// </summary>
        private void OnLogEntry(LogEntry entry)
        {
            if (dgvLog.InvokeRequired) { dgvLog.Invoke(() => OnLogEntry(entry)); return; }

            var color = entry.Step switch
            {
                LogStep.ERROR => System.Drawing.Color.OrangeRed,
                LogStep.WARN => System.Drawing.Color.Yellow,
                LogStep.REMAP => System.Drawing.Color.LimeGreen,
                LogStep.WRITE => System.Drawing.Color.DeepSkyBlue,
                _ => System.Drawing.Color.LimeGreen
            };

            var row = dgvLog.Rows[dgvLog.Rows.Add(
                entry.Timestamp.ToString("HH:mm:ss.fff"),
                entry.Step.ToString(),
                entry.Action)];

            row.DefaultCellStyle.ForeColor = color;
            dgvLog.FirstDisplayedScrollingRowIndex = dgvLog.Rows.Count - 1;

            // Enforce max 1000 rows
            if (dgvLog.Rows.Count > 1000)
                dgvLog.Rows.RemoveAt(0);

            // Mirror to status bar
            if (entry.Step != LogStep.INFO)
                lblStatus.Text = entry.Action.Length > 100
                    ? entry.Action[..100] + "…"
                    : entry.Action;
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            dgvLog.Rows.Clear();
            MapperLogger.Info("Log cleared.");
        }
   
        private async void btnInjectSystem_Click(object sender, EventArgs e)
        {
            if (_loadedComponents == null || _loadedComponents.Count == 0)
            {
                MapperLogger.Info("[ERROR] No components loaded — browse a Control.xml first.");
                MessageBox.Show("Load a Control.xml first.", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnInjectSystem.Enabled = false;

            try
            {
                // ── 1. Load config and find the live EAE project ──────────────────
                _mapperConfig = MapperConfig.Load();
                MapperLogger.Info("Config loaded.");
                MapperLogger.Info($"syslay → {_mapperConfig.SyslayPath}");
                MapperLogger.Info($"  sysres → {_mapperConfig.SysresPath}");

                if (!File.Exists(_mapperConfig.SyslayPath))
                {
                    MapperLogger.Error($"syslay not found: {_mapperConfig.SyslayPath}");
                        MessageBox.Show($"syslay not found:\n{_mapperConfig.SyslayPath}\n\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var dfbproj = DeriveBaselineFolder(_mapperConfig.SyslayPath);
                if (dfbproj == null)
                {
                    MapperLogger.Validate("Checking CAT type registrations in .dfbproj");
                    MessageBox.Show("Cannot find .dfbproj above the syslay path.\nCheck mapper_config.json.",
                        "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                MapperLogger.Validate("Checking CAT type registrations in .dfbproj");

                // ── 2. Verify CAT types are registered in .dfbproj ────────────────
                MapperLogger.Info("Checking CAT type registrations in .dfbproj...");
                var dfbprojContent = await File.ReadAllTextAsync(dfbproj);
                bool actuatorRegistered = dfbprojContent.Contains("Five_State_Actuator_CAT");
                bool sensorRegistered = dfbprojContent.Contains("Sensor_Bool_CAT");
                bool processRegistered = dfbprojContent.Contains("Process1_CAT");

                MapperLogger.Validate("Five_State_Actuator_CAT: FOUND ✓");
                MapperLogger.Validate($"  Sensor_Bool_CAT         : {(sensorRegistered ? "FOUND ✓" : "MISSING ✗")}");
                MapperLogger.Validate($"  Process1_CAT            : {(processRegistered ? "FOUND ✓" : "MISSING ✗")}");

                if (!actuatorRegistered || !sensorRegistered)
                {
                    MapperLogger.Error("[ERROR] Required CAT types not registered. Is this the right EAE project?");
                    MessageBox.Show("Required CAT types not found in .dfbproj.\nIs mapper_config.json pointing at the right project?",
                        "Wrong Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // ── 3. Preview diff before touching anything ──────────────────────
                MapperLogger.Diff("Running diff against live syslay");
                var injector = new MapperUI.Services.SystemInjector();
                var config = new CodeGen.Configuration.MapperConfig
                {
                    SyslayPath = _mapperConfig.SyslayPath,
                    SysresPath = _mapperConfig.SysresPath
                };
                var diff = injector.PreviewDiff(config, _loadedComponents);
                MapperLogger.Diff($"Already in project: {diff.AlreadyPresent.Count}");
                    foreach (var s in diff.AlreadyPresent) MapperLogger.Info($"  = {s}");
                MapperLogger.Diff($"Will be remapped: {diff.ToBeInjected.Count}");
                    foreach (var i in diff.ToBeInjected) MapperLogger.Info($"  + {i}");
                MapperLogger.Diff($"  Unsupported (skip)  : {diff.Unsupported.Count}");
                    foreach (var u in diff.Unsupported) MapperLogger.Warn($"  ! {u}");

                if (diff.ToBeInjected.Count == 0)
                {
                    MapperLogger.Info("Nothing new to inject — project already up to date.");
                    MessageBox.Show("All components already exist in the project.\nNothing to inject.",
                        "Up To Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // ── 4. Inject directly into live syslay + sysres ──────────────────
                MapperLogger.Remap("Remapping FB names in live project files");
                var result = await Task.Run(() => injector.Inject(config, _loadedComponents));

                if (!result.Success)
                {
                    MapperLogger.Error($"Injection failed: {result.ErrorMessage}");
                    MessageBox.Show($"Injection failed:\n\n{result.ErrorMessage}",
                        "Injection Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MapperLogger.Write($"Remap complete — {result.InjectedFBs.Count} FB(s) renamed");
                foreach (var fb in result.InjectedFBs) MapperLogger.Info($"  + {fb}");

                // ── 5. Touch .dfbproj → EAE detects change → Reload Solution ─────
                MapperLogger.Touch($"Touching .dfbproj → {Path.GetFileName(dfbproj)}");
                File.SetLastWriteTime(dfbproj, DateTime.Now);
                AppendLog($"  Touched: {Path.GetFileName(dfbproj)}");
                AppendLog($"  EAE will now show 'Reload Solution' — click Yes in EAE.");

                MessageBox.Show(
                    $"Injected {result.InjectedFBs.Count} component(s) into the live project.\n\n" +
                    $"Switch to EAE — it will show a 'Reload Solution' dialog.\nClick Yes to load the changes.",
                    "Done — Reload EAE",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                MapperLogger.Info("=== Done ===");
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"EXCEPTION: {ex.Message}");
                MessageBox.Show($"Unexpected error:\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnInjectSystem.Enabled = true;
            }
        }
    }
}