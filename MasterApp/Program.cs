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
        private static readonly string AgentDebugLogPath = InitLogPath();

        private static string InitLogPath()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var logDir = Path.Combine(baseDir, "logs");
                Directory.CreateDirectory(logDir);
                return Path.Combine(logDir, "masterapp-49c081.log");
            }
            catch
            {
                return "masterapp-49c081.log";
            }
        }

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
            catch
            {
                // если даже лог не пишется — не роняем приложение
            }
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
                var webSocketPort = _configuration.GetValue<int>("MasterApp:WebSocketPort", 8080);
                var resultPort = _configuration.GetValue<int>("MasterApp:ResultSocketPort", 9000);
                var frameWaitTimeMs = _configuration.GetValue<int>("MasterApp:FrameWaitTimeMs", 100);
                var responseTimeoutMs = _configuration.GetValue<int>("MasterApp:ResponseTimeoutMs", 30);
                var gtinErrorRate = _configuration.GetValue<double>("Validation:GtinErrorRate", 0.01);
                var snErrorRate = _configuration.GetValue<double>("Validation:SerialNumberErrorRate", 0.0005);
                var cryptoErrorRate = _configuration.GetValue<double>("Validation:CryptoFormatErrorRate", 0.00001);
                AgentLog("M2", "MasterApp/Program.cs:Main", "Settings loaded", new { webSocketPort, resultPort, frameWaitTimeMs, responseTimeoutMs }, "pre");
                var masterCameraId = SelectRandomMasterCamera();
                Console.WriteLine($"Главная камера выбрана случайно: Камера #{masterCameraId}");
                AgentLog("M4", "MasterApp/Program.cs:Main", "Master camera selected", new { masterCameraId }, "pre");
                _frameProcessor = new FrameProcessor(frameWaitTimeMs, responseTimeoutMs);
                _validationService = new ValidationService(gtinErrorRate, snErrorRate, cryptoErrorRate);
                _resultServer = new ResultSocketServer(resultPort);
                _resultServer.Start();
                AgentLog("M3", "MasterApp/Program.cs:Main", "Result server started", new { resultPort }, "pre");
                _webSocketServer = new WebSocketServer(webSocketPort, masterCameraId, _frameProcessor, _validationService, _resultServer);
                _webSocketServer.Start();
                Console.WriteLine($"WebSocket сервер запущен на порту {webSocketPort}");
                AgentLog("M3", "MasterApp/Program.cs:Main", "WebSocket server started", new { webSocketPort }, "pre");
                var cameraProcesses = StartCamerasAsync(masterCameraId, webSocketPort);
                Console.WriteLine($"Запущено {cameraProcesses.Count} камер(ы)");
                AgentLog("M4", "MasterApp/Program.cs:Main", "Cameras started", new { count = cameraProcesses.Count }, "pre");
                _mainWindow = new MainWindow();
                AgentLog("M5", "MasterApp/Program.cs:Main", "MainWindow created", null, "pre");
                _mainWindow.ShowDialog();
                AgentLog("M5", "MasterApp/Program.cs:Main", "MainWindow closed", null, "pre");
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
            return random.Next(1, 5);
        }

        static List<Process> StartCamerasAsync(int masterCameraId, int webSocketPort)
        {
            var cameraProcesses = new List<Process>();
            var camerasConfig = _configuration.GetSection("Cameras").GetChildren().ToList();
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
                var baseDir = AppContext.BaseDirectory;
                var solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));

                // Ищем CameraClient в нескольких типичных местах
                var candidates = new List<(string binDir, string exePath)>
                {
                    (Path.Combine(solutionRoot, "CameraClient", "bin", "Debug", "net8.0"),
                     Path.Combine(solutionRoot, "CameraClient", "bin", "Debug", "net8.0", "CameraClient.exe")),
                    (Path.Combine(solutionRoot, "CameraClient", "bin", "Debug", "net8.0-windows"),
                     Path.Combine(solutionRoot, "CameraClient", "bin", "Debug", "net8.0-windows", "CameraClient.exe")),
                    (Path.Combine(solutionRoot, "bin", "Debug", "CameraClient"),
                     Path.Combine(solutionRoot, "bin", "Debug", "CameraClient", "CameraClient.exe"))
                };

                string? cameraBinDir = null;
                string? cameraExePath = null;

                foreach (var (binDir, exePathCandidate) in candidates)
                {
                    if (File.Exists(exePathCandidate))
                    {
                        cameraBinDir = binDir;
                        cameraExePath = exePathCandidate;
                        break;
                    }
                }

                if (cameraBinDir == null || cameraExePath == null || !Directory.Exists(cameraBinDir))
                {
                    Console.WriteLine($"Ошибка запуска камеры {id}: не найден CameraClient.exe. Ожидаемые пути:");
                    foreach (var (binDir, exe) in candidates)
                    {
                        Console.WriteLine($"  {exe}");
                    }
                    AgentLog("M_CAM_ERR", "MasterApp/Program.cs:StartCameraProcessAsync",
                        "Camera executable not found",
                        new { id, isMaster, solutionRoot, candidates }, "pre");
                    return null;
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
