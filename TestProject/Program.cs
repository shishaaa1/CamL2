using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql; 

namespace L2App 
{
    class Program
    {
        private static IConfiguration _configuration;

        static async Task Main(string[] args)
        {
            _configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Console.WriteLine("Привет");

            await CheckDBConnectionAsync();

            startTCPConnect("127.0.0.1", 5432);

            Console.WriteLine("Нажмите любую клавишу для выхода...");
            Console.ReadKey();
        }

        static void startTCPConnect(string host, int port)
        {
            try
            {
                using Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sock.Connect(host, port);
                Console.WriteLine($"✓ TCP подключён к {host}:{port}");

                sock.Close();
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"✗ TCP ошибка: {ex.Message}");
            }
        }

        static async Task CheckDBConnectionAsync()
        {
            var connectionString = _configuration.GetConnectionString("PostgresConnection");

            if (string.IsNullOrEmpty(connectionString))
            {
                Console.WriteLine("✗ Строка подключения не найдена в конфигурации!");
                return;
            }

            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                await using var command = new NpgsqlCommand("SELECT NOW()", connection);
                var serverTime = await command.ExecuteScalarAsync();

                Console.WriteLine($"✓ Подключение к PostgreSQL успешно!");
                Console.WriteLine($"   Серверное время: {serverTime}");
            }
            catch (NpgsqlException ex)
            {
                Console.WriteLine($"✗ Ошибка БД: {ex.Message}");
                Console.WriteLine($"   SqlState: {ex.SqlState}, ErrorCode: {ex.ErrorCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Неожиданная ошибка: {ex.Message}");
            }
        }
    }
}