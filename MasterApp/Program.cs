using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using WebView;

namespace MasterApp
{
    class Program
    {
        private static IConfiguration _configuration = null!;

        [STAThread]
        static async Task Main(string[] args)
        {
            _configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Console.WriteLine("Запуск Мастер-приложения...");

            var cameraProcesses = await StartCamerasAsync();

            Console.WriteLine($"Запущено {cameraProcesses.Count} камер(ы)");

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        static async Task<List<Process>> StartCamerasAsync()
        {
            var cameraProcesses = new List<Process>();
            var camerasConfig = _configuration.GetSection("Cameras").GetChildren().ToList();

            foreach (var cameraConfig in camerasConfig)
            {
                var id = cameraConfig["Id"];
                var port = cameraConfig["SocketPort"];
                var isMaster = cameraConfig["IsMaster"] == "True";

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "CameraClient.exe",
                        Arguments = $"--id {id} --port {port} --ismaster {isMaster.ToString().ToLower()}",
                        UseShellExecute = false,
                        WorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "CameraClient")
                    }
                };

                process.Start();
                cameraProcesses.Add(process);

                Console.WriteLine($"Запущена камера {id} (Master={isMaster}) на порту {port}");
                await Task.Delay(100);
            }

            return cameraProcesses;
        }
    }
}
