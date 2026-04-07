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

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(int f, int dx, int dy, int b, int e);
        const int SW_RESTORE = 9;
        const int MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04;
        const int MOUSEEVENTF_RIGHTDOWN = 0x08, MOUSEEVENTF_RIGHTUP = 0x10;

        static Action<string>? _log;

        static void Log(string msg)
        {
            _log?.Invoke(msg);
            MapperLogger.Info(msg);
        }

        public static EaeImportResult Import(MapperConfig cfg, List<VueOneComponent> components,
            Action<string>? onProgress = null)
        {
            _log = onProgress;
            var result = new EaeImportResult();
            var libPath = cfg.TemplateLibraryPath;

            if (string.IsNullOrWhiteSpace(libPath) || !Directory.Exists(libPath))
                throw new DirectoryNotFoundException($"Template Library not found: {libPath}");

            Log("Preparing templates...");
            var stagingDir = Path.Combine(Path.GetTempPath(), "VueOneMapper_Import");
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
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
            Log($"Staged {exportFiles.Count} template(s). Connecting to EAE...");

            var eaeProcess = FindEaeProcess();
            if (eaeProcess == null)
            {
                result.Warnings.Add("EAE is not running.");
                return result;
            }

            var hwnd = eaeProcess.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                result.Warnings.Add("EAE window not found.");
                return result;
            }

            Log($"Connected: {eaeProcess.MainWindowTitle}");

            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            Thread.Sleep(500);

            var eaeWindow = AutomationElement.FromHandle(hwnd);
            var projectNode = FindProjectNode(eaeWindow);

            if (projectNode == null)
            {
                result.Warnings.Add("Could not find project node in EAE.");
                return result;
            }

            Log($"Project node: '{projectNode.Current.Name}'");

            for (int i = 0; i < exportFiles.Count; i++)
            {
                var exportFile = exportFiles[i];
                var fileName = Path.GetFileName(exportFile);
                Log($"[{i + 1}/{exportFiles.Count}] Importing {fileName}...");

                SetForegroundWindow(hwnd);
                Thread.Sleep(300);

                bool ok = DoImport(hwnd, projectNode, exportFile);
                if (ok)
                {
                    result.ImportedCount++;
                    Log($"[{i + 1}/{exportFiles.Count}] Done: {fileName}");
                    Thread.Sleep(1500);
                }
                else
                {
                    result.Warnings.Add($"Failed: {fileName}");
                    Log($"[{i + 1}/{exportFiles.Count}] FAILED: {fileName}");
                }
            }

            result.Success = result.ImportedCount > 0;
            return result;
        }

        static bool DoImport(IntPtr hwnd, AutomationElement projectNode, string exportFilePath)
        {
            try
            {
                var rect = projectNode.Current.BoundingRectangle;
                if (rect.IsEmpty)
                {
                    Log("  Project node has no bounds — cannot click.");
                    return false;
                }

                int cx = (int)(rect.X + rect.Width / 3);
                int cy = (int)(rect.Y + rect.Height / 2);

                Log($"  Left-clicking project at ({cx}, {cy})...");
                SetCursorPos(cx, cy);
                Thread.Sleep(100);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                Thread.Sleep(30);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                Thread.Sleep(300);

                Log("  Right-clicking for context menu...");
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                Thread.Sleep(50);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                Thread.Sleep(1000);

                bool foundImport = TryClickImportInMenu();

                if (!foundImport)
                {
                    Log("  Import not found via UIAutomation. Trying keyboard 'I'...");
                    SendKeys.SendWait("i");
                    Thread.Sleep(500);

                    foundImport = IsFileDialogOpen();
                    if (!foundImport)
                    {
                        Log("  Keyboard 'I' didn't work. Trying 'p' then 'i'...");
                        SendKeys.SendWait("{ESCAPE}");
                        Thread.Sleep(300);

                        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                        Thread.Sleep(50);
                        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                        Thread.Sleep(1000);

                        SendKeys.SendWait("p");
                        Thread.Sleep(500);

                        var menus2 = AutomationElement.RootElement.FindAll(TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
                        Log($"  Menus after 'p': {menus2.Count}");

                        SendKeys.SendWait("i");
                        Thread.Sleep(500);
                        foundImport = IsFileDialogOpen();
                    }
                }

                if (!foundImport)
                {
                    Log("  Could not trigger Import. Dismissing menu...");
                    SendKeys.SendWait("{ESCAPE}");
                    Thread.Sleep(200);
                    SendKeys.SendWait("{ESCAPE}");
                    return false;
                }

                Log("  File dialog detected. Filling path...");
                return FillFileDialog(exportFilePath);
            }
            catch (Exception ex)
            {
                Log($"  Error: {ex.Message}");
                try { SendKeys.SendWait("{ESCAPE}"); } catch { }
                return false;
            }
        }

        static bool TryClickImportInMenu()
        {
            var menus = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));

            Log($"  Found {menus.Count} menu(s).");

            foreach (AutomationElement menu in menus)
            {
                var items = menu.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

                Log($"  Menu has {items.Count} item(s):");
                foreach (AutomationElement item in items)
                {
                    Log($"    '{item.Current.Name}'");

                    if (item.Current.Name.Equals("Import", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("  Clicking 'Import'...");
                        if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                            ((InvokePattern)ip).Invoke();
                        else
                            ClickAt(item.Current.BoundingRectangle);

                        Thread.Sleep(1000);
                        return true;
                    }
                }
            }

            var popups = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            Log($"  Top-level windows: {popups.Count}");
            foreach (AutomationElement popup in popups)
                Log($"    Window: '{popup.Current.Name}' class='{popup.Current.ClassName}'");

            return false;
        }

        static bool IsFileDialogOpen()
        {
            return FindFileDialog() != null;
        }

        static bool FillFileDialog(string filePath)
        {
            for (int wait = 0; wait < 40; wait++)
            {
                Thread.Sleep(150);
                var dialog = FindFileDialog();
                if (dialog == null) continue;

                Log($"  Dialog: '{dialog.Current.Name}'");

                var edits = dialog.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

                AutomationElement? fileBox = null;
                foreach (AutomationElement edit in edits)
                {
                    if (edit.Current.Name.Contains("name", StringComparison.OrdinalIgnoreCase))
                    { fileBox = edit; break; }
                }
                if (fileBox == null && edits.Count > 0)
                    fileBox = edits[edits.Count - 1];

                if (fileBox == null) { Log("  No filename box found."); continue; }

                if (fileBox.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                    ((ValuePattern)vp).SetValue(filePath);
                else
                {
                    fileBox.SetFocus();
                    Thread.Sleep(100);
                    SendKeys.SendWait("^a");
                    Thread.Sleep(50);
                    SendKeys.SendWait(EscapeSendKeys(filePath));
                }

                Thread.Sleep(300);

                var buttons = dialog.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement btn in buttons)
                {
                    if (btn.Current.Name.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
                        btn.Current.Name.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                            ((InvokePattern)ip).Invoke();
                        else
                            ClickAt(btn.Current.BoundingRectangle);
                        Thread.Sleep(3000);
                        return true;
                    }
                }

                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(3000);
                return true;
            }

            Log("  File dialog did not appear.");
            return false;
        }

        static AutomationElement? FindFileDialog()
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            foreach (AutomationElement w in windows)
            {
                var name = w.Current.Name;
                var cls = w.Current.ClassName;
                if ((name.Contains("Open", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Import", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Select", StringComparison.OrdinalIgnoreCase) ||
                     cls == "#32770") &&
                    w.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)) != null)
                    return w;
            }
            return null;
        }

        static AutomationElement? FindProjectNode(AutomationElement eaeWindow)
        {
            var trees = eaeWindow.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree));
            Log($"Found {trees.Count} tree control(s).");

            foreach (AutomationElement tree in trees)
            {
                var topItems = tree.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));

                foreach (AutomationElement item in topItems)
                {
                    var name = item.Current.Name;
                    Log($"  Tree root: '{name}'");

                    if (name.Contains("Solution", StringComparison.OrdinalIgnoreCase))
                    {
                        if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ecp))
                        {
                            var state = ((ExpandCollapsePattern)ecp).Current.ExpandCollapseState;
                            if (state == ExpandCollapseState.Collapsed)
                                ((ExpandCollapsePattern)ecp).Expand();
                        }
                        Thread.Sleep(300);

                        var children = item.FindAll(TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));

                        foreach (AutomationElement child in children)
                        {
                            Log($"    Child: '{child.Current.Name}'");
                            if (!child.Current.Name.Contains("Librar", StringComparison.OrdinalIgnoreCase))
                                return child;
                        }
                        return item;
                    }
                }
            }

            Log("No Solution node found. Scanning all tree items...");
            var allItems = eaeWindow.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
            foreach (AutomationElement ti in allItems)
            {
                Log($"  TreeItem: '{ti.Current.Name}'");
                if (ti.Current.Name.Contains("Demonstr", StringComparison.OrdinalIgnoreCase) ||
                    ti.Current.Name.Contains("Station", StringComparison.OrdinalIgnoreCase))
                    return ti;
            }

            return null;
        }

        static void ClickAt(System.Windows.Rect rect)
        {
            if (rect.IsEmpty) return;
            SetCursorPos((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }

        static string EscapeSendKeys(string s) =>
            s.Replace("{", "{{").Replace("}", "}}").Replace("+", "{+}")
             .Replace("^", "{^}").Replace("%", "{%}").Replace("~", "{~}");

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
            var files = new List<string>();
            int step = 1;
            foreach (var basic in neededBasics.OrderBy(b => b))
            {
                var zip = FindPackage(libPath, "Basic", basic, ".Basic");
                if (zip == null) { result.Warnings.Add($"Basic not found: {basic}"); continue; }
                var dir = Path.Combine(stagingDir, $"{step:D2}_Basic_{basic}");
                Directory.CreateDirectory(dir);
                ExtractZip(zip, dir);
                var ef = Directory.GetFiles(dir, "*.export", SearchOption.AllDirectories).FirstOrDefault();
                if (ef != null) files.Add(ef);
                step++;
            }
            foreach (var cat in neededCats.OrderBy(c => c))
            {
                var zip = FindPackage(libPath, "CAT", cat, ".cat");
                if (zip == null) { result.Warnings.Add($"CAT not found: {cat}"); continue; }
                var dir = Path.Combine(stagingDir, $"{step:D2}_CAT_{cat}");
                Directory.CreateDirectory(dir);
                ExtractZip(zip, dir);
                var ef = Directory.GetFiles(dir, "*.export", SearchOption.AllDirectories).FirstOrDefault();
                if (ef != null) files.Add(ef);
                step++;
            }
            return files;
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

        static void ExtractZip(string zipPath, string targetDir)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var path = Path.Combine(targetDir, entry.FullName);
                var folder = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                entry.ExtractToFile(path, true);
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
