using CodeGen.Configuration;
using CodeGen.Domain.Twin;
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
            IReadOnlyList<VueOneComponent> components,
            CodeGen.Configuration.CompilerConfiguration config,
            CodeGen.Mapping.DeploymentProfile profile)
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

            Reload(controlXmlPath, components, config, profile);
        }

        public void Reload(string controlXmlPath, IReadOnlyList<VueOneComponent> components,
            CodeGen.Configuration.CompilerConfiguration config, CodeGen.Mapping.DeploymentProfile profile)
        {
            _header.Text = $"Source: {Path.GetFileName(controlXmlPath)}   ({controlXmlPath})";

            var snapshot = StateTransitionTableBuilder.Build(components, config, profile);
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

        // The configuration and the placement are the RUN's, handed in. Constructing a default
        // MapperConfig here planned with no instance-name overrides and an empty template library, and
        // AsPlaced discarded the Device column - so the preview showed a project Generate never writes.
        public static Snapshot Build(IReadOnlyList<VueOneComponent> components,
            CodeGen.Configuration.CompilerConfiguration config, CodeGen.Mapping.DeploymentProfile profile)
        {
            var recipeRows = CreateRecipeTable();
            var transitionRows = CreateTransitionTable();
            var notes = CreateNotesTable();

            // The preview shows what generation will produce, so it reads the SAME plan rather than
            // re-deriving an allocation, a slot map and a recipe of its own.
            CodeGen.Translation.GenerationContext? plan = null;
            string? planError = null;
            try
            {
                // The RUN's snapshot, handed in. Loading a second one here would let the preview show
                // a project compiled against declarations the run never used.
                plan = CodeGen.Translation.GenerationContext.Plan(config, components, profile);
            }
            catch (Exception ex) { planError = ex.Message; }

            // The plan already resolved the twin; a second TwinModel over the same components would be
            // a second answer to every reference this table renders.
            var twin = plan?.Twin ?? TwinModel.Build(components, config.Twin);

            foreach (var process in twin.Processes.Select(p => p.Source))
            {
                AddTransitionRows(transitionRows, plan, process, components, twin);
                AddRecipeRows(recipeRows, notes, process, components, twin, plan, planError);
            }

            return new Snapshot(recipeRows, transitionRows, notes);
        }

        static void AddTransitionRows(DataTable table, CodeGen.Translation.GenerationContext? plan, VueOneComponent process,
            IReadOnlyList<VueOneComponent> components, TwinModel twin)
        {
            int stateIndex = 0;
            foreach (var state in process.States)
            {
                if (state.Transitions.Count == 0)
                {
                    AddTransitionRow(table, plan, process, stateIndex, state, null, null, 0, components, twin);
                    stateIndex++;
                    continue;
                }

                foreach (var transition in state.Transitions)
                {
                    if (transition.Conditions.Count == 0)
                    {
                        AddTransitionRow(table, plan, process, stateIndex, state, transition, null, 0, components, twin);
                        continue;
                    }

                    for (int i = 0; i < transition.Conditions.Count; i++)
                        AddTransitionRow(table, plan, process, stateIndex, state, transition,
                            transition.Conditions[i], i + 1, components, twin);
                }
                stateIndex++;
            }
        }

        static void AddTransitionRow(DataTable table, CodeGen.Translation.GenerationContext? plan, VueOneComponent process,
            int stateIndex, VueOneState state, VueOneTransition? transition,
            VueOneCondition? condition, int conditionIndex,
            IReadOnlyList<VueOneComponent> components, TwinModel twin)
        {
            var destState = transition == null
                ? null
                : process.States.FirstOrDefault(s =>
                    string.Equals(s.StateID, transition.DestinationStateID,
                        StringComparison.OrdinalIgnoreCase));
            var target = condition == null ? null : twin.ById(condition.ComponentID)?.Source;
            var targetState = condition == null || target == null
                ? null
                : target.States.FirstOrDefault(s =>
                    string.Equals(s.StateID, condition.ID, StringComparison.OrdinalIgnoreCase));

            table.Rows.Add(
                StationOf(process, plan),
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
            VueOneComponent process, IReadOnlyList<VueOneComponent> components, TwinModel twin,
            CodeGen.Translation.GenerationContext? plan, string? planError)
        {
            if (plan == null || !plan.Recipes.TryGetValue(process.Name?.Trim() ?? string.Empty, out var recipe))
            {
                notes.Rows.Add(StationOf(process, plan), process.Name, "Error",
                    planError ?? "the plan compiled no recipe for this process");
                return;
            }

            var idToComponent = recipe.ComponentIds
                .Select(kv => new
                {
                    Id = kv.Value,
                    Component = twin.ById(kv.Key)?.Source
                })
                .Where(x => x.Component != null)
                .ToDictionary(x => x.Id, x => x.Component!);

            for (int i = 0; i < recipe.StepType.Count; i++)
            {
                bool isWait = recipe.StepType[i] == StepType.Wait;
                var waitTarget = isWait && idToComponent.TryGetValue(recipe.Wait1Id[i], out var waitComp)
                    ? waitComp
                    : null;
                var cmdTarget = components.FirstOrDefault(c =>
                    string.Equals(c.Name, recipe.CmdTargetName[i],
                        StringComparison.OrdinalIgnoreCase));

                table.Rows.Add(
                    StationOf(process, plan),
                    process.Name,
                    i,
                    StepTypeName(recipe.StepType[i]),
                    recipe.StepType[i],
                    recipe.CmdTargetName[i],
                    recipe.CmdStateArr[i],
                    CommandMeaning(cmdTarget, recipe.CmdStateArr[i], plan),
                    isWait ? recipe.Wait1Id[i] : DBNull.Value,
                    waitTarget?.Name ?? "",
                    isWait ? recipe.Wait1State[i] : DBNull.Value,
                    isWait ? WaitMeaning(waitTarget, recipe.Wait1State[i], plan) : "",
                    recipe.NextStep[i]);
            }

            foreach (var line in recipe.Warnings)
                notes.Rows.Add(StationOf(process, plan), process.Name, "Warning", line);
            foreach (var line in recipe.TransitionTable)
                notes.Rows.Add(StationOf(process, plan), process.Name, "TransitionChain", line);
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

        // What a recipe row IS. The engine's three step kinds are protocol, so they are read from the
        // one place that declares them rather than re-listed for a grid.
        static string StepTypeName(int stepType) =>
            stepType == StepType.Cmd ? "CMD"
            : stepType == StepType.Wait ? "WAIT"
            : stepType == StepType.End ? "END"
            : $"UNKNOWN {stepType}";

        // What a command VALUE means is the CAT's declaration - which stop that value drives it to -
        // so it is asked of the protocol the plan selected for this component. Three tables of
        // Pick/Place/Home used to live here and could disagree with the CAT the run actually deployed.
        static string CommandMeaning(VueOneComponent? component, int cmdState,
            CodeGen.Translation.GenerationContext? plan)
        {
            if (component == null || cmdState == 0) return "";
            var stop = Protocol(component, plan)?.Command
                .FirstOrDefault(kv => kv.Value == cmdState).Key;
            return stop ?? $"cmd {cmdState}";
        }

        // What a WAIT value means: for a sensor the twin's own state name, for an actuator the stop the
        // CAT publishes that value at.
        static string WaitMeaning(VueOneComponent? component, int waitState,
            CodeGen.Translation.GenerationContext? plan)
        {
            if (component == null) return waitState == 0 ? "" : $"state {waitState}";
            if (ComponentType.IsSensor(component))
                return component.States.FirstOrDefault(s => s.StateNumber == waitState)?.Name
                       ?? $"sensor state {waitState}";
            return Protocol(component, plan)?.StopFor(waitState) ?? $"state {waitState}";
        }

        // The CAT the PLAN selected, never one re-derived here from how the component's states look.
        static CodeGen.Configuration.CatProtocolDeclaration? Protocol(VueOneComponent component,
            CodeGen.Translation.GenerationContext? plan) =>
            plan != null && plan.CatTypes.TryGetValue((component.Name ?? string.Empty).Trim(), out var cat)
                ? plan.Manifest.ProtocolOrNull(cat)
                : null;

        // Where the plan runs this process. A name-recognition table here would report the twin's own
        // station wrongly the moment a model renamed one.
        static string StationOf(VueOneComponent process,
            CodeGen.Translation.GenerationContext? plan)
        {
            var plc = plan?.Allocation.Of(process.Name) ?? CodeGen.Translation.PlcAssignment.Unknown;
            return plc == CodeGen.Translation.PlcAssignment.Unknown ? "Process" : plc.ToString();
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
