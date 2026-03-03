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
        private static ClientWebSocket? _masterAppClient;
        private static TcpListener? _slaveListener;
        private static List<TcpClient> _connectedSlaves = new();

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
            _masterAppPort = int.Parse(GetArgument(args, "--masterport") ?? "9000");

            Console.WriteLine($"Камера {_cameraId} запускается (Master={_isMaster}, Порт={_port})");
            Console.WriteLine($"БД: {_dbHost}:{_dbPort}/{_dbDatabase}");

            if (_isMaster)
            {
                // Мастер-камера запускает сервер для ведомых и подключается к MasterApp
                await StartMasterCameraAsync();
            }
            else
            {
                // Ведомая камера подключается к мастеру
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
            
            // Запуск сервера для ведомых камер
            _slaveListener = new TcpListener(IPAddress.Any, _port);
            _slaveListener.Start();
            Console.WriteLine($"Сервер запущен. Ожидание подключений ведомых камер...");

            // Отправка широковещательного сообщения
            await BroadcastMasterReadyAsync();

            // Приём подключений от ведомых (ожидаем 3 камеры)
            _ = AcceptSlavesAsync();

            // Подключение к MasterApp для отправки данных
            await ConnectToMasterAppAsync();

            // Ждём подключения хотя бы одной ведомой камеры
            await Task.Delay(1000);

            // Отправка сигнала START всем подключённым камерам
            Console.WriteLine($"Отправка сигнала СТАРТ {_connectedSlaves.Count} ведомым камерам...");
            foreach (var client in _connectedSlaves)
            {
                await SendStartSignalAsync(client);
            }

            // Запуск работы
            await StartCameraWorkAsync(true);
        }

        static async Task AcceptSlavesAsync()
        {
            while (_connectedSlaves.Count < 3)
            {
                try
                {
                    var client = await _slaveListener!.AcceptTcpClientAsync();
                    _connectedSlaves.Add(client);
                    Console.WriteLine($"Подключена ведомая камера от {client.Client.RemoteEndPoint}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка принятия подключения: {ex.Message}");
                    break;
                }
            }
        }

        static async Task StartSlaveCameraAsync()
        {
            var masterPort = 9001; // Порт мастер-камеры
            Console.WriteLine($"Ведомая камера {_cameraId} подключается к мастеру на порту {masterPort}...");

            var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", masterPort);
                Console.WriteLine($"Подключено к мастер-камере");
                Console.WriteLine("Ожидание сигнала СТАРТ...");
                await WaitForStartSignalAsync(client);
                
                // Подключение к MasterApp
                await ConnectToMasterAppAsync();
                
                await StartCameraWorkAsync(false);
            }
            catch (Exception ex)
            {
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
                _masterAppClient = new ClientWebSocket();
                await _masterAppClient.ConnectAsync(
                    new Uri($"ws://localhost:{_masterAppPort}/ws"), 
                    CancellationToken.None);
                Console.WriteLine($"Подключено к MasterApp на порту {_masterAppPort}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка подключения к MasterApp: {ex.Message}");
            }
        }

        static async Task StartCameraWorkAsync(bool isMaster)
        {
            Console.WriteLine($"Камера {_cameraId} начинает работу...");

            var random = new Random();
            var connectionString = $"Host={_dbHost};Port={_dbPort};Database={_dbDatabase};Username={_dbUsername};Password={_dbPassword}";
            Console.WriteLine($"Подключение к БД: {connectionString}");

            // Получение настроек валидации
            var gtinErrorRate = _configuration.GetValue<double>("Validation:GtinErrorRate", 0.01);
            var snErrorRate = _configuration.GetValue<double>("Validation:SerialNumberErrorRate", 0.0005);
            var cryptoErrorRate = _configuration.GetValue<double>("Validation:CryptoFormatErrorRate", 0.00001);

            int messageCount = 0;
            while (messageCount < 100)
            {
                var delay = random.Next(10, 90);
                await Task.Delay(delay);

                messageCount++;
                
                // Генерация тестовых данных
                var gtin = GenerateGtin(random, gtinErrorRate);
                var serialNumber = GenerateSerialNumber(random, snErrorRate);
                var cryptoCode = GenerateCryptoCode(random, cryptoErrorRate);

                // Определение валидности
                var isValid = !gtin.Contains("INVALID") && !serialNumber.Contains("INVALID") && !cryptoCode.Contains("INVALID");
                var validationResult = isValid ? "OK" : GetValidationError(gtin, serialNumber, cryptoCode);

                Console.WriteLine($"Камера {_cameraId}: Сообщение #{messageCount} (GTIN={gtin}, SN={serialNumber}, Valid={isValid})");

                // Отправка данных в MasterApp
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
            
            // Генерация валидного GTIN-14
            var gtin = new StringBuilder();
            for (int i = 0; i < 13; i++)
            {
                gtin.Append(random.Next(0, 10));
            }
            // Контрольная цифра
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
                return;

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
                var bytes = Encoding.UTF8.GetBytes(json);
                await _masterAppClient.SendAsync(
                    new ArraySegment<byte>(bytes), 
                    WebSocketMessageType.Text, 
                    true, 
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка отправки в MasterApp: {ex.Message}");
            }
        }
    }
}
