using CodeGen.Configuration;
using CodeGen.Mapping;
using CodeGen.Models;
using CodeGen.Translation;
using CodeGen.Translation.Process;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MapperUI
{
    public sealed class StateTransitionTableForm : Form
    {
        readonly Label _header = new();
        readonly DataGridView _recipeGrid = CreateGrid();
        readonly DataGridView _transitionGrid = CreateGrid();
        readonly DataGridView _notesGrid = CreateGrid();

        public StateTransitionTableForm(string controlXmlPath,
            IReadOnlyList<VueOneComponent> components)
        {
            Text = "State-Transition Table";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1280, 760);
            MinimumSize = new Size(960, 560);

            _header.Dock = DockStyle.Top;
            _header.Height = 34;
            _header.Padding = new Padding(10, 8, 10, 4);
            _header.AutoEllipsis = true;
            _header.Font = new Font("Segoe UI", 9F, FontStyle.Bold);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(CreateTab("Recipe Data Array", _recipeGrid));
            tabs.TabPages.Add(CreateTab("Control.xml Transition Table", _transitionGrid));
            tabs.TabPages.Add(CreateTab("Generator Notes", _notesGrid));

            Controls.Add(tabs);
            Controls.Add(_header);

            Reload(controlXmlPath, components);
        }

        public void Reload(string controlXmlPath, IReadOnlyList<VueOneComponent> components)
        {
            _header.Text = $"Source: {Path.GetFileName(controlXmlPath)}   ({controlXmlPath})";

            var snapshot = StateTransitionTableBuilder.Build(components);
            _recipeGrid.DataSource = snapshot.RecipeRows;
            _transitionGrid.DataSource = snapshot.TransitionRows;
            _notesGrid.DataSource = snapshot.Notes;

            // The CSV snapshot export is a CONVENIENCE — the three grids are already populated
            // above. A failure to write the snapshot folder (path/permission/missing dir) must NOT
            // take down the whole table view, so swallow it and just note it in the header. This is
            // what threw the FileNotFound* unhandled crash that hid the table.
            try
            {
                var snapshotDir = StateTransitionTableExporter.Save(controlXmlPath, snapshot);
                _header.Text =
                    $"Source: {Path.GetFileName(controlXmlPath)}   Saved snapshot: {snapshotDir}";
            }
            catch (Exception ex)
            {
                _header.Text =
                    $"Source: {Path.GetFileName(controlXmlPath)}   (snapshot not saved: {ex.GetType().Name})";
            }

            AutoSizeUsefulColumns(_recipeGrid);
            AutoSizeUsefulColumns(_transitionGrid);
            AutoSizeUsefulColumns(_notesGrid);
        }

        static TabPage CreateTab(string title, Control content)
        {
            var page = new TabPage(title);
            page.Controls.Add(content);
            return page;
        }

        static DataGridView CreateGrid()
        {
            return new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = true,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
            };
        }

        static void AutoSizeUsefulColumns(DataGridView grid)
        {
            foreach (DataGridViewColumn col in grid.Columns)
            {
                col.SortMode = DataGridViewColumnSortMode.Automatic;
                col.MinimumWidth = 70;
                if (col.Name.Contains("Condition", StringComparison.OrdinalIgnoreCase) ||
                    col.Name.Contains("Transition", StringComparison.OrdinalIgnoreCase) ||
                    col.Name.Contains("Message", StringComparison.OrdinalIgnoreCase))
                    col.Width = 260;
                else if (col.Name.Contains("State", StringComparison.OrdinalIgnoreCase) ||
                         col.Name.Contains("Target", StringComparison.OrdinalIgnoreCase))
                    col.Width = 150;
                else
                    col.Width = 110;
            }
        }
    }

    static class StateTransitionTableBuilder
    {
        public sealed record Snapshot(
            DataTable RecipeRows,
            DataTable TransitionRows,
            DataTable Notes);

        public static Snapshot Build(IReadOnlyList<VueOneComponent> components)
        {
            var recipeRows = CreateRecipeTable();
            var transitionRows = CreateTransitionTable();
            var notes = CreateNotesTable();

            var processes = components
                .Where(c => string.Equals(c.Type, "Process", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var feedProcess = SystemInjector.FindStation1Process(components.ToList());

            bool previousSimulatorMode = MapperConfig.SimulatorRecipeMode;
            try
            {
                MapperConfig.SimulatorRecipeMode = true;
                foreach (var process in processes)
                {
                    AddTransitionRows(transitionRows, process, components);
                    AddRecipeRows(recipeRows, notes, process, components, feedProcess);
                }
            }
            finally
            {
                MapperConfig.SimulatorRecipeMode = previousSimulatorMode;
            }

            return new Snapshot(recipeRows, transitionRows, notes);
        }

        static void AddTransitionRows(DataTable table, VueOneComponent process,
            IReadOnlyList<VueOneComponent> components)
        {
            int stateIndex = 0;
            foreach (var state in process.States)
            {
                if (state.Transitions.Count == 0)
                {
                    AddTransitionRow(table, process, stateIndex, state, null, null, 0, components);
                    stateIndex++;
                    continue;
                }

                foreach (var transition in state.Transitions)
                {
                    if (transition.Conditions.Count == 0)
                    {
                        AddTransitionRow(table, process, stateIndex, state, transition, null, 0, components);
                        continue;
                    }

                    for (int i = 0; i < transition.Conditions.Count; i++)
                        AddTransitionRow(table, process, stateIndex, state, transition,
                            transition.Conditions[i], i + 1, components);
                }
                stateIndex++;
            }
        }

        static void AddTransitionRow(DataTable table, VueOneComponent process,
            int stateIndex, VueOneState state, VueOneTransition? transition,
            VueOneCondition? condition, int conditionIndex,
            IReadOnlyList<VueOneComponent> components)
        {
            var destState = transition == null
                ? null
                : process.States.FirstOrDefault(s =>
                    string.Equals(s.StateID, transition.DestinationStateID,
                        StringComparison.OrdinalIgnoreCase));
            var target = condition == null ? null : LookupComponent(condition.ComponentID, components);
            var targetState = condition == null || target == null
                ? null
                : target.States.FirstOrDefault(s =>
                    string.Equals(s.StateID, condition.ID, StringComparison.OrdinalIgnoreCase));

            table.Rows.Add(
                StationOf(process),
                process.Name,
                stateIndex,
                state.InitialState ? "Yes" : "",
                state.Name,
                state.StateNumber,
                transition?.TransitionType ?? "",
                destState?.Name ?? transition?.DestinationStateID ?? "END",
                conditionIndex == 0 ? "" : conditionIndex.ToString(),
                condition?.Name ?? "",
                target?.Name ?? "",
                target?.Type ?? "",
                targetState?.Name ?? "",
                targetState?.StateNumber.ToString() ?? "",
                condition?.Operator ?? "");
        }

        static void AddRecipeRows(DataTable table, DataTable notes,
            VueOneComponent process, IReadOnlyList<VueOneComponent> components,
            VueOneComponent? feedProcess)
        {
            var contents = BuildGlobalContents(process, components);
            bool commandFromCondition = !SameComponent(process, feedProcess);
            int processId = 1000 + table.Rows.Count;

            RecipeArrays recipe;
            try
            {
                recipe = ProcessRecipeArrayGenerator.Generate(
                    process, contents, components, processId, commandFromCondition);
            }
            catch (Exception ex)
            {
                notes.Rows.Add(StationOf(process), process.Name, "Error", ex.Message);
                return;
            }

            var idToComponent = recipe.ComponentRegistry
                .Select(kv => new
                {
                    Id = kv.Value,
                    Component = LookupComponent(kv.Key, components)
                })
                .Where(x => x.Component != null)
                .ToDictionary(x => x.Id, x => x.Component!);

            for (int i = 0; i < recipe.StepType.Count; i++)
            {
                bool isWait = recipe.StepType[i] == 2;
                var waitTarget = isWait && idToComponent.TryGetValue(recipe.Wait1Id[i], out var waitComp)
                    ? waitComp
                    : null;
                var cmdTarget = components.FirstOrDefault(c =>
                    string.Equals(c.Name, recipe.CmdTargetName[i],
                        StringComparison.OrdinalIgnoreCase));

                table.Rows.Add(
                    StationOf(process),
                    process.Name,
                    i,
                    StepTypeName(recipe.StepType[i]),
                    recipe.StepType[i],
                    recipe.CmdTargetName[i],
                    recipe.CmdStateArr[i],
                    CommandMeaning(cmdTarget, recipe.CmdStateArr[i]),
                    isWait ? recipe.Wait1Id[i] : DBNull.Value,
                    waitTarget?.Name ?? "",
                    isWait ? recipe.Wait1State[i] : DBNull.Value,
                    isWait ? WaitMeaning(waitTarget, recipe.Wait1State[i]) : "",
                    recipe.NextStep[i]);
            }

            foreach (var line in recipe.SkippedConditions)
                notes.Rows.Add(StationOf(process), process.Name, "Skipped", line);
            foreach (var line in recipe.Warnings)
                notes.Rows.Add(StationOf(process), process.Name, "Warning", line);
            foreach (var line in recipe.TransitionTable)
                notes.Rows.Add(StationOf(process), process.Name, "TransitionChain", line);
        }

        static StationContents BuildGlobalContents(VueOneComponent process,
            IReadOnlyList<VueOneComponent> components)
        {
            var sensors = components
                .Where(c => string.Equals(c.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var actuators = components
                .Where(c =>
                    string.Equals(c.Type, "Actuator", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Type, "Robot", StringComparison.OrdinalIgnoreCase))
                .Where(c => c.States.Count > 0)
                .ToList();
            return new StationContents(process, actuators, sensors);
        }

        static DataTable CreateRecipeTable()
        {
            var table = new DataTable();
            table.Columns.Add("Station");
            table.Columns.Add("Process");
            table.Columns.Add("Row", typeof(int));
            table.Columns.Add("Step");
            table.Columns.Add("StepType", typeof(int));
            table.Columns.Add("CmdTargetName");
            table.Columns.Add("CmdStateArr", typeof(int));
            table.Columns.Add("CmdMeaning");
            table.Columns.Add("Wait1Id", typeof(int));
            table.Columns.Add("WaitTarget");
            table.Columns.Add("Wait1State", typeof(int));
            table.Columns.Add("WaitMeaning");
            table.Columns.Add("NextStep", typeof(int));
            return table;
        }

        static DataTable CreateTransitionTable()
        {
            var table = new DataTable();
            table.Columns.Add("Station");
            table.Columns.Add("Process");
            table.Columns.Add("StateIndex", typeof(int));
            table.Columns.Add("Initial");
            table.Columns.Add("SourceState");
            table.Columns.Add("SourceStateNumber", typeof(int));
            table.Columns.Add("TransitionType");
            table.Columns.Add("DestinationState");
            table.Columns.Add("ConditionIndex");
            table.Columns.Add("ConditionName");
            table.Columns.Add("ConditionComponent");
            table.Columns.Add("ConditionComponentType");
            table.Columns.Add("ConditionState");
            table.Columns.Add("ConditionStateNumber");
            table.Columns.Add("Operator");
            return table;
        }

        static DataTable CreateNotesTable()
        {
            var table = new DataTable();
            table.Columns.Add("Station");
            table.Columns.Add("Process");
            table.Columns.Add("Type");
            table.Columns.Add("Message");
            return table;
        }

        static string StepTypeName(int stepType) => stepType switch
        {
            1 => "CMD",
            2 => "WAIT",
            9 => "END",
            _ => $"UNKNOWN {stepType}",
        };

        static string CommandMeaning(VueOneComponent? component, int cmdState)
        {
            if (component == null || cmdState == 0) return "";
            if (IsSevenState(component))
                return cmdState switch
                {
                    1 => "Pick / Work1",
                    3 => "Place / Work2",
                    5 => "Home / Centre",
                    _ => $"Seven cmd {cmdState}",
                };
            return cmdState switch
            {
                1 => "toWork",
                3 => "toHome",
                _ => $"cmd {cmdState}",
            };
        }

        static string WaitMeaning(VueOneComponent? component, int waitState)
        {
            if (component == null) return waitState == 0 ? "" : $"state {waitState}";
            if (string.Equals(component.Type, "Sensor", StringComparison.OrdinalIgnoreCase))
                return waitState == 1 ? "On / TRUE" : $"sensor state {waitState}";
            if (IsSevenState(component))
                return waitState switch
                {
                    0 => "AtHomeInit",
                    2 => "AtPick / Work1",
                    4 => "AtPlace / Work2",
                    6 => "AtHome",
                    _ => $"state {waitState}",
                };
            return waitState switch
            {
                0 => "AtHomeInit / settled",
                2 => "AtWork",
                4 => "AtHomeEnd",
                _ => $"state {waitState}",
            };
        }

        static bool IsSevenState(VueOneComponent component) =>
            !MapperConfig.StubSevenStateActuatorsAsFiveState &&
            (component.States.Count == 7 || TemplateMap.IsBranchedSevenState(component));

        static VueOneComponent? LookupComponent(string? componentId,
            IReadOnlyList<VueOneComponent> components)
        {
            if (string.IsNullOrWhiteSpace(componentId)) return null;
            return components.FirstOrDefault(c =>
                string.Equals(c.ComponentID, componentId.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        static bool SameComponent(VueOneComponent? left, VueOneComponent? right)
        {
            if (left == null || right == null) return false;
            return string.Equals(left.ComponentID, right.ComponentID,
                StringComparison.OrdinalIgnoreCase);
        }

        static string StationOf(VueOneComponent process)
        {
            var name = process.Name ?? string.Empty;
            if (name.Equals("Feed_Station", StringComparison.OrdinalIgnoreCase))
                return "Station 1 / M262";
            if (name.Equals("Assembly_Station", StringComparison.OrdinalIgnoreCase))
                return "Station 2 / M580";
            if (name.Contains("Disassembly", StringComparison.OrdinalIgnoreCase))
                return "Station 2 / M580";
            return "Process";
        }
    }

    static class StateTransitionTableExporter
    {
        public static string Save(string controlXmlPath,
            StateTransitionTableBuilder.Snapshot snapshot)
        {
            var root = ResolveExportRoot();
            Directory.CreateDirectory(root);

            string stem = Path.GetFileNameWithoutExtension(controlXmlPath);
            if (string.IsNullOrWhiteSpace(stem)) stem = "Control";
            stem = SanitizeFileName(stem);

            string dir = Path.Combine(root, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(dir);

            WriteCsv(Path.Combine(dir, "recipe-data-array.csv"), snapshot.RecipeRows);
            WriteCsv(Path.Combine(dir, "controlxml-transition-table.csv"), snapshot.TransitionRows);
            WriteCsv(Path.Combine(dir, "generator-notes.csv"), snapshot.Notes);

            File.WriteAllText(Path.Combine(dir, "metadata.txt"),
                "VueOneMapper State-Transition Table Snapshot" + Environment.NewLine +
                $"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                $"Source: {controlXmlPath}" + Environment.NewLine,
                Encoding.UTF8);

            return dir;
        }

        static string ResolveExportRoot()
        {
            try
            {
                var root = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "StateTransitionTables"));
                var parent = Directory.GetParent(root)?.FullName;
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    return root;
            }
            catch
            {
                // Fall back below.
            }
            return Path.Combine(AppContext.BaseDirectory, "StateTransitionTables");
        }

        static void WriteCsv(string path, DataTable table)
        {
            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.WriteLine(string.Join(",", table.Columns.Cast<DataColumn>()
                .Select(c => Escape(c.ColumnName))));
            foreach (DataRow row in table.Rows)
            {
                writer.WriteLine(string.Join(",", table.Columns.Cast<DataColumn>()
                    .Select(c => Escape(row[c]?.ToString() ?? string.Empty))));
            }
        }

        static string Escape(string value)
        {
            value ??= string.Empty;
            bool quote = value.Contains(',') || value.Contains('"') ||
                         value.Contains('\r') || value.Contains('\n');
            value = value.Replace("\"", "\"\"");
            return quote ? $"\"{value}\"" : value;
        }

        static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
