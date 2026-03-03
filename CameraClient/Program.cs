using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace CameraClient
{
    class Program
    {
        private static IConfiguration _configuration = null!;
        private static int _cameraId;
        private static int _port;
        private static bool _isMaster;
        private static string _dbHost = "127.0.0.1";
        private static int _dbPort = 5432;
        private static string _dbDatabase = "L2";
        private static string _dbUsername = "postgres";
        private static string _dbPassword = "postgres";
        private static int _masterAppPort = 9000;
        private static int _masterCameraPort = 9001;
        private static ClientWebSocket? _masterAppClient;
        private static TcpListener? _slaveListener;
        private static List<TcpClient> _connectedSlaves = new();
        
        // Общий Random с уникальным seed для каждой камеры
        private static Random _random = new Random(Environment.TickCount ^ Guid.NewGuid().GetHashCode());

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
                File.AppendAllText(AgentDebugLogPath, JsonSerializer.Serialize(payload) + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
        #endregion

        static async Task Main(string[] args)
        {
            _configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();
            _cameraId = int.Parse(GetArgument(args, "--id") ?? "1");
            _port = int.Parse(GetArgument(args, "--port") ?? "9001");
            _isMaster = GetArgument(args, "--ismaster")?.ToLower() == "true";
            _dbHost = GetArgument(args, "--dbhost") ?? "127.0.0.1";
            _dbPort = int.Parse(GetArgument(args, "--dbport") ?? "5432");
            _dbDatabase = GetArgument(args, "--dbdatabase") ?? "L2";
            _dbUsername = GetArgument(args, "--dbusername") ?? "postgres";
            _dbPassword = GetArgument(args, "--dbpassword") ?? "postgres";
            _masterAppPort = int.Parse(GetArgument(args, "--masterport") ?? "8080");
            _masterCameraPort = int.Parse(GetArgument(args, "--mastercamera") ?? _port.ToString());

            AgentLog(
                hypothesisId: "H1",
                location: "CameraClient/Program.cs:Main",
                message: "Startup args parsed",
                data: new
                {
                    cameraId = _cameraId,
                    port = _port,
                    isMaster = _isMaster,
                    masterAppPort = _masterAppPort,
                    masterCameraPort = _masterCameraPort,
                    cwd = Directory.GetCurrentDirectory(),
                    args = args
                });

            Console.WriteLine($"Камера {_cameraId} запускается (Master={_isMaster}, Порт={_port})");
            Console.WriteLine($"БД: {_dbHost}:{_dbPort}/{_dbDatabase}");

            if (_isMaster)
            {
                await StartMasterCameraAsync();
            }
            else
            {
                await StartSlaveCameraAsync();
            }
        }

        static string? GetArgument(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == name && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        static async Task StartMasterCameraAsync()
        {
            Console.WriteLine($"Мастер-камера {_cameraId} запускает сервер на порту {_port}...");
            _slaveListener = new TcpListener(IPAddress.Any, _port);
            _slaveListener.Start();
            AgentLog(
                hypothesisId: "H2",
                location: "CameraClient/Program.cs:StartMasterCameraAsync",
                message: "Master listener started",
                data: new
                {
                    cameraId = _cameraId,
                    port = _port,
                    localEndpoint = _slaveListener.LocalEndpoint?.ToString()
                });
            Console.WriteLine($"Сервер запущен. Ожидание подключений ведомых камер...");
            await BroadcastMasterReadyAsync();
            
            // Подключаемся к MasterApp сначала
            await ConnectToMasterAppAsync();
            
            // Запускаем прием подключений от ведомых и ждем 3 секунды для подключения всех
            _ = AcceptSlavesAsync();
            await Task.Delay(3000);
            
            Console.WriteLine($"Отправка сигнала СТАРТ {_connectedSlaves.Count} ведомым камерам...");
            foreach (var client in _connectedSlaves)
            {
                await SendStartSignalAsync(client);
            }
            
            // Начинаем отправку данных мастер-камерой
            await StartCameraWorkAsync(true);
        }

        static async Task AcceptSlavesAsync()
        {
            var timeout = DateTime.Now.AddSeconds(5);
            while (_connectedSlaves.Count < 3 && DateTime.Now < timeout)
            {
                try
                {
                    var client = await _slaveListener!.AcceptTcpClientAsync();
                    _connectedSlaves.Add(client);
                    Console.WriteLine($"Подключена ведомая камера от {client.Client.RemoteEndPoint} (всего: {_connectedSlaves.Count})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка принятия подключения: {ex.Message}");
                    break;
                }
            }
            Console.WriteLine($"Завершено ожидание ведомых камер. Подключено: {_connectedSlaves.Count}");
        }

        static async Task StartSlaveCameraAsync()
        {
            var masterPort = _masterCameraPort;
            Console.WriteLine($"Ведомая камера {_cameraId} подключается к мастеру на порту {masterPort}...");

            var client = new TcpClient();
            try
            {
                AgentLog(
                    hypothesisId: "H3",
                    location: "CameraClient/Program.cs:StartSlaveCameraAsync",
                    message: "Slave connecting to master",
                    data: new
                    {
                        cameraId = _cameraId,
                        masterHost = "127.0.0.1",
                        masterPort,
                        configuredPortArg = _port
                    });
                await client.ConnectAsync("127.0.0.1", masterPort);
                AgentLog(
                    hypothesisId: "H3",
                    location: "CameraClient/Program.cs:StartSlaveCameraAsync",
                    message: "Slave connected to master",
                    data: new
                    {
                        local = client.Client.LocalEndPoint?.ToString(),
                        remote = client.Client.RemoteEndPoint?.ToString()
                    });
                Console.WriteLine($"Подключено к мастер-камере");
                Console.WriteLine("Ожидание сигнала СТАРТ...");
                await WaitForStartSignalAsync(client);
                await ConnectToMasterAppAsync();
                
                await StartCameraWorkAsync(false);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException;
                object? socketDetails = null;
                if (ex is SocketException se)
                {
                    socketDetails = new { se.SocketErrorCode, se.ErrorCode, se.NativeErrorCode };
                }
                AgentLog(
                    hypothesisId: "H4",
                    location: "CameraClient/Program.cs:StartSlaveCameraAsync",
                    message: "Slave failed to connect to master",
                    data: new
                    {
                        exType = ex.GetType().FullName,
                        exMessage = ex.Message,
                        exHResult = ex.HResult,
                        innerType = inner?.GetType().FullName,
                        innerMessage = inner?.Message,
                        socket = socketDetails
                    });
                Console.WriteLine($"Ошибка подключения к мастеру: {ex.Message}");
            }
        }

        static async Task BroadcastMasterReadyAsync()
        {
            using var udpClient = new UdpClient();
            udpClient.EnableBroadcast = true;
            var message = Encoding.UTF8.GetBytes($"MASTER_READY:{_cameraId}");
            var endpoint = new IPEndPoint(IPAddress.Broadcast, 15000);
            await udpClient.SendAsync(message, message.Length, endpoint);
            Console.WriteLine("Отправлено широковещательное сообщение о готовности");
        }

        static async Task SendStartSignalAsync(TcpClient client)
        {
            var message = Encoding.UTF8.GetBytes("START");
            var stream = client.GetStream();
            await stream.WriteAsync(message);
        }

        static async Task WaitForStartSignalAsync(TcpClient client)
        {
            var buffer = new byte[1024];
            var stream = client.GetStream();
            var bytesRead = await stream.ReadAsync(buffer);
            var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            if (message == "START")
            {
                Console.WriteLine("Получен сигнал СТАРТ от мастера");
            }
        }

        static async Task ConnectToMasterAppAsync()
        {
            try
            {
                Console.WriteLine($"[Camera {_cameraId}] Подключение к MasterApp на ws://localhost:{_masterAppPort}/camera/{_cameraId}...");
                AgentLog(
                    hypothesisId: "H5",
                    location: "CameraClient/Program.cs:ConnectToMasterAppAsync",
                    message: "Connecting to MasterApp",
                    data: new
                    {
                        cameraId = _cameraId,
                        masterAppPort = _masterAppPort,
                        url = $"ws://localhost:{_masterAppPort}/camera/{_cameraId}"
                    });
                _masterAppClient = new ClientWebSocket();
                await _masterAppClient.ConnectAsync(
                    new Uri($"ws://localhost:{_masterAppPort}/camera/{_cameraId}"),
                    CancellationToken.None);
                Console.WriteLine($"[Camera {_cameraId}] Подключено к MasterApp!");
                AgentLog(
                    hypothesisId: "H5",
                    location: "CameraClient/Program.cs:ConnectToMasterAppAsync",
                    message: "Connected to MasterApp",
                    data: new
                    {
                        cameraId = _cameraId,
                        state = _masterAppClient.State
                    });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Camera {_cameraId}] Ошибка подключения к MasterApp: {ex.Message}");
                AgentLog(
                    hypothesisId: "H5_ERR",
                    location: "CameraClient/Program.cs:ConnectToMasterAppAsync",
                    message: "Failed to connect to MasterApp",
                    data: new
                    {
                        cameraId = _cameraId,
                        exType = ex.GetType().FullName,
                        ex.Message
                    });
            }
        }

        static async Task StartCameraWorkAsync(bool isMaster)
        {
            Console.WriteLine($"Камера {_cameraId} начинает работу...");

            var connectionString = $"Host={_dbHost};Port={_dbPort};Database={_dbDatabase};Username={_dbUsername};Password={_dbPassword}";
            Console.WriteLine($"Подключение к БД: {connectionString}");
            var gtinErrorRate = _configuration.GetValue<double>("Validation:GtinErrorRate", 0.01);
            var snErrorRate = _configuration.GetValue<double>("Validation:SerialNumberErrorRate", 0.0005);
            var cryptoErrorRate = _configuration.GetValue<double>("Validation:CryptoFormatErrorRate", 0.00001);
            int messageCount = 0;

            // Все камеры отправляют данные в MasterApp асинхронно
            while (messageCount < 100)
            {
                var delay = _random.Next(10, 90);
                await Task.Delay(delay);

                messageCount++;
                var gtin = GenerateGtin(_random, gtinErrorRate);
                var serialNumber = GenerateSerialNumber(_random, snErrorRate);
                var cryptoCode = GenerateCryptoCode(_random, cryptoErrorRate);
                var isValid = !gtin.Contains("INVALID") && !serialNumber.Contains("INVALID") && !cryptoCode.Contains("INVALID");
                var validationResult = isValid ? "OK" : GetValidationError(gtin, serialNumber, cryptoCode);

                Console.WriteLine($"Камера {_cameraId}: Сообщение #{messageCount} (GTIN={gtin}, SN={serialNumber}, Valid={isValid})");

                // Все камеры (и мастер, и ведомые) отправляют данные в MasterApp
                await SendDataToMasterAppAsync(gtin, serialNumber, cryptoCode, isValid, validationResult);
            }

            Console.WriteLine($"Камера {_cameraId} завершила работу");
            Console.WriteLine("Нажмите любую клавишу для выхода...");
            Console.ReadKey();
        }

        static string GenerateGtin(Random random, double errorRate)
        {
            if (random.NextDouble() < errorRate)
                return "INVALID_GTIN_" + random.Next(1000, 9999);
            var gtin = new StringBuilder();
            for (int i = 0; i < 13; i++)
            {
                gtin.Append(random.Next(0, 10));
            }
            int sum = 0;
            for (int i = 0; i < 13; i++)
            {
                sum += (gtin[i] - '0') * (i % 2 == 0 ? 1 : 3);
            }
            int checkDigit = (10 - (sum % 10)) % 10;
            gtin.Append(checkDigit);
            return gtin.ToString();
        }

        static string GenerateSerialNumber(Random random, double errorRate)
        {
            if (random.NextDouble() < errorRate)
                return "INVALID_SN_" + random.Next(1000, 9999);

            return "SN" + random.Next(100000, 999999);
        }

        static string GenerateCryptoCode(Random random, double errorRate)
        {
            if (random.NextDouble() < errorRate)
                return "INVALID_CRYPTO";

            var bytes = new byte[32];
            random.NextBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        static string GetValidationError(string gtin, string sn, string crypto)
        {
            if (gtin.Contains("INVALID")) return "Неверный GTIN";
            if (sn.Contains("INVALID")) return "Неверный SN";
            if (crypto.Contains("INVALID")) return "Неверный криптокод";
            return "OK";
        }

        static async Task SendDataToMasterAppAsync(string gtin, string sn, string crypto, bool isValid, string validationResult)
        {
            if (_masterAppClient == null || _masterAppClient.State != WebSocketState.Open)
            {
                Console.WriteLine($"[Camera {_cameraId}] SendDataToMasterApp: WebSocket не подключён (state={_masterAppClient?.State.ToString() ?? "null"})");
                return;
            }

            try
            {
                var data = new
                {
                    CameraId = _cameraId,
                    Gtin = gtin,
                    SerialNumber = sn,
                    CryptoCode = crypto,
                    IsValid = isValid,
                    ValidationResult = validationResult,
                    Timestamp = DateTime.Now
                };

                var json = JsonSerializer.Serialize(data);
                Console.WriteLine($"[Camera {_cameraId}] Отправка в MasterApp: {json}");
                var bytes = Encoding.UTF8.GetBytes(json);
                await _masterAppClient.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
                Console.WriteLine($"[Camera {_cameraId}] Сообщение отправлено");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Camera {_cameraId}] Ошибка отправки в MasterApp: {ex.Message}");
            }
        }
    }
}
