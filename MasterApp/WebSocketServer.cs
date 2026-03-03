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

        public event EventHandler<CameraMessageData>? MessageReceived;
        public event EventHandler? ClientConnected;
        public event EventHandler? ClientDisconnected;

        public WebSocketServer(int port)
        {
            _port = port;
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{port}/");
        }

        public void Start()
        {
            _isRunning = true;
            _httpListener.Start();
            Task.Run(() => ListenAsync());
        }

        public void Stop()
        {
            _isRunning = false;
            _httpListener.Stop();
            _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None).Wait();
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
