using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace CameraClient
{
    class Program
    {
        private static IConfiguration _configuration = null!;
        private static int _cameraId;
        private static int _port;
        private static bool _isMaster;

        static async Task Main(string[] args)
        {
            _configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            // Парсинг аргументов командной строки
            _cameraId = int.Parse(GetArgument(args, "--id") ?? "1");
            _port = int.Parse(GetArgument(args, "--port") ?? "9001");
            _isMaster = GetArgument(args, "--ismaster")?.ToLower() == "true";

            Console.WriteLine($"Камера {_cameraId} запускается (Master={_isMaster}, Порт={_port})");

            if (_isMaster)
            {
                // Мастер-камера запускает сервер для ведомых
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
            var listener = new TcpListener(IPAddress.Any, _port);
            listener.Start();

            Console.WriteLine($"Сервер запущен. Ожидание подключений ведомых камер...");
            await BroadcastMasterReadyAsync();

            // Приём подключений
            var clients = new List<TcpClient>();
            for (int i = 0; i < 3; i++) // Ожидание 3 ведомых камер
            {
                var client = await listener.AcceptTcpClientAsync();
                clients.Add(client);
                Console.WriteLine($"Подключена ведомая камера от {client.Client.RemoteEndPoint}");
            }

            Console.WriteLine("Отправка сигнала СТАРТ всем камерам...");
            await Task.Delay(500);

            foreach (var client in clients)
            {
                await SendStartSignalAsync(client);
            }

            await StartCameraWorkAsync(true);
        }

        static async Task StartSlaveCameraAsync()
        {
            var masterPort = 9001;
            Console.WriteLine($"Ведомая камера {_cameraId} подключается к мастеру на порту {masterPort}...");

            var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", masterPort);
                Console.WriteLine($"Подключено к мастер-камере");
                Console.WriteLine("Ожидание сигнала СТАРТ...");
                await WaitForStartSignalAsync(client);
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

        static async Task StartCameraWorkAsync(bool isMaster)
        {
            Console.WriteLine($"Камера {_cameraId} начинает работу...");

            var random = new Random();
            var connectionString = _configuration.GetConnectionString("PostgresConnection");

            Console.WriteLine($"Подключение к БД: {connectionString}");
            int messageCount = 0;
            while (messageCount < 10) 
            {
                var delay = random.Next(10, 90);
                await Task.Delay(delay);

                messageCount++;
                Console.WriteLine($"Камера {_cameraId}: Сообщение #{messageCount} (задержка {delay}мс)");

                // await SendDataToMasterAsync();
            }

            Console.WriteLine($"Камера {_cameraId} завершила работу");
            Console.WriteLine("Нажмите любую клавишу для выхода...");
            Console.ReadKey();
        }
    }
}
