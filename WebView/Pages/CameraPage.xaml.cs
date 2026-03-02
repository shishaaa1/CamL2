using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WebView.Pages
{
    /// <summary>
    /// Логика взаимодействия для CameraPage.xaml
    /// </summary>
    public partial class CameraPage : Page
    {
        int cameraCount=4;
        public CameraPage()
        {
            InitializeComponent();
            AddCamera();
            TcpClientConnection();
        }
        public void AddCamera()
        {
            if (cameraCount >= 1)
            {
                camnum.Text = "";
                for (int i = 0; i <= cameraCount; i++)
                {
                    cameramain.ColumnDefinitions.Add(new ColumnDefinition());
                    TextBlock textBlock = new TextBlock();
                    textBlock.Text = $"Номер камеры:{i} ";
                }
            }
        }
        public async void TcpClientConnection()
        {
            TcpClient tcpClient = new TcpClient();
            await tcpClient.ConnectAsync("127.0.0.1", 5432);
            if (tcpClient.Connected)
            {
                MessageBox.Show("Подключение успешно");
            }
            else
            {
                MessageBox.Show($"Ошибка подключения");
            }
        }
    }
}
