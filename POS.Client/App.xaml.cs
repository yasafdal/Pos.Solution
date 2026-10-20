namespace POS.Client
{
    public partial class App : Application
    {
        public App()
        {
#if WINDOWS
            try
            {
                var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                var webView2Dir = System.IO.Path.Combine(localAppData, "PosSolution", "WebView2");
                System.IO.Directory.CreateDirectory(webView2Dir);
                System.Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webView2Dir);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set WebView2 user data folder: {ex.Message}");
            }
#endif
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new MainPage()) { Title = "POS.Client" };
        }
    }
}
