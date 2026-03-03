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
        private readonly int _masterCameraId;
        private WebSocket? _webViewSocket;  // Подключение WebView интерфейса
        private readonly List<WebSocket> _cameraSockets = new();  // Подключения камер
        private bool _isRunning;
        private readonly FrameProcessor? _frameProcessor;
        private readonly ValidationService? _validationService;
        private readonly ResultSocketServer? _resultServer;

        public event EventHandler<CameraMessageData>? MessageReceived;
        public event EventHandler? ClientConnected;
        public event EventHandler? ClientDisconnected;

        public WebSocketServer(int port, int masterCameraId, FrameProcessor? frameProcessor = null, ValidationService? validationService = null, ResultSocketServer? resultServer = null)
        {
            _port = port;
            _masterCameraId = masterCameraId;
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
            
            // Закрываем WebView подключение
            _webViewSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait();
            
            // Закрываем все подключения камер
            foreach (var cameraSocket in _cameraSockets)
            {
                try
                {
                    cameraSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait();
                }
                catch { }
            }
            _cameraSockets.Clear();
        }

        private async void OnFrameProcessed(object? sender, FrameResult result)
        {
            Console.WriteLine($"[FrameProcessor] Обработка фрейма #{result.FrameNumber} ({result.Messages.Count} сообщений)");
            
            foreach (var message in result.Messages)
            {
                if (_validationService != null)
                {
                    var validationResult = _validationService.Validate(message);
                    message.IsValid = validationResult.IsValid;
                    message.ValidationResult = validationResult.ResultMessage;
                    Console.WriteLine($"[FrameProcessor] Сообщение от камеры {message.CameraId}: Valid={message.IsValid}, Result={message.ValidationResult}");
                }
            }

            // Отправка результата внешнему клиенту
            if (_resultServer != null)
            {
                await _resultServer.SendResultAsync(result);
            }

            // Отправка обновлённых данных в WebView (только если клиент подключён)
            if (_webViewSocket != null && _webViewSocket.State == WebSocketState.Open)
            {
                Console.WriteLine($"[FrameProcessor] Отправка {result.Messages.Count} сообщений в WebView");
                foreach (var message in result.Messages)
                {
                    await SendToWebViewAsync(message);
                }
            }
            else
            {
                var wsState = _webViewSocket?.State.ToString() ?? "null";
                Console.WriteLine($"[FrameProcessor] WARNING: WebView не подключён (state={wsState})");
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
            if (context.Request.IsWebSocketRequest)
            {
                var path = context.Request.Url?.AbsolutePath ?? "";
                
                if (path == "/ws")
                {
                    // Подключение WebView интерфейса
                    await HandleWebViewConnectionAsync(context);
                }
                else if (path.StartsWith("/camera/"))
                {
                    // Подключение камеры
                    await HandleCameraConnectionAsync(context);
                }
                else
                {
                    // Неизвестный путь - 404
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
            else
            {
                // Обычный HTTP запрос - возвращаем 404
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }

        private async Task HandleWebViewConnectionAsync(HttpListenerContext context)
        {
            try
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                _webViewSocket = webSocketContext.WebSocket;

                Console.WriteLine($"[WebSocket] WebView клиент подключён. State={_webViewSocket.State}");
                ClientConnected?.Invoke(this, EventArgs.Empty);

                await ReceiveWebViewMessagesAsync();
                
                Console.WriteLine($"[WebSocket] WebView клиент завершил работу. State={_webViewSocket?.State}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Ошибка WebSocket WebView: {ex.Message}");
            }
            finally
            {
                if (_webViewSocket != null)
                {
                    await _webViewSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    _webViewSocket = null;
                }

                Console.WriteLine("[WebSocket] WebView клиент отключён");
                ClientDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task HandleCameraConnectionAsync(HttpListenerContext context)
        {
            try
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                var cameraSocket = webSocketContext.WebSocket;

                lock (_cameraSockets)
                {
                    _cameraSockets.Add(cameraSocket);
                }

                var cameraId = context.Request.Url?.AbsolutePath.Replace("/camera/", "") ?? "?";
                Console.WriteLine($"[WebSocket] Камера {cameraId} подключена к WebSocket. Всего подключений: {_cameraSockets.Count}");

                // Важно: ожидаем завершения приёма сообщений, иначе сокет закроется
                await ReceiveCameraMessagesAsync(cameraSocket, cameraId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Ошибка WebSocket камеры: {ex.Message}");
            }
        }

        private async Task ReceiveWebViewMessagesAsync()
        {
            var ws = _webViewSocket;
            if (ws == null) return;

            var buffer = new byte[4096];

            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                var wsState = _webViewSocket?.State;
                if (wsState == WebSocketState.Open)
                    Console.WriteLine($"Ошибка приёма WebView: {ex.Message}");
            }
        }

        private async Task ReceiveCameraMessagesAsync(WebSocket cameraSocket, string cameraId)
        {
            var buffer = new byte[4096];
            Console.WriteLine($"[WebSocket] Начат приём сообщений от камеры {cameraId}");

            try
            {
                while (cameraSocket.State == WebSocketState.Open)
                {
                    var result = await cameraSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine($"[WebSocket] Камера {cameraId} отправила Close");
                        await cameraSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        Console.WriteLine($"[WebSocket] Получено сообщение от камеры {cameraId}: {message}");
                        var messageData = JsonSerializer.Deserialize<CameraMessageData>(message);
                        if (messageData != null)
                        {
                            Console.WriteLine($"[WebSocket] Сообщение от камеры {messageData.CameraId}, добавляем в процессор фреймов");
                            MessageReceived?.Invoke(this, messageData);
                            _frameProcessor?.AddMessage(messageData);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var wsState = cameraSocket.State;
                if (wsState == WebSocketState.Open)
                    Console.WriteLine($"[WebSocket] Ошибка приёма от камеры {cameraId}: {ex.Message}");
            }
            finally
            {
                lock (_cameraSockets)
                {
                    _cameraSockets.Remove(cameraSocket);
                }
                cameraSocket.Dispose();
                Console.WriteLine($"[WebSocket] Камера {cameraId} отключена. Осталось подключений: {_cameraSockets.Count}");
            }
        }

        public async Task SendToWebViewAsync(CameraMessageData data)
        {
            var ws = _webViewSocket;
            if (ws == null || ws.State != WebSocketState.Open)
            {
                Console.WriteLine($"[WebSocket] SendToWebViewAsync отменён: ws={ws}, state={(ws?.State).ToString() ?? "null"}");
                return;
            }

            try
            {
                // Добавляем информацию о мастер-камере
                data.MasterCameraId = _masterCameraId;

                var json = JsonSerializer.Serialize(data);
                Console.WriteLine($"[WebSocket] Отправка в WebView: {json}");
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
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
        public int? MasterCameraId { get; set; }
    }
}
