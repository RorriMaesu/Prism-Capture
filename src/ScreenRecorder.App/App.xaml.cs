using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using ScreenRecorder.App.Helpers;
using WinRT.Interop;
using System.Diagnostics;

namespace ScreenRecorder.App
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();

            Breadcrumbs.Session("App: ctor");
            Breadcrumbs.Write($"BaseDirectory: {AppContext.BaseDirectory}");

            // Packaged (MSIX) desktop apps commonly start with CurrentDirectory = System32.
            // Use a stable, writable directory to reduce surprises when launching child processes.
            try
            {
                var wd = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PrismCapture");
                System.IO.Directory.CreateDirectory(wd);
                Environment.CurrentDirectory = wd;
            }
            catch
            {
            }

            try { Breadcrumbs.Write($"CurrentDirectory: {Environment.CurrentDirectory}"); } catch { }

            try
            {
                UnhandledException += (_, e) =>
                {
                    Breadcrumbs.Session("App: UnhandledException");
                    Breadcrumbs.Write(e.Exception);
                };
            }
            catch { }

            try
            {
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    Breadcrumbs.Session("AppDomain: UnhandledException");
                    if (e.ExceptionObject is Exception ex)
                    {
                        Breadcrumbs.Write(ex);
                    }
                    else
                    {
                        Breadcrumbs.Write(e.ExceptionObject?.ToString() ?? "<null>");
                    }
                };
            }
            catch { }

            try
            {
                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    Breadcrumbs.Session("TaskScheduler: UnobservedTaskException");
                    Breadcrumbs.Write(e.Exception);
                    try { e.SetObserved(); } catch { }
                };
            }
            catch { }

            try
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try
                    {
                        Breadcrumbs.Session("AppDomain: ProcessExit");
                        Breadcrumbs.Write($"ExitCode={Environment.ExitCode}");
                    }
                    catch { }
                };
            }
            catch { }
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Breadcrumbs.Session("App: OnLaunched");
            Breadcrumbs.Write($"Args: {e.Arguments}");

            MainWindow ??= new Window();

            // Ensure the title bar text uses our app name (otherwise WinUI may show a default).
            try
            {
                MainWindow.Title = "Prism Capture";
            }
            catch { }

            try
            {
                MainWindow.Closed -= OnMainWindowClosed;
                MainWindow.Closed += OnMainWindowClosed;
            }
            catch { }

            try
            {
                // Premium Windows 11 look (best-effort). If unsupported, it fails silently.
                MainWindow.SystemBackdrop = new MicaBackdrop();
            }
            catch { }

            _ = Helpers.WindowInterop.TryExcludeWindowFromCapture(MainWindow);

            if (MainWindow.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                MainWindow.Content = rootFrame;
            }

            _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
            MainWindow.Activate();

            // Ensure the app opens maximized so the full UI is visible.
            try
            {
                var hwnd = WindowInterop.GetWindowHandle(MainWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);

                try
                {
                    appWindow.Title = "Prism Capture";
                }
                catch { }

                if (appWindow.Presenter is OverlappedPresenter overlapped)
                {
                    overlapped.Maximize();
                }
            }
            catch { }
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            try
            {
                Breadcrumbs.Session("App: MainWindow.Closed");
            }
            catch { }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
