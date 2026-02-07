using System;
using System.Windows.Forms;

namespace MapperUI
{
    public partial class MainForm : Form
    {
        public MainForm()
        {
            InitializeComponent();
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*";
                dialog.Title = "Select VueOne Control.xml";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtModelPath.Text = dialog.FileName;
                    LoadVueOneModel(dialog.FileName);
                }
            }
        }

        private void LoadVueOneModel(string filePath)
        {
            try
            {
                lblStatus.Text = "Loading model...";
                statusStrip.Refresh();

                // TODO: Use CodeGen classes here
                // var parser = new CodeGen.Parser();
                // var system = parser.Parse(filePath);

                // Mock data for now
                dgvComponents.Rows.Clear();
                dgvComponents.Rows.Add("Feeder", "Actuator", "FiveStatePneumatic");
                dgvComponents.Rows.Add("Transfer", "Actuator", "FiveStatePneumatic");
                dgvComponents.Rows.Add("Assembly_Station", "Process", "Process");

                lblStatus.Text = $"Loaded: {System.IO.Path.GetFileName(filePath)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Error loading model";
            }
        }
    }
}