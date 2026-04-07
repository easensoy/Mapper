using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
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

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_RESTORE = 9;

        public static EaeImportResult Import(MapperConfig cfg, List<VueOneComponent> components)
        {
            var result = new EaeImportResult();
            var libPath = cfg.TemplateLibraryPath;

            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            var stagingDir = Path.Combine(Path.GetTempPath(), "VueOneMapper_Import");
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            var neededCats = ResolveNeededCats(components);
            var neededBasics = ResolveNeededBasics(neededCats);
            var exportFiles = StageExportFiles(libPath, stagingDir, neededBasics, neededCats, result);

            if (exportFiles.Count == 0)
            {
                result.Warnings.Add("No importable templates found.");
                return result;
            }

            result.ImportFiles = exportFiles;
            result.StagingDirectory = stagingDir;

            var eaeProcess = FindEaeProcess();
            if (eaeProcess == null)
            {
                result.Warnings.Add("EAE is not running. Start EAE with a project open and try again.");
                return result;
            }

            var hwnd = eaeProcess.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                result.Warnings.Add("EAE window not found.");
                return result;
            }

            MapperLogger.Info($"[Import] Found EAE: {eaeProcess.MainWindowTitle} (PID {eaeProcess.Id})");

            foreach (var exportFile in exportFiles)
            {
                var fileName = Path.GetFileName(exportFile);
                MapperLogger.Info($"[Import] Importing: {fileName}");

                bool ok = ImportSingleFile(hwnd, exportFile);
                if (ok)
                {
                    result.ImportedCount++;
                    MapperLogger.Info($"[Import] Done: {fileName}");
                }
                else
                {
                    result.Warnings.Add($"Failed: {fileName}");
                    MapperLogger.Error($"[Import] Failed: {fileName}");
                }
            }

            result.Success = result.ImportedCount > 0;
            return result;
        }

        static bool ImportSingleFile(IntPtr eaeHwnd, string exportFilePath)
        {
            try
            {
                ShowWindow(eaeHwnd, SW_RESTORE);
                SetForegroundWindow(eaeHwnd);
                Thread.Sleep(400);

                var eaeWindow = AutomationElement.FromHandle(eaeHwnd);

                var projectNode = FindProjectNodeInTree(eaeWindow);
                if (projectNode == null)
                {
                    MapperLogger.Warn("[Import] Could not find project node in tree.");
                    return false;
                }

                MapperLogger.Info($"[Import] Found project node: {projectNode.Current.Name}");

                if (projectNode.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selPat))
                    ((SelectionItemPattern)selPat).Select();
                Thread.Sleep(200);

                var rect = projectNode.Current.BoundingRectangle;
                if (rect.IsEmpty)
                {
                    MapperLogger.Warn("[Import] Project node has no bounding rectangle.");
                    return false;
                }

                int cx = (int)(rect.X + rect.Width / 2);
                int cy = (int)(rect.Y + rect.Height / 2);

                SetCursorPos(cx, cy);
                Thread.Sleep(100);
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                Thread.Sleep(50);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                Thread.Sleep(600);

                if (!ClickContextMenuItem("Import"))
                {
                    MapperLogger.Warn("[Import] 'Import' not found in context menu. Dismissing...");
                    SendKeys.SendWait("{ESCAPE}");
                    return false;
                }

                Thread.Sleep(1000);
                return FillFileDialog(exportFilePath);
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"[Import] Error: {ex.Message}");
                return false;
            }
        }

        static bool ClickContextMenuItem(string itemName)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var menus = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));

                foreach (AutomationElement menu in menus)
                {
                    var items = menu.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

                    foreach (AutomationElement item in items)
                    {
                        if (item.Current.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                            {
                                ((InvokePattern)ip).Invoke();
                                return true;
                            }

                            var r = item.Current.BoundingRectangle;
                            if (!r.IsEmpty)
                            {
                                SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                                Thread.Sleep(50);
                                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                                Thread.Sleep(30);
                                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                                return true;
                            }
                        }
                    }
                }
                Thread.Sleep(200);
            }
            return false;
        }

        static bool FillFileDialog(string filePath)
        {
            for (int wait = 0; wait < 30; wait++)
            {
                Thread.Sleep(200);
                var dialog = FindFileDialog();
                if (dialog == null) continue;

                MapperLogger.Info($"[Import] File dialog found: {dialog.Current.Name}");

                var edits = dialog.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

                AutomationElement? fileNameBox = null;
                foreach (AutomationElement edit in edits)
                {
                    var name = edit.Current.Name;
                    if (name.Contains("File name", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Dateiname", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(name))
                    {
                        fileNameBox = edit;
                        break;
                    }
                }

                if (fileNameBox == null && edits.Count > 0)
                    fileNameBox = edits[edits.Count - 1];

                if (fileNameBox == null)
                {
                    MapperLogger.Warn("[Import] File name box not found in dialog.");
                    continue;
                }

                if (fileNameBox.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                {
                    ((ValuePattern)vp).SetValue(filePath);
                }
                else
                {
                    fileNameBox.SetFocus();
                    Thread.Sleep(100);
                    SendKeys.SendWait("^a");
                    Thread.Sleep(50);
                    SendKeys.SendWait(filePath.Replace("{", "{{").Replace("}", "}}"));
                }

                Thread.Sleep(300);

                var buttons = dialog.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                foreach (AutomationElement btn in buttons)
                {
                    var btnName = btn.Current.Name;
                    if (btnName.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
                        btnName.Equals("\u00d6ffnen", StringComparison.OrdinalIgnoreCase))
                    {
                        if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                            ((InvokePattern)ip).Invoke();
                        else
                        {
                            var r = btn.Current.BoundingRectangle;
                            SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                            Thread.Sleep(50);
                            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                            Thread.Sleep(30);
                            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                        }

                        Thread.Sleep(3000);
                        return true;
                    }
                }

                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(3000);
                return true;
            }

            MapperLogger.Warn("[Import] File dialog did not appear within timeout.");
            return false;
        }

        static AutomationElement? FindFileDialog()
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

            foreach (AutomationElement w in windows)
            {
                var name = w.Current.Name;
                if (name.Contains("Open", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Import", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Select", StringComparison.OrdinalIgnoreCase))
                {
                    var hasEdit = w.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    if (hasEdit != null) return w;
                }
            }
            return null;
        }

        static AutomationElement? FindProjectNodeInTree(AutomationElement eaeWindow)
        {
            var trees = eaeWindow.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree));

            foreach (AutomationElement tree in trees)
            {
                var topItems = tree.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));

                foreach (AutomationElement item in topItems)
                {
                    var name = item.Current.Name;
                    MapperLogger.Info($"[Import] Tree root: '{name}'");

                    if (name.Contains("Solution", StringComparison.OrdinalIgnoreCase))
                    {
                        var children = item.FindAll(TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));

                        foreach (AutomationElement child in children)
                        {
                            if (!child.Current.Name.Contains("Libraries", StringComparison.OrdinalIgnoreCase))
                            {
                                MapperLogger.Info($"[Import] Project node: '{child.Current.Name}'");
                                return child;
                            }
                        }
                    }

                    if (name.Contains("Demonstr", StringComparison.OrdinalIgnoreCase))
                        return item;
                }

                if (topItems.Count > 0)
                    return topItems[0];
            }

            return null;
        }

        static Process? FindEaeProcess()
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var title = proc.MainWindowTitle;
                    if (!string.IsNullOrEmpty(title) &&
                        (title.Contains("Automation Expert", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("nXT-", StringComparison.OrdinalIgnoreCase)))
                        return proc;
                }
                catch { }
            }
            return null;
        }

        static List<string> StageExportFiles(string libPath, string stagingDir,
            HashSet<string> neededBasics, HashSet<string> neededCats, EaeImportResult result)
        {
            var exportFiles = new List<string>();
            int step = 1;

            foreach (var basic in neededBasics.OrderBy(b => b))
            {
                var zipPath = FindPackage(libPath, "Basic", basic, ".Basic");
                if (zipPath == null) { result.Warnings.Add($"Basic not found: {basic}"); continue; }

                var stepDir = Path.Combine(stagingDir, $"{step:D2}_Basic_{basic}");
                Directory.CreateDirectory(stepDir);
                ExtractZip(zipPath, stepDir);

                var ef = Directory.GetFiles(stepDir, "*.export", SearchOption.AllDirectories).FirstOrDefault();
                if (ef != null) exportFiles.Add(ef);
                step++;
            }

            foreach (var cat in neededCats.OrderBy(c => c))
            {
                var zipPath = FindPackage(libPath, "CAT", cat, ".cat");
                if (zipPath == null) { result.Warnings.Add($"CAT not found: {cat}"); continue; }

                var stepDir = Path.Combine(stagingDir, $"{step:D2}_CAT_{cat}");
                Directory.CreateDirectory(stepDir);
                ExtractZip(zipPath, stepDir);

                var ef = Directory.GetFiles(stepDir, "*.export", SearchOption.AllDirectories).FirstOrDefault();
                if (ef != null) exportFiles.Add(ef);
                step++;
            }

            return exportFiles;
        }

        static HashSet<string> ResolveNeededCats(List<VueOneComponent> components)
        {
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in components)
            {
                var key = $"{c.Type}_{c.States.Count}";
                if (ComponentTypeToCat.TryGetValue(key, out var cat))
                    cats.Add(cat);
            }
            return cats;
        }

        static HashSet<string> ResolveNeededBasics(HashSet<string> cats)
        {
            var basics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cat in cats)
            {
                if (CatDependencies.TryGetValue(cat, out var deps))
                    foreach (var dep in deps)
                        basics.Add(dep);
            }
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

        static void ExtractZip(string zipPath, string targetDir)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var targetPath = Path.Combine(targetDir, entry.FullName);
                var targetFolder = Path.GetDirectoryName(targetPath)!;
                if (!Directory.Exists(targetFolder))
                    Directory.CreateDirectory(targetFolder);
                entry.ExtractToFile(targetPath, true);
            }
        }

        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        const int MOUSEEVENTF_LEFTDOWN = 0x02;
        const int MOUSEEVENTF_LEFTUP = 0x04;
        const int MOUSEEVENTF_RIGHTDOWN = 0x08;
        const int MOUSEEVENTF_RIGHTUP = 0x10;
    }

    public class EaeImportResult
    {
        public bool Success { get; set; }
        public int ImportedCount { get; set; }
        public List<string> ImportFiles { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string? StagingDirectory { get; set; }
    }
}
