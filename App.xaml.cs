using SPPS2001D_Pro.Services;
using System.IO;
using System.Windows;

namespace SPPS2001D_Pro
{
    public partial class App : Application
    {
        private PowerSupplyController? _controller;
        private LocalApiService? _apiService;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _controller = new PowerSupplyController();

            string recipePath = Environment.GetEnvironmentVariable("SPPS_RECIPE_FILE")
                ?? Path.Combine(AppContext.BaseDirectory, "PowerSupplyRecipes.xlsx");

            string apiUrl = Environment.GetEnvironmentVariable("SPPS_API_URL")
                ?? "http://127.0.0.1:5187";

            string? apiKey = Environment.GetEnvironmentVariable("SPPS_API_KEY");

            var recipeProvider = new ExcelRecipeProvider(recipePath);
            _apiService = new LocalApiService(_controller, recipeProvider, apiUrl, apiKey);

            try
            {
                await _apiService.StartAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"The local Plex API could not start.\n\n{ex.Message}",
                    "Plex API startup warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var mainWindow = new MainWindow(_controller);
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_apiService is not null)
                await _apiService.DisposeAsync();

            if (_controller is not null)
            {
                try { await _controller.DisconnectAsync(); } catch { }
                _controller.Dispose();
            }

            base.OnExit(e);
        }
    }
}
