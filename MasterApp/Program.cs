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

        #region agent log
        private const string AgentDebugSessionId = "49c081";
        private const string AgentDebugLogPath = @"C:\Users\gada\Desktop\TestProject\debug-49c081.log";
        private static void AgentLog(string hypothesisId, string location, string message, object? data = null, string runId = "pre")
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var payload = new
                {
                    sessionId = AgentDebugSessionId,
                    runId,
                    hypothesisId,
                    location,
                    message,
                    data,
                    timestamp = now,
                    id = $"log_{now}_{Guid.NewGuid():N}"
                };
                File.AppendAllText(AgentDebugLogPath, System.Text.Json.JsonSerializer.Serialize(payload) + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch { }
        }
        #endregion

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                AgentLog("M1", "MasterApp/Program.cs:Main", "Main started", new { cwd = Directory.GetCurrentDirectory(), args }, "pre");

                _configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                    .Build();

                AgentLog("M1", "MasterApp/Program.cs:Main", "Configuration loaded", null, "pre");

                Console.WriteLine("Запуск Мастер-приложения...");

                // Получение настроек
                var webSocketPort = _configuration.GetValue<int>("MasterApp:WebSocketPort", 8080);
                var resultPort = _configuration.GetValue<int>("MasterApp:ResultSocketPort", 9000);
                var frameWaitTimeMs = _configuration.GetValue<int>("MasterApp:FrameWaitTimeMs", 100);
                var responseTimeoutMs = _configuration.GetValue<int>("MasterApp:ResponseTimeoutMs", 30);

                var gtinErrorRate = _configuration.GetValue<double>("Validation:GtinErrorRate", 0.01);
                var snErrorRate = _configuration.GetValue<double>("Validation:SerialNumberErrorRate", 0.0005);
                var cryptoErrorRate = _configuration.GetValue<double>("Validation:CryptoFormatErrorRate", 0.00001);

                AgentLog("M2", "MasterApp/Program.cs:Main", "Settings loaded", new { webSocketPort, resultPort, frameWaitTimeMs, responseTimeoutMs }, "pre");

                // Случайный выбор главной камеры
                var masterCameraId = SelectRandomMasterCamera();
                Console.WriteLine($"Главная камера выбрана случайно: Камера #{masterCameraId}");
                AgentLog("M4", "MasterApp/Program.cs:Main", "Master camera selected", new { masterCameraId }, "pre");

                // Инициализация сервисов
                _frameProcessor = new FrameProcessor(frameWaitTimeMs, responseTimeoutMs);
                _validationService = new ValidationService(gtinErrorRate, snErrorRate, cryptoErrorRate);
                _resultServer = new ResultSocketServer(resultPort);

                // Запуск сервера результатов
                _resultServer.Start();
                AgentLog("M3", "MasterApp/Program.cs:Main", "Result server started", new { resultPort }, "pre");

                // Запуск WebSocket сервера для WebView
                _webSocketServer = new WebSocketServer(webSocketPort, masterCameraId, _frameProcessor, _validationService, _resultServer);
                _webSocketServer.Start();
                Console.WriteLine($"WebSocket сервер запущен на порту {webSocketPort}");
                AgentLog("M3", "MasterApp/Program.cs:Main", "WebSocket server started", new { webSocketPort }, "pre");

                // Запуск камер
                var cameraProcesses = StartCamerasAsync(masterCameraId, webSocketPort);
                Console.WriteLine($"Запущено {cameraProcesses.Count} камер(ы)");
                AgentLog("M4", "MasterApp/Program.cs:Main", "Cameras started", new { count = cameraProcesses.Count }, "pre");

                // Запуск WPF окна напрямую
                _mainWindow = new MainWindow();
                AgentLog("M5", "MasterApp/Program.cs:Main", "MainWindow created", null, "pre");
                _mainWindow.ShowDialog();
                AgentLog("M5", "MasterApp/Program.cs:Main", "MainWindow closed", null, "pre");

                // Остановка камер при выходе
                foreach (var process in cameraProcesses)
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.Dispose();
                    }
                }
                AgentLog("M6", "MasterApp/Program.cs:Main", "Cameras stopped", null, "pre");

                _resultServer?.Stop();
                _webSocketServer?.Stop();
                _frameProcessor?.Stop();
                AgentLog("M6", "MasterApp/Program.cs:Main", "Servers stopped", null, "pre");
            }
            catch (Exception ex)
            {
                AgentLog("M_ERR", "MasterApp/Program.cs:Main", "Unhandled exception", new { exType = ex.GetType().FullName, ex.Message, ex.StackTrace }, "pre");
                Console.WriteLine($"Фатальная ошибка MasterApp: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Случайный выбор главной камеры среди 4
        /// </summary>
        static int SelectRandomMasterCamera()
        {
            var random = new Random();
            return random.Next(1, 5); // 1-4
        }

        static List<Process> StartCamerasAsync(int masterCameraId, int webSocketPort)
        {
            var cameraProcesses = new List<Process>();
            var camerasConfig = _configuration.GetSection("Cameras").GetChildren().ToList();

            // Порт мастер-камеры берём из её конфигурации (SocketPort),
            // все ведомые будут подключаться именно к нему.
            var masterCameraConfig = camerasConfig.First(c =>
                int.Parse(c["Id"] ?? "0") == masterCameraId);
            var masterCameraPort = int.Parse(masterCameraConfig["SocketPort"] ?? "9001");

            foreach (var cameraConfig in camerasConfig)
            {
                var id = cameraConfig["Id"];
                var isMaster = int.Parse(id) == masterCameraId;
                
                var process = StartCameraProcessAsync(cameraConfig, isMaster, masterCameraPort, webSocketPort);
                if (process != null)
                {
                    cameraProcesses.Add(process);
                    Console.WriteLine($"Запущена камера {id} (Master={isMaster})");
                }

                Thread.Sleep(100);
            }

            return cameraProcesses;
        }

        static Process? StartCameraProcessAsync(IConfigurationSection cameraConfig, bool isMaster, int masterCameraPort, int webSocketPort)
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

                // Базовая директория MasterApp (...\MasterApp\bin\Debug\net8.0-windows\).
                var baseDir = AppContext.BaseDirectory;
                // Переходим к корню решения: ...\TestProject\
                // Нужно подняться на 4 уровня: net8.0-windows -> Debug -> bin -> MasterApp -> TestProject
                var solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
                // Путь к собранному CameraClient: ...\TestProject\CameraClient\bin\Debug\net8.0\
                var cameraBinDir = Path.Combine(solutionRoot, "CameraClient", "bin", "Debug", "net8.0");
                var cameraExePath = Path.Combine(cameraBinDir, "CameraClient.exe");

                // Если CameraClient.exe не найден в отдельной папке, ищем в общей папке bin решения
                if (!File.Exists(cameraExePath))
                {
                    // Альтернативный путь: ...\TestProject\bin\Debug\CameraClient\
                    var altCameraBinDir = Path.Combine(solutionRoot, "bin", "Debug", "CameraClient");
                    var altCameraExePath = Path.Combine(altCameraBinDir, "CameraClient.exe");
                    if (File.Exists(altCameraExePath))
                    {
                        cameraBinDir = altCameraBinDir;
                        cameraExePath = altCameraExePath;
                    }
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = cameraExePath,
                        Arguments = $"--id {id} --port {port} --ismaster {isMaster.ToString().ToLower()} " +
                                    $"--dbhost {dbHost} --dbport {dbPort} --dbdatabase {dbDatabase} " +
                                    $"--dbusername {dbUsername} --dbpassword {dbPassword} " +
                                    $"--mastercamera {masterCameraPort} --masterport {webSocketPort}",
                        UseShellExecute = false,
                        WorkingDirectory = cameraBinDir
                    }
                };

                process.Start();
                AgentLog("M_CAM", "MasterApp/Program.cs:StartCameraProcessAsync", "Camera process started",
                    new { id, isMaster, port, masterCameraPort, webSocketPort, exe = cameraExePath }, "pre");

                return process;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка запуска камеры {cameraConfig["Id"]}: {ex.Message}");
                AgentLog("M_CAM_ERR", "MasterApp/Program.cs:StartCameraProcessAsync", "Camera process failed",
                    new { id = cameraConfig["Id"], isMaster, exType = ex.GetType().FullName, ex.Message }, "pre");
                return null;
            }
        }
    }
}
