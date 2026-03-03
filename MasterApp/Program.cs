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
        private static WebSocketServer? _webSocketServer;
        private static MainWindow? _mainWindow;

        [STAThread]
        static void Main(string[] args)
        {
            _configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Console.WriteLine("Запуск Мастер-приложения...");
            var webSocketPort = _configuration.GetValue<int>("MasterApp:WebSocketPort", 8080);
            _webSocketServer = new WebSocketServer(webSocketPort);
            _webSocketServer.Start();
            Console.WriteLine($"WebSocket сервер запущен на порту {webSocketPort}");
            var cameraProcesses = StartCamerasAsync();
            Console.WriteLine($"Запущено {cameraProcesses.Count} камер(ы)");
            _mainWindow = new MainWindow();
            _mainWindow.ShowDialog();
            foreach (var process in cameraProcesses)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.Dispose();
                }
            }

            _webSocketServer.Stop();
        }

        static List<Process> StartCamerasAsync()
        {
            var cameraProcesses = new List<Process>();
            var camerasConfig = _configuration.GetSection("Cameras").GetChildren().ToList();
            var masterConfig = camerasConfig.FirstOrDefault(c => c["IsMaster"] == "True");
            if (masterConfig != null)
            {
                var process = StartCameraProcessAsync(masterConfig);
                if (process != null)
                    cameraProcesses.Add(process);
                Thread.Sleep(500);
            }
            var slaveConfigs = camerasConfig.Where(c => c["IsMaster"] != "True").ToList();
            foreach (var cameraConfig in slaveConfigs)
            {
                var process = StartCameraProcessAsync(cameraConfig);
                if (process != null)
                    cameraProcesses.Add(process);
                
                Thread.Sleep(100);
            }

            return cameraProcesses;
        }

        static Process? StartCameraProcessAsync(IConfigurationSection cameraConfig)
        {
            try
            {
                var id = cameraConfig["Id"];
                var port = cameraConfig["SocketPort"];
                var isMaster = cameraConfig["IsMaster"] == "True";
                var dbHost = cameraConfig["Database:Host"];
                var dbPort = cameraConfig["Database:Port"];
                var dbDatabase = cameraConfig["Database:Database"];
                var dbUsername = cameraConfig["Database:Username"];
                var dbPassword = cameraConfig["Database:Password"];

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "CameraClient.exe",
                        Arguments = $"--id {id} --port {port} --ismaster {isMaster.ToString().ToLower()} " +
                                    $"--dbhost {dbHost} --dbport {dbPort} --dbdatabase {dbDatabase} " +
                                    $"--dbusername {dbUsername} --dbpassword {dbPassword}",
                        UseShellExecute = false,
                        WorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "CameraClient")
                    }
                };

                process.Start();
                Console.WriteLine($"Запущена камера {id} (Master={isMaster}) на порту {port}");
                
                return process;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка запуска камеры {cameraConfig["Id"]}: {ex.Message}");
                return null;
            }
        }
    }
}
