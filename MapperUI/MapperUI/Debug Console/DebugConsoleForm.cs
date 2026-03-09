using MapperUI.Services;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace MapperUI
{
    /// <summary>
    /// Non-modal dark terminal window showing all Mapper log activity.
    /// Open via Build > Debug Console.
    /// 
    /// On construction it:
    ///   1. Subscribes to MapperLogger.OnEntry for live updates.
    ///   2. Replays MapperLogger.RecentEntries so entries logged BEFORE the
    ///      console was opened are still visible.
    /// </summary>
    public partial class DebugConsoleForm : Form
    {
        public DebugConsoleForm()
        {
            InitializeComponent();

            // 1. Subscribe for live updates
            MapperLogger.OnEntry += OnLogEntry;
            FormClosed += (_, __) => MapperLogger.OnEntry -= OnLogEntry;

            // 2. Replay buffered entries so nothing is missed
            foreach (var entry in MapperLogger.RecentEntries)
                OnLogEntry(entry);
        }

        // ── Position below the main form ────────────────────────────────────

        public void PositionBelow(Form owner)
        {
            var screen = Screen.FromControl(owner).WorkingArea;
            int x = Math.Max(screen.Left, owner.Left);
            int y = Math.Min(screen.Bottom - Height, owner.Bottom + 4);
            Location = new System.Drawing.Point(x, y);
            Width = Math.Min(owner.Width, screen.Width);
        }

        // ── Log entry handler ────────────────────────────────────────────────

        private void OnLogEntry(LogEntry entry)
        {
            if (dgvLog.InvokeRequired) { dgvLog.Invoke(() => OnLogEntry(entry)); return; }

            var color = entry.Step switch
            {
                LogStep.ERROR => Color.OrangeRed,
                LogStep.WARN => Color.Yellow,
                LogStep.REMAP => Color.LimeGreen,
                LogStep.WRITE => Color.DeepSkyBlue,
                LogStep.TOUCH => Color.Plum,
                LogStep.VALIDATE => Color.Aquamarine,
                LogStep.DIFF => Color.LightSkyBlue,
                LogStep.PARSE => Color.LightSalmon,
                _ => Color.LimeGreen
            };

            var idx = dgvLog.Rows.Add(
                entry.Timestamp.ToString("HH:mm:ss.fff"),
                entry.Step.ToString(),
                entry.Action);

            dgvLog.Rows[idx].DefaultCellStyle.ForeColor = color;
            dgvLog.FirstDisplayedScrollingRowIndex = dgvLog.Rows.Count - 1;

            if (dgvLog.Rows.Count > 2000)
                dgvLog.Rows.RemoveAt(0);
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            dgvLog.Rows.Clear();
            MapperLogger.Info("Log cleared.");
        }
    }
}