using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MasterApp
{
    /// <summary>
    /// WebSocket сервер для подключения WebView клиента
    /// </summary>
    public class WebSocketServer
    {
        private readonly HttpListener _httpListener;
        private readonly int _port;
        private WebSocket? _webSocket;
        private bool _isRunning;
        private readonly FrameProcessor? _frameProcessor;
        private readonly ValidationService? _validationService;
        private readonly ResultSocketServer? _resultServer;

        public event EventHandler<CameraMessageData>? MessageReceived;
        public event EventHandler? ClientConnected;
        public event EventHandler? ClientDisconnected;

        public WebSocketServer(int port, FrameProcessor? frameProcessor = null, ValidationService? validationService = null, ResultSocketServer? resultServer = null)
        {
            _port = port;
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{port}/");
            _frameProcessor = frameProcessor;
            _validationService = validationService;
            _resultServer = resultServer;
        }

        public void Start()
        {
            _isRunning = true;
            _httpListener.Start();
            
            // Подписка на обработку фреймов
            if (_frameProcessor != null)
            {
                _frameProcessor.FrameProcessed += OnFrameProcessed;
                _frameProcessor.Start();
            }

            Task.Run(() => ListenAsync());
        }

        public void Stop()
        {
            _isRunning = false;
            
            if (_frameProcessor != null)
            {
                _frameProcessor.FrameProcessed -= OnFrameProcessed;
                _frameProcessor.Stop();
            }

            _httpListener.Stop();
            _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait();
        }

        private async void OnFrameProcessed(object? sender, FrameResult result)
        {
            // Валидация каждого сообщения в фрейме
            foreach (var message in result.Messages)
            {
                if (_validationService != null)
                {
                    var validationResult = _validationService.Validate(message);
                    message.IsValid = validationResult.IsValid;
                    message.ValidationResult = validationResult.ResultMessage;
                }
            }

            // Отправка результата внешнему клиенту
            if (_resultServer != null)
            {
                await _resultServer.SendResultAsync(result);
            }

            // Отправка обновлённых данных в WebView
            foreach (var message in result.Messages)
            {
                await SendAsync(message);
            }

            Console.WriteLine($"Фрейм #{result.FrameNumber} обработан за {result.ProcessingTimeMs} мс ({result.Messages.Count} сообщений)");
        }

        private async Task ListenAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    _ = HandleRequestAsync(context);
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Console.WriteLine($"Ошибка HTTP: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            if (context.Request.IsWebSocketRequest && context.Request.Url?.AbsolutePath == "/ws")
            {
                await HandleWebSocketRequestAsync(context);
            }
            else
            {
                // Обычный HTTP запрос - возвращаем 404
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }

        private async Task HandleWebSocketRequestAsync(HttpListenerContext context)
        {
            try
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                _webSocket = webSocketContext.WebSocket;

                Console.WriteLine("WebView клиент подключён");
                ClientConnected?.Invoke(this, EventArgs.Empty);

                await ReceiveMessagesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка WebSocket: {ex.Message}");
            }
            finally
            {
                if (_webSocket != null)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    _webSocket = null;
                }

                Console.WriteLine("WebView клиент отключён");
                ClientDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            if (_webSocket == null) return;

            var buffer = new byte[4096];

            while (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        var messageData = JsonSerializer.Deserialize<CameraMessageData>(message);
                        if (messageData != null)
                        {
                            MessageReceived?.Invoke(this, messageData);
                            
                            // Добавляем сообщение в обработчик фреймов
                            _frameProcessor?.AddMessage(messageData);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_webSocket.State == WebSocketState.Open)
                        Console.WriteLine($"Ошибка приёма: {ex.Message}");
                    break;
                }
            }
        }

        public async Task SendAsync(CameraMessageData data)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                return;

            try
            {
                var json = JsonSerializer.Serialize(data);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка отправки: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Модель данных сообщения от камеры
    /// </summary>
    public class CameraMessageData
    {
        public int CameraId { get; set; }
        public string? RawData { get; set; }
        public string? Gtin { get; set; }
        public string? SerialNumber { get; set; }
        public string? CryptoCode { get; set; }
        public bool IsValid { get; set; }
        public string? ValidationResult { get; set; }
        public int? Frame { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
