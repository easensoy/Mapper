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
            var exportFiles = new List<string>();

            int step = 1;
            foreach (var basic in neededBasics.OrderBy(b => b))
            {
                var zipPath = FindPackage(libPath, "Basic", basic, ".Basic");
                if (zipPath == null) { result.Warnings.Add($"Basic package not found: {basic}"); continue; }

                var stepDir = Path.Combine(stagingDir, $"{step:D2}_Basic_{basic}");
                Directory.CreateDirectory(stepDir);
                ExtractZip(zipPath, stepDir);

                var exportFile = Directory.GetFiles(stepDir, "*.export", SearchOption.AllDirectories).FirstOrDefault();
                if (exportFile != null)
                {
                    exportFiles.Add(exportFile);
                    MapperLogger.Info($"[Import] Staged Basic: {basic}");
                }
                step++;
            }

            foreach (var cat in neededCats.OrderBy(c => c))
            {
                var zipPath = FindPackage(libPath, "CAT", cat, ".cat");
                if (zipPath == null) { result.Warnings.Add($"CAT package not found: {cat}"); continue; }

                var stepDir = Path.Combine(stagingDir, $"{step:D2}_CAT_{cat}");
                Directory.CreateDirectory(stepDir);
                ExtractZip(zipPath, stepDir);

                var exportFile = Directory.GetFiles(stepDir, "*.export", SearchOption.AllDirectories).FirstOrDefault();
                if (exportFile != null)
                {
                    exportFiles.Add(exportFile);
                    MapperLogger.Info($"[Import] Staged CAT: {cat}");
                }
                step++;
            }

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
                result.Warnings.Add("EAE is not running. Start EAE and try again.");
                return result;
            }

            MapperLogger.Info($"[Import] Found EAE process: {eaeProcess.ProcessName} (PID {eaeProcess.Id})");

            foreach (var exportFile in exportFiles)
            {
                MapperLogger.Info($"[Import] Importing: {Path.GetFileName(exportFile)}");
                bool imported = AutomateEaeImport(eaeProcess, exportFile);
                if (imported)
                    result.ImportedCount++;
                else
                    result.Warnings.Add($"Failed to automate import for: {Path.GetFileName(exportFile)}");
            }

            result.Success = result.ImportedCount > 0;
            return result;
        }

        static Process? FindEaeProcess()
        {
            var names = new[] { "EcoStruxureAutomationExpert", "nxtstudio", "nXTStudio" };
            foreach (var name in names)
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) return procs[0];
            }

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowTitle.Contains("Automation Expert", StringComparison.OrdinalIgnoreCase) ||
                        proc.MainWindowTitle.Contains("nXT-", StringComparison.OrdinalIgnoreCase))
                        return proc;
                }
                catch { }
            }
            return null;
        }

        static bool AutomateEaeImport(Process eaeProcess, string exportFilePath)
        {
            try
            {
                var hwnd = eaeProcess.MainWindowHandle;
                if (hwnd == IntPtr.Zero) return false;

                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                Thread.Sleep(500);

                var eaeWindow = AutomationElement.FromHandle(hwnd);
                if (eaeWindow == null) return false;

                var treeView = FindTreeView(eaeWindow);
                if (treeView == null)
                {
                    MapperLogger.Warn("[Import] Could not find Solution Explorer tree view. Trying menu approach...");
                    return TryMenuImport(eaeWindow, exportFilePath);
                }

                var projectNode = FindProjectNode(treeView);
                if (projectNode == null)
                {
                    MapperLogger.Warn("[Import] Could not find project node. Trying menu approach...");
                    return TryMenuImport(eaeWindow, exportFilePath);
                }

                SelectTreeItem(projectNode);
                Thread.Sleep(300);

                RightClickElement(projectNode);
                Thread.Sleep(500);

                var importMenu = FindImportMenuItem();
                if (importMenu == null)
                {
                    MapperLogger.Warn("[Import] Could not find Import menu item. Trying keyboard...");
                    SendKeys.SendWait("{ESCAPE}");
                    Thread.Sleep(200);
                    return TryMenuImport(eaeWindow, exportFilePath);
                }

                ClickElement(importMenu);
                Thread.Sleep(1000);

                return FillFileDialog(exportFilePath);
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"[Import] Automation error: {ex.Message}");
                return false;
            }
        }

        static bool TryMenuImport(AutomationElement eaeWindow, string exportFilePath)
        {
            try
            {
                SetForegroundWindow(new IntPtr(eaeWindow.Current.NativeWindowHandle));
                Thread.Sleep(300);

                SendKeys.SendWait("%f");
                Thread.Sleep(500);

                var menuItems = AutomationElement.RootElement.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

                foreach (AutomationElement item in menuItems)
                {
                    if (item.Current.Name.Contains("Import", StringComparison.OrdinalIgnoreCase))
                    {
                        if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                        {
                            ((InvokePattern)pattern).Invoke();
                            Thread.Sleep(1000);
                            return FillFileDialog(exportFilePath);
                        }
                    }
                }

                SendKeys.SendWait("{ESCAPE}");
                return false;
            }
            catch
            {
                return false;
            }
        }

        static bool FillFileDialog(string filePath)
        {
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(250);
                    var dialog = FindOpenFileDialog();
                    if (dialog != null)
                    {
                        var fileNameBox = dialog.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

                        if (fileNameBox != null)
                        {
                            if (fileNameBox.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                            {
                                ((ValuePattern)vp).SetValue(filePath);
                                Thread.Sleep(300);

                                var openBtn = dialog.FindFirst(TreeScope.Descendants,
                                    new AndCondition(
                                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                        new PropertyCondition(AutomationElement.NameProperty, "Open")));

                                if (openBtn != null && openBtn.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                                {
                                    ((InvokePattern)ip).Invoke();
                                    Thread.Sleep(2000);
                                    MapperLogger.Info($"[Import] Successfully imported: {Path.GetFileName(filePath)}");
                                    return true;
                                }
                            }
                        }
                    }
                }

                MapperLogger.Warn("[Import] File dialog not found within timeout.");
                return false;
            }
            catch (Exception ex)
            {
                MapperLogger.Error($"[Import] File dialog error: {ex.Message}");
                return false;
            }
        }

        static AutomationElement? FindTreeView(AutomationElement window)
        {
            return window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree));
        }

        static AutomationElement? FindProjectNode(AutomationElement treeView)
        {
            var items = treeView.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));

            foreach (AutomationElement item in items)
            {
                var name = item.Current.Name;
                if (name.Contains("Solution", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Demonstr", StringComparison.OrdinalIgnoreCase))
                {
                    var children = item.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
                    foreach (AutomationElement child in children)
                    {
                        if (!child.Current.Name.Contains("Libraries", StringComparison.OrdinalIgnoreCase))
                            return child;
                    }
                    return item;
                }
            }

            if (items.Count > 0)
                return items[0];

            return null;
        }

        static void SelectTreeItem(AutomationElement item)
        {
            if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                ((SelectionItemPattern)pattern).Select();
        }

        static void RightClickElement(AutomationElement element)
        {
            var rect = element.Current.BoundingRectangle;
            int x = (int)(rect.X + rect.Width / 2);
            int y = (int)(rect.Y + rect.Height / 2);

            SetCursorPos(x, y);
            Thread.Sleep(100);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, x, y, 0, 0);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_RIGHTUP, x, y, 0, 0);
            Thread.Sleep(300);
        }

        static void ClickElement(AutomationElement element)
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return;
            }

            var rect = element.Current.BoundingRectangle;
            int x = (int)(rect.X + rect.Width / 2);
            int y = (int)(rect.Y + rect.Height / 2);

            SetCursorPos(x, y);
            Thread.Sleep(100);
            mouse_event(MOUSEEVENTF_LEFTDOWN, x, y, 0, 0);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_LEFTUP, x, y, 0, 0);
        }

        static AutomationElement? FindImportMenuItem()
        {
            Thread.Sleep(300);
            var menus = AutomationElement.RootElement.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

            foreach (AutomationElement item in menus)
            {
                if (item.Current.Name.Equals("Import", StringComparison.OrdinalIgnoreCase) ||
                    item.Current.Name.Contains("Import", StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        static AutomationElement? FindOpenFileDialog()
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

            foreach (AutomationElement w in windows)
            {
                var name = w.Current.Name;
                if (name.Contains("Open", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Import", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Browse", StringComparison.OrdinalIgnoreCase))
                    return w;
            }
            return null;
        }

        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        const int MOUSEEVENTF_LEFTDOWN = 0x02;
        const int MOUSEEVENTF_LEFTUP = 0x04;
        const int MOUSEEVENTF_RIGHTDOWN = 0x08;
        const int MOUSEEVENTF_RIGHTUP = 0x10;

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
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                    fileName.Contains(extension, StringComparison.OrdinalIgnoreCase))
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
