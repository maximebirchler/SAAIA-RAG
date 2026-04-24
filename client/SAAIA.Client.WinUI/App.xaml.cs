using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SAAIA.Client.WinUI.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SAAIA.Client.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            // Startup diagnostics (support-friendly). If the UI crashes before showing,
            // the log will contain the exception details.
            ClientLog.Info("=== App starting ===");
            try
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
                ClientLog.Info($"Version: {ver}");
                ClientLog.Info($"OS: {Environment.OSVersion}");
                ClientLog.Info($"Process: {Environment.ProcessPath}");
            }
            catch { /* ignore */ }

            ClientLog.RegisterGlobalHandlers();
            UnhandledException += App_UnhandledException;
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                ClientLog.Info("OnLaunched");
                if (StartupMaintenanceMode.TryRunAsync(Environment.GetCommandLineArgs()).GetAwaiter().GetResult())
                {
                    ClientLog.Info("Maintenance mode completed; exiting without UI.");
                    Exit();
                    return;
                }

                _window = new MainWindow();
                _window.Activate();
                ClientLog.Info("MainWindow activated");
            }
            catch (Exception ex)
            {
                // If anything fails before the window is visible, we want a clean log.
                ClientLog.Exception("OnLaunched", ex);
                throw;
            }
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                ClientLog.Exception("UI.UnhandledException", e.Exception);
            }
            catch { /* ignore */ }
        }

        // AppDomain/TaskScheduler handlers are registered in ClientLog.RegisterGlobalHandlers().
    }
}
