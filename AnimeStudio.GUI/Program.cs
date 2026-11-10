using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AnimeStudio.GUI
{
    static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            KeepSettings();
            Application.Run(new MainForm());
        }

        // Settings are stored per version. On the first start of a new version, take over
        // those of the version used before, as long as the program sits in the same folder.
        static void KeepSettings()
        {
            try
            {
                var path = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
                if (!File.Exists(path))
                {
                    Properties.Settings.Default.Upgrade();
                    Properties.Settings.Default.Save();
                }
            }
            catch (Exception)
            {
                // Without them the program starts with its defaults, as it did before.
            }
        }
    }
}