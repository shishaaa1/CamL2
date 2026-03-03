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
        private static FrameProcessor? _frameProcessor;
        private static ValidationService? _validationService;
        private static ResultSocketServer? _resultServer;
        private static MainWindow? _mainWindow;

        [STAThread]
        static void Main(string[] args)
        {
            _configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Console.WriteLine("Запуск Мастер-приложения...");

            // Получение настроек
            var webSocketPort = _configuration.GetValue<int>("MasterApp:WebSocketPort", 8080);
            var resultPort = _configuration.GetValue<int>("MasterApp:ResultSocketPort", 9000);
            var frameWaitTimeMs = _configuration.GetValue<int>("MasterApp:FrameWaitTimeMs", 100);
            var responseTimeoutMs = _configuration.GetValue<int>("MasterApp:ResponseTimeoutMs", 30);
            
            var gtinErrorRate = _configuration.GetValue<double>("Validation:GtinErrorRate", 0.01);
            var snErrorRate = _configuration.GetValue<double>("Validation:SerialNumberErrorRate", 0.0005);
            var cryptoErrorRate = _configuration.GetValue<double>("Validation:CryptoFormatErrorRate", 0.00001);

            // Инициализация сервисов
            _frameProcessor = new FrameProcessor(frameWaitTimeMs, responseTimeoutMs);
            _validationService = new ValidationService(gtinErrorRate, snErrorRate, cryptoErrorRate);
            _resultServer = new ResultSocketServer(resultPort);

            // Запуск сервера результатов
            _resultServer.Start();

            // Запуск WebSocket сервера для WebView
            _webSocketServer = new WebSocketServer(webSocketPort, _frameProcessor, _validationService, _resultServer);
            _webSocketServer.Start();
            Console.WriteLine($"WebSocket сервер запущен на порту {webSocketPort}");

            // Случайный выбор главной камеры
            var masterCameraId = SelectRandomMasterCamera();
            Console.WriteLine($"Главная камера выбрана случайно: Камера #{masterCameraId}");

            // Запуск камер
            var cameraProcesses = StartCamerasAsync(masterCameraId);
            Console.WriteLine($"Запущено {cameraProcesses.Count} камер(ы)");

            // Запуск WPF окна напрямую
            _mainWindow = new MainWindow();
            _mainWindow.ShowDialog();

            // Остановка камер при выходе
            foreach (var process in cameraProcesses)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.Dispose();
                }
            }

            _resultServer?.Stop();
            _webSocketServer?.Stop();
            _frameProcessor?.Stop();
        }

        /// <summary>
        /// Случайный выбор главной камеры среди 4
        /// </summary>
        static int SelectRandomMasterCamera()
        {
            var random = new Random();
            return random.Next(1, 5); // 1-4
        }

        static List<Process> StartCamerasAsync(int masterCameraId)
        {
            var cameraProcesses = new List<Process>();
            var camerasConfig = _configuration.GetSection("Cameras").GetChildren().ToList();

            foreach (var cameraConfig in camerasConfig)
            {
                var id = cameraConfig["Id"];
                var isMaster = int.Parse(id) == masterCameraId;
                
                var process = StartCameraProcessAsync(cameraConfig, isMaster);
                if (process != null)
                {
                    cameraProcesses.Add(process);
                    Console.WriteLine($"Запущена камера {id} (Master={isMaster})");
                }

                Thread.Sleep(100);
            }

            return cameraProcesses;
        }

        static Process? StartCameraProcessAsync(IConfigurationSection cameraConfig, bool isMaster)
        {
            try
            {
                var id = cameraConfig["Id"];
                var port = cameraConfig["SocketPort"];
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
