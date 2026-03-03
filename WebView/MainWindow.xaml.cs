using System.Collections.ObjectModel;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WebView
{
    public partial class MainWindow : Window
    {
        private ClientWebSocket? _webSocket;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _reconnectTimer;
        private readonly JsonSerializerOptions _jsonOptions;
        
        private const string DebugSessionId = "49c081";
        private const string DebugLogPath = @"C:\Users\gada\Desktop\TestProject\debug-49c081.log";
        
        private void DebugLog(string location, string message, object? data = null)
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var payload = new
                {
                    sessionId = DebugSessionId,
                    runId = "webview",
                    location,
                    message,
                    data,
                    timestamp = now
                };
                File.AppendAllText(DebugLogPath, JsonSerializer.Serialize(payload) + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private int _totalMessages;
        private int _validMessages;
        private int _invalidMessages;
        private int _messagesLastSecond;
        private DateTime _lastMessageTime;
        private int _currentFrame;

        public ObservableCollection<CameraViewModel> Cameras { get; } = new();
        public ObservableCollection<MessageViewModel> Messages { get; } = new();
        public ObservableCollection<LogEntry> LogEntries { get; } = new();

        public MainWindow()
        {
            try
            {
                DebugLog("MainWindow", "Constructor started");
                InitializeComponent();
                DebugLog("MainWindow", "InitializeComponent completed");

                _jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                InitializeCameras();
                MessagesDataGrid.ItemsSource = Messages;
                LogListBox.ItemsSource = LogEntries;
                _clockTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _clockTimer.Tick += (s, e) =>
                {
                    CurrentTimeText.Text = DateTime.Now.ToString("HH:mm:ss");
                };
                _clockTimer.Start();

                _reconnectTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _reconnectTimer.Tick += async (s, e) =>
                {
                    _reconnectTimer.Stop();
                    await ConnectAsync();
                };

                _lastMessageTime = DateTime.Now;
                Log("Инициализация интерфейса...", Colors.LightBlue);
                DebugLog("MainWindow", "Starting ConnectAsync");
                _ = ConnectAsync();
                DebugLog("MainWindow", "Constructor completed");
            }
            catch (Exception ex)
            {
                DebugLog("MainWindow", "Constructor ERROR", new { exType = ex.GetType().FullName, ex.Message, ex.StackTrace });
                MessageBox.Show(ex.ToString());
            }
        }

        private void InitializeCameras()
        {
            Cameras.Add(new CameraViewModel { Id = 1, Title = "Камера 1", IsMaster = false });
            Cameras.Add(new CameraViewModel { Id = 2, Title = "Камера 2", IsMaster = false });
            Cameras.Add(new CameraViewModel { Id = 3, Title = "Камера 3", IsMaster = false });
            Cameras.Add(new CameraViewModel { Id = 4, Title = "Камера 4", IsMaster = false });

            CamerasItemsControl.ItemsSource = Cameras;
        }

        private async Task ConnectAsync()
        {
            try
            {
                DebugLog("ConnectAsync", "Connecting to ws://localhost:8080/ws");
                UpdateConnectionStatus(ConnectionState.Connecting);
                Log("Подключение к серверу...", Colors.LightBlue);

                _webSocket = new ClientWebSocket();
                await _webSocket.ConnectAsync(new Uri("ws://localhost:8080/ws"), CancellationToken.None);

                DebugLog("ConnectAsync", "Connected successfully", new { state = _webSocket.State });
                UpdateConnectionStatus(ConnectionState.Connected);
                Log("Подключение установлено", Colors.LightGreen);
                _ = ReceiveMessagesAsync();
            }
            catch (Exception ex)
            {
                DebugLog("ConnectAsync", "Connection ERROR", new { exType = ex.GetType().FullName, ex.Message });
                UpdateConnectionStatus(ConnectionState.Disconnected);
                Log($"Ошибка подключения: {ex.Message}", Colors.Red);
                _reconnectTimer.Start();
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            if (_webSocket == null) return;

            var buffer = new byte[4096];

            try
            {
                DebugLog("ReceiveMessagesAsync", "Started listening for messages");
                while (_webSocket.State == WebSocketState.Open)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        DebugLog("ReceiveMessagesAsync", "Received Close message");
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    DebugLog("ReceiveMessagesAsync", "Received message", new { length = message.Length, preview = message.Substring(0, Math.Min(50, message.Length)) });
                    ProcessMessage(message);
                }
            }
            catch (Exception ex)
            {
                DebugLog("ReceiveMessagesAsync", "ERROR", new { exType = ex.GetType().FullName, ex.Message });
                Log($"Ошибка приёма сообщения: {ex.Message}", Colors.Red);
                Dispatcher.Invoke(() =>
                {
                    UpdateConnectionStatus(ConnectionState.Disconnected);
                    _reconnectTimer.Start();
                });
            }
        }

        private void ProcessMessage(string json)
        {
            try
            {
                var messageData = JsonSerializer.Deserialize<CameraMessageData>(json, _jsonOptions);
                if (messageData == null) return;

                Dispatcher.Invoke(() =>
                {
                    HandleMessageData(messageData);
                });
            }
            catch (Exception ex)
            {
                Log($"Ошибка парсинга сообщения: {ex.Message}", Colors.Red);
            }
        }

        private void HandleMessageData(CameraMessageData data)
        {
            _totalMessages++;
            _messagesLastSecond++;

            if (data.IsValid)
                _validMessages++;
            else
                _invalidMessages++;
            if (data.MasterCameraId.HasValue)
            {
                var masterId = data.MasterCameraId.Value;
                foreach (var cam in Cameras)
                {
                    cam.IsMaster = (cam.Id == masterId);
                }
            }

            var camera = Cameras.FirstOrDefault(c => c.Id == data.CameraId);
            if (camera != null)
            {
                camera.MessageCount++;
                camera.IsOnline = true;

                if (data.IsValid)
                    camera.ValidCount++;
                else
                    camera.InvalidCount++;
            }

            var displayData = !string.IsNullOrEmpty(data.Gtin) ? data.Gtin : 
                              (!string.IsNullOrEmpty(data.RawData) ? data.RawData : "N/A");

            var message = new MessageViewModel
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                CameraId = data.CameraId,
                Data = displayData,
                IsValid = data.IsValid,
                Result = data.ValidationResult ?? "OK",
                Frame = data.Frame ?? _currentFrame
            };

            Messages.Insert(0, message);
            if (Messages.Count > 50)
                Messages.RemoveAt(Messages.Count - 1);
            if (data.Frame.HasValue)
                _currentFrame = data.Frame.Value;
            if ((DateTime.Now - _lastMessageTime).TotalSeconds >= 1)
            {
                MessagesPerSecText.Text = _messagesLastSecond.ToString();
                _messagesLastSecond = 0;
                _lastMessageTime = DateTime.Now;
            }
            TotalMessagesText.Text = _totalMessages.ToString();
            ValidMessagesText.Text = _validMessages.ToString();
            InvalidMessagesText.Text = _invalidMessages.ToString();
            CurrentFrameText.Text = _currentFrame.ToString();
            Log($"Получено от камеры {data.CameraId}: {(data.IsValid ? "ВАЛИДНО" : "НЕВАЛИДНО")} [{data.ValidationResult ?? "OK"}]",
                data.IsValid ? Colors.LightGreen : Colors.Orange);
        }

        private void UpdateConnectionStatus(ConnectionState state)
        {
            Dispatcher.Invoke(() =>
            {
                switch (state)
                {
                    case ConnectionState.Connected:
                        WsIndicator.Fill = new SolidColorBrush(Color.FromRgb(0, 255, 136));
                        WsIndicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = Color.FromRgb(0, 255, 136),
                            BlurRadius = 10
                        };
                        WsStatusText.Text = "Подключено";
                        break;
                    case ConnectionState.Disconnected:
                        WsIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 71, 87));
                        WsIndicator.Effect = null;
                        WsStatusText.Text = "Отключено";
                        break;
                    case ConnectionState.Connecting:
                        WsIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 165, 2));
                        WsIndicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = Color.FromRgb(255, 165, 2),
                            BlurRadius = 10
                        };
                        WsStatusText.Text = "Подключение...";
                        break;
                }
            });
        }

        private void Log(string message, Color color)
        {
            Dispatcher.Invoke(() =>
            {
                var entry = new LogEntry
                {
                    Text = $"[{DateTime.Now:HH:mm:ss}] {message}",
                    Color = color
                };

                LogEntries.Insert(0, entry);
                if (LogEntries.Count > 100)
                    LogEntries.RemoveAt(LogEntries.Count - 1);
            });
        }

        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }

            _clockTimer.Stop();
            _reconnectTimer.Stop();

            base.OnClosing(e);
        }
    }

    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected
    }

    public class CameraViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public bool IsMaster { get; set; }

        private bool _isOnline;
        public bool IsOnline
        {
            get => _isOnline;
            set { _isOnline = value; OnPropertyChanged(nameof(IsOnline)); }
        }

        private int _messageCount;
        public int MessageCount
        {
            get => _messageCount;
            set { _messageCount = value; OnPropertyChanged(nameof(MessageCount)); }
        }

        private int _validCount;
        public int ValidCount
        {
            get => _validCount;
            set { _validCount = value; OnPropertyChanged(nameof(ValidCount)); }
        }

        private int _invalidCount;
        public int InvalidCount
        {
            get => _invalidCount;
            set { _invalidCount = value; OnPropertyChanged(nameof(InvalidCount)); }
        }

        public double SuccessPercent => MessageCount > 0 ? (double)ValidCount / MessageCount * 100 : 0;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
            if (propertyName is nameof(MessageCount) or nameof(ValidCount))
                OnPropertyChanged(nameof(SuccessPercent));
        }
    }

    public class MessageViewModel
    {
        public string Time { get; set; } = string.Empty;
        public int CameraId { get; set; }
        public string Data { get; set; } = string.Empty;
        public bool IsValid { get; set; }
        public string Result { get; set; } = string.Empty;
        public int Frame { get; set; }
    }

    public class LogEntry
    {
        public string Text { get; set; } = string.Empty;
        public Color Color { get; set; }
    }

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
        public DateTime Timestamp { get; set; }
        public int? MasterCameraId { get; set; }
    }
}
