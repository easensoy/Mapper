using System;
using System.Windows.Forms;

namespace MapperUI
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(args));
        }
    }
}
