using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using CodeGen.Configuration;
using CodeGen.Models;

namespace MapperUI.Services
{
    public static class EaeImportService
    {
        static readonly Dictionary<string, string[]> CatDependencies = new()
        {
            { "Five_State_Actuator_CAT", new[] { "FiveStateActuator" } },
            { "Sensor_Bool_CAT",         new[] { "Sensor_Bool" } },
            { "Actuator_Fault_CAT",      new[] { "FaultLatch" } },
            { "Robot_Task_CAT",          new[] { "Robot_Task_Core" } },
            { "Seven_State_Actuator_CAT",new[] { "SevenStateActuator2" } },
            { "Station_CAT",             new[] { "Station_Core", "Station_Fault", "Station_Status" } },
        };

        static readonly Dictionary<string, string> ComponentTypeToCat = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Actuator_5",  "Five_State_Actuator_CAT" },
            { "Actuator_7",  "Seven_State_Actuator_CAT" },
            { "Sensor_2",    "Sensor_Bool_CAT" },
        };

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

        static Action<string>? _log;
        static void Log(string msg) { _log?.Invoke(msg); MapperLogger.Info(msg); }

        public static EaeImportResult Import(MapperConfig cfg, List<VueOneComponent> components,
            Action<string>? onProgress = null)
        {
            _log = onProgress;
            var result = new EaeImportResult();
            var libPath = cfg.TemplateLibraryPath;

            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var eaeProjectDir = DeriveEaeProjectDir(cfg);
            if (string.IsNullOrWhiteSpace(eaeProjectDir))
                throw new InvalidOperationException("Cannot determine EAE project directory.");

            var eaeProcess = FindEaeProcess();
            int eaePid = eaeProcess?.Id ?? 0;

            var neededCats = ResolveNeededCats(components);
            var neededBasics = ResolveNeededBasics(neededCats);

            var iec61499Dir = Path.Combine(eaeProjectDir, "IEC61499");
            var dfbproj = Directory.GetFiles(iec61499Dir, "*.dfbproj").FirstOrDefault();

            var reloadWatcher = eaePid > 0
                ? StartReloadWatcher(eaePid)
                : null;

            Log("Deploying templates...");

            foreach (var basic in neededBasics.OrderBy(b => b))
            {
                var zipPath = FindPackage(libPath, "Basic", basic, ".Basic");
                if (zipPath == null) { result.Warnings.Add($"Basic not found: {basic}"); continue; }

                ExtractToProject(zipPath, eaeProjectDir);
                if (dfbproj != null)
                    DfbprojRegistrar.RegisterBasicFb(dfbproj, basic + ".fbt");
                result.ImportedCount++;
                Log($"  Basic: {basic}");
            }

            foreach (var cat in neededCats.OrderBy(c => c))
            {
                var zipPath = FindPackage(libPath, "CAT", cat, ".cat");
                if (zipPath == null) { result.Warnings.Add($"CAT not found: {cat}"); continue; }

                ExtractToProject(zipPath, eaeProjectDir);
                GenerateCfgFile(iec61499Dir, cat);
                if (dfbproj != null)
                    DfbprojRegistrar.RegisterCat(dfbproj, cat);
                result.ImportedCount++;
                Log($"  CAT: {cat}");
            }

            if (dfbproj != null)
                File.SetLastWriteTime(dfbproj, DateTime.Now);

            if (eaePid > 0)
            {
                Log("Waiting for EAE to detect changes...");
                SetForegroundWindow(eaeProcess!.MainWindowHandle);
                Thread.Sleep(2000);

                if (reloadWatcher != null)
                {
                    reloadWatcher.Join(TimeSpan.FromSeconds(10));
                    Log("EAE reload handled.");
                }
            }

            result.Success = result.ImportedCount > 0;
            if (result.Success)
                Log($"Done. {result.ImportedCount} template(s) deployed.");
            return result;
        }

        static Thread StartReloadWatcher(int eaePid)
        {
            var thread = new Thread(() => WatchForReloadDialog(eaePid))
            {
                IsBackground = true,
                Name = "EAE-Reload-Watcher"
            };
            thread.Start();
            return thread;
        }

        static void WatchForReloadDialog(int eaePid)
        {
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                try
                {
                    var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

                    foreach (AutomationElement w in windows)
                    {
                        if (w.Current.ProcessId != eaePid) continue;

                        var name = w.Current.Name ?? "";
                        var cls = w.Current.ClassName ?? "";

                        if (cls == "#32770" ||
                            name.Contains("Reload", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("modified", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("changed", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("EcoStruxure", StringComparison.OrdinalIgnoreCase))
                        {
                            var buttons = w.FindAll(TreeScope.Descendants,
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                            foreach (AutomationElement btn in buttons)
                            {
                                var btnName = btn.Current.Name ?? "";
                                if (btnName.Contains("Reload", StringComparison.OrdinalIgnoreCase) ||
                                    btnName.Contains("Yes", StringComparison.OrdinalIgnoreCase) ||
                                    btnName.Equals("OK", StringComparison.OrdinalIgnoreCase))
                                {
                                    Log($"  Auto-clicking '{btnName}' on EAE dialog.");
                                    if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                                        ((InvokePattern)ip).Invoke();
                                    return;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        static void ExtractToProject(string zipPath, string eaeProjectDir)
        {
            using var zip = ZipFile.OpenRead(zipPath);

            var knownRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "IEC61499", "HMI", "HwConfiguration" };
            string? prefixToStrip = null;

            var firstFile = zip.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
            if (firstFile != null)
            {
                var parts = firstFile.FullName.Split('/');
                if (parts.Length >= 2 && !knownRoots.Contains(parts[0]))
                    prefixToStrip = parts[0] + "/";
            }

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var relativePath = entry.FullName;
                if (prefixToStrip != null && relativePath.StartsWith(prefixToStrip, StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(prefixToStrip.Length);

                var targetPath = Path.Combine(eaeProjectDir, relativePath);
                var targetDir = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
                if (!File.Exists(targetPath))
                    entry.ExtractToFile(targetPath);
            }
        }

        static void GenerateCfgFile(string iec61499Dir, string cat)
        {
            var catDir = Path.Combine(iec61499Dir, cat);
            var cfgPath = Path.Combine(catDir, $"{cat}.cfg");
            if (File.Exists(cfgPath) || !Directory.Exists(catDir)) return;

            var hmi = cat + "_HMI";
            var cfg = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<CAT xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" Name=""{cat}"" CATFile=""{cat}\{cat}.fbt"" SymbolDefFile=""..\HMI\{cat}\{cat}.def.cs"" SymbolEventFile=""..\HMI\{cat}\{cat}.event.cs"" DesignFile=""..\HMI\{cat}\{cat}.Design.resx"" xmlns=""http://www.nxtcontrol.com/IEC61499.xsd"">
  <HMIInterface Name=""IThis"" FileName=""{cat}\{hmi}.fbt"" UsedInCAT=""true"" Usage=""Private"">
    <Symbol Name=""sDefault"" FileName=""..\HMI\{cat}\{cat}_sDefault.cnv.cs"">
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.Designer.cs</DependentFiles>
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.resx</DependentFiles>
      <DependentFiles>..\HMI\{cat}\{cat}_sDefault.cnv.xml</DependentFiles>
    </Symbol>
  </HMIInterface>
  <Plugin Name=""Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{cat}_CAT.offline.xml"" />
  <Plugin Name=""Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{cat}_CAT.opcua.xml"" />
  <Plugin Name=""Plugin=OfflineParametrizationEditor;IEC61499Type=CAT_OFFLINE;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{hmi}.offline.xml"" />
  <Plugin Name=""Plugin=OPCUAConfigurator;IEC61499Type=CAT_OPCUA;$ItemType$=None"" Project=""IEC61499"" Value=""{cat}\{hmi}.opcua.xml"" />
  <HWConfiguration xsi:nil=""true"" />
</CAT>";
            File.WriteAllText(cfgPath, cfg);
        }

        static string? DeriveEaeProjectDir(MapperConfig cfg)
        {
            var syslayPath = cfg.ActiveSyslayPath;
            if (string.IsNullOrWhiteSpace(syslayPath)) return null;
            var dir = Path.GetDirectoryName(syslayPath);
            while (dir != null)
            {
                var checkDir = dir;
                while (checkDir != null)
                {
                    if (Directory.GetFiles(checkDir, "*.dfbproj").Any())
                        return Path.GetDirectoryName(checkDir);
                    checkDir = Path.GetDirectoryName(checkDir);
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static HashSet<string> ResolveNeededCats(List<VueOneComponent> components)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in components)
            {
                var key = $"{c.Type}_{c.States.Count}";
                if (ComponentTypeToCat.TryGetValue(key, out var cat)) cats.Add(cat);
            }
            return cats;
        }

        static HashSet<string> ResolveNeededBasics(HashSet<string> cats)
        {
            var basics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in cats)
                if (CatDependencies.TryGetValue(cat, out var deps))
                    foreach (var dep in deps) basics.Add(dep);
            return basics;
        }

        static string? FindPackage(string libPath, string subfolder, string name, string extension)
        {
            var dir = Path.Combine(libPath, subfolder);
            if (!Directory.Exists(dir)) return null;
            foreach (var file in Directory.GetFiles(dir))
            {
                var fn = Path.GetFileName(file);
                if (fn.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                    fn.Contains(extension, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }

        static Process? FindEaeProcess()
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrEmpty(proc.MainWindowTitle) &&
                        proc.MainWindowTitle.Contains("Automation Expert", StringComparison.OrdinalIgnoreCase))
                        return proc;
                }
                catch { }
            }
            return null;
        }
    }

    public class EaeImportResult
    {
        public bool Success { get; set; }
        public int ImportedCount { get; set; }
        public List<string> Warnings { get; set; } = new();
    }
}
