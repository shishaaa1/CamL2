using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Timers;
using Timer = System.Timers.Timer;

namespace MasterApp
{
    /// <summary>
    /// Обработчик фреймов - собирает сообщения за 100 мс и обрабатывает пакетом
    /// </summary>
    public class FrameProcessor
    {
        private readonly ConcurrentQueue<CameraMessageData> _messageQueue = new();
        private readonly List<CameraMessageData> _currentFrame = new();
        private readonly Timer _frameTimer;
        private readonly int _frameWaitTimeMs;
        private readonly int _responseTimeoutMs;
        private DateTime _firstMessageTime;
        private bool _waitingForFrame;
        private int _frameCounter;

        public event EventHandler<FrameResult>? FrameProcessed;

        public FrameProcessor(int frameWaitTimeMs = 100, int responseTimeoutMs = 30)
        {
            _frameWaitTimeMs = frameWaitTimeMs;
            _responseTimeoutMs = responseTimeoutMs;

            _frameTimer = new Timer(_frameWaitTimeMs);
            _frameTimer.Elapsed += OnFrameTimerElapsed;
            _frameTimer.AutoReset = false;
        }

        public void Start()
        {
            _frameTimer.Start();
        }

        public void Stop()
        {
            _frameTimer.Stop();
        }

        public void AddMessage(CameraMessageData message)
        {
            _messageQueue.Enqueue(message);

            if (!_waitingForFrame)
            {
                _waitingForFrame = true;
                _firstMessageTime = DateTime.Now;
                _frameTimer.Start();
            }
        }

        private void OnFrameTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            ProcessFrame();
        }

        private void ProcessFrame()
        {
            _currentFrame.Clear();

            while (_messageQueue.TryDequeue(out var message))
            {
                _currentFrame.Add(message);
            }

            if (_currentFrame.Count > 0)
            {
                _frameCounter++;
                var result = new FrameResult
                {
                    FrameNumber = _frameCounter,
                    Messages = _currentFrame.ToList(),
                    Timestamp = DateTime.Now,
                    ProcessingTimeMs = (int)(DateTime.Now - _firstMessageTime).TotalMilliseconds
                };

                FrameProcessed?.Invoke(this, result);
            }

            _waitingForFrame = false;
        }

        public int GetFrameCount() => _frameCounter;
    }

    /// <summary>
    /// Результат обработки фрейма
    /// </summary>
    public class FrameResult
    {
        public int FrameNumber { get; set; }
        public List<CameraMessageData> Messages { get; set; } = new();
        public DateTime Timestamp { get; set; }
        public int ProcessingTimeMs { get; set; }
    }

    /// <summary>
    /// Сервер для подключения внешних клиентов (получают результаты валидации)
    /// </summary>
    public class ResultSocketServer
    {
        private readonly TcpListener _listener;
        private readonly int _port;
        private readonly List<TcpClient> _clients = new();
        private bool _isRunning;

        public ResultSocketServer(int port)
        {
            _port = port;
            _listener = new TcpListener(IPAddress.Any, port);
        }

        public void Start()
        {
            _isRunning = true;
            _listener.Start();
            Console.WriteLine($"Сервер результатов запущен на порту {_port}");
            Task.Run(() => AcceptClientsAsync());
        }

        public void Stop()
        {
            _isRunning = false;
            _listener.Stop();

            foreach (var client in _clients)
            {
                client.Close();
            }
        }

        private async Task AcceptClientsAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _clients.Add(client);
                    Console.WriteLine($"Клиент результатов подключён: {client.Client.RemoteEndPoint}");
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Console.WriteLine($"Ошибка принятия клиента: {ex.Message}");
                }
            }
        }

        public async Task SendResultAsync(FrameResult result)
        {
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = false
            });
            var bytes = Encoding.UTF8.GetBytes(json);

            var disconnectedClients = new List<TcpClient>();

            foreach (var client in _clients)
            {
                try
                {
                    if (client.Connected)
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(bytes);
                        await stream.WriteAsync(new byte[] { 0x0A });
                    }
                    else
                    {
                        disconnectedClients.Add(client);
                    }
                }
                catch (Exception)
                {
                    disconnectedClients.Add(client);
                }
            }
            foreach (var client in disconnectedClients)
            {
                _clients.Remove(client);
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// Сервис валидации данных
    /// </summary>
    public class ValidationService
    {
        private readonly double _gtinErrorRate;
        private readonly double _snErrorRate;
        private readonly double _cryptoErrorRate;

        public ValidationService(double gtinErrorRate = 0.01, double snErrorRate = 0.0005, double cryptoErrorRate = 0.00001)
        {
            _gtinErrorRate = gtinErrorRate;
            _snErrorRate = snErrorRate;
            _cryptoErrorRate = cryptoErrorRate;
        }

        public ValidationResult Validate(CameraMessageData message)
        {
            var result = new ValidationResult
            {
                MessageId = Guid.NewGuid(),
                CameraId = message.CameraId,
                Timestamp = DateTime.Now,
                IsValid = true,
                Errors = new List<string>()
            };
            var gtinValid = ValidateGtin(message.Gtin);
            if (!gtinValid)
            {
                result.IsValid = false;
                result.Errors.Add("Неверный GTIN");
            }
            var snValid = ValidateSerialNumber(message.SerialNumber);
            if (!snValid)
            {
                result.IsValid = false;
                result.Errors.Add("Неверный серийный номер");
            }
            var cryptoValid = ValidateCryptoCode(message.CryptoCode);
            if (!cryptoValid)
            {
                result.IsValid = false;
                result.Errors.Add("Неверный криптокод");
            }

            result.ResultMessage = result.IsValid ? "OK" : string.Join("; ", result.Errors);

            return result;
        }

        private bool ValidateGtin(string? gtin)
        {
            if (string.IsNullOrEmpty(gtin))
                return false;
            if (gtin.Contains("INVALID"))
                return false;
            if (gtin.Length != 14 || !gtin.All(char.IsDigit))
                return false;
            return ValidateGtinCheckDigit(gtin);
        }

        private bool ValidateGtinCheckDigit(string gtin)
        {
            int sum = 0;
            for (int i = 0; i < 13; i++)
            {
                sum += (gtin[i] - '0') * (i % 2 == 0 ? 1 : 3);
            }
            int checkDigit = (10 - (sum % 10)) % 10;
            return checkDigit == (gtin[13] - '0');
        }

        private bool ValidateSerialNumber(string? serialNumber)
        {
            if (string.IsNullOrEmpty(serialNumber))
                return false;

            if (serialNumber.Contains("INVALID"))
                return false;
            return serialNumber.StartsWith("SN") && serialNumber.Length >= 8;
        }

        private bool ValidateCryptoCode(string? cryptoCode)
        {
            if (string.IsNullOrEmpty(cryptoCode))
                return false;

            if (cryptoCode.Contains("INVALID"))
                return false;
            try
            {
                var bytes = Convert.FromBase64String(cryptoCode);
                return bytes.Length >= 32;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Результат валидации
    /// </summary>
    public class ValidationResult
    {
        public Guid MessageId { get; set; }
        public int CameraId { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsValid { get; set; }
        public string ResultMessage { get; set; } = string.Empty;
        public List<string> Errors { get; set; } = new();
    }
}
