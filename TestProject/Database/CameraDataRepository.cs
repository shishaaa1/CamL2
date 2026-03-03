using Npgsql;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace L2App.Database
{
    /// <summary>
    /// Простейший репозиторий чтения кодов из БД L2.
    /// Схема ориентирована на описанное ТЗ: GTIN из Schemes, SN и Code из Items.
    /// </summary>
    public class CameraCodeRecord
    {
        public string Gtin { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        /// <summary>
        /// Криптокод / Code из Items (длина 4).
        /// </summary>
        public string CryptoCode { get; set; } = string.Empty;
    }

    public class CameraDataRepository
    {
        private readonly string _connectionString;

        public CameraDataRepository(DBSettings settings)
        {
            _connectionString = settings.ConnectionStrings;
        }

        /// <summary>
        /// Возвращает все записи с кодами.
        /// Ожидается структура БД:
        ///   Schemes(Id, GTIN, TaskId, ...)
        ///   Items(Id, SN, Code, Crypto, TaskId, SchemeId, ...)
        /// </summary>
        public async Task<IReadOnlyList<CameraCodeRecord>> GetAllCodesAsync(int cameraId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Берём GTIN из Schemes, а SN / Code / Crypto из Items через SchemeId.
            // CameraId в схеме нет, поэтому cameraId сейчас не используется
            // (оставляем параметр метода на будущее, если появится привязка камер к заданиям).
            const string sql = @"
                SELECT s.""GTIN"", i.""SN"", i.""Code""
                FROM ""Items"" i
                JOIN ""Schemes"" s ON i.""SchemeId"" = s.""Id""
                ORDER BY i.""Id"";";

            await using var cmd = new NpgsqlCommand(sql, connection);

            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new List<CameraCodeRecord>();
            while (await reader.ReadAsync())
            {
                result.Add(new CameraCodeRecord
                {
                    Gtin = reader.GetString(0),
                    SerialNumber = reader.GetString(1),
                    CryptoCode = reader.GetString(2)
                });
            }

            return result;
        }
    }
}

