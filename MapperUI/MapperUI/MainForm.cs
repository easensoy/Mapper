using System;
using System.Windows.Forms;
using MapperUI.Services;

namespace MapperUI
{
    public partial class MainForm : Form
    {
        private readonly MapperService _mapperService;

        public MainForm()
        {
            InitializeComponent();
            _mapperService = new MapperService();
        }

        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            txtOutput.Clear();
            AppendOutput("Starting VueOne to IEC 61499 mapping...\n");

            var result = await _mapperService.RunMapping();

            if (!result.Success)
            {
                AppendOutput($"\n❌ FAILED: {result.ErrorMessage}\n", true);

                if (result.ValidationResult != null)
                {
                    AppendValidationResults(result.ValidationResult);
                }
                return;
            }

            AppendValidationResults(result.ValidationResult);

            AppendOutput($"\n✓ Component: {result.ComponentName}\n");
            AppendOutput($"✓ Generated: {result.GeneratedFB.FBName}\n");
            AppendOutput($"✓ GUID: {result.GeneratedFB.GUID}\n");
            AppendOutput($"✓ Output: {result.OutputPath}\n");
            AppendOutput($"✓ Deployed: {result.DeployPath}\n");
            AppendOutput("\n✅ SUCCESS - Translation Complete!\n", false, true);
        }

        private void AppendValidationResults(CodeGen.Validation.ValidationResult result)
        {
            AppendOutput("\n=== VALIDATION RESULTS ===\n");

            foreach (var info in result.InfoMessages)
                AppendOutput($"{info}\n");

            foreach (var warning in result.Warnings)
                AppendOutput($"⚠ {warning}\n", true);

            foreach (var error in result.Errors)
                AppendOutput($"❌ {error}\n", true);

            AppendOutput(result.IsValid
                ? "\n✅ VALIDATION PASSED\n"
                : "\n❌ VALIDATION FAILED\n",
                !result.IsValid);
        }

        private void AppendOutput(string text, bool isError = false, bool isSuccess = false)
        {
            if (txtOutput.InvokeRequired)
            {
                txtOutput.Invoke(new Action(() => AppendOutput(text, isError, isSuccess)));
                return;
            }

            int start = txtOutput.TextLength;
            txtOutput.AppendText(text);
            int end = txtOutput.TextLength;

            txtOutput.Select(start, end - start);
            txtOutput.SelectionColor = isError ? System.Drawing.Color.Red
                                      : isSuccess ? System.Drawing.Color.Green
                                      : System.Drawing.Color.Black;
            txtOutput.SelectionLength = 0;
            txtOutput.ScrollToCaret();
        }
    }
}