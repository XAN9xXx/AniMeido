using Microsoft.Data.Sqlite;

namespace AniMeido.App.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;
        public string DbPath { get; }



        public DatabaseService()
        {
            DbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AniMeido",
                "AniMeido.db"
            );
            string dir = Path.GetDirectoryName(DbPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={DbPath}";
        }


        public async Task InitializeAsync()
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS tracking(
                    AnimeID   INTEGER PRIMARY KEY,
                    Status    INTEGER NOT NULL,
                    UpdatedAt TEXT NOT NULL
                )
            """;
            await command.ExecuteNonQueryAsync();

            command.CommandText = """
                CREATE TABLE IF NOT EXISTS cache(
                    CacheKey  TEXT PRIMARY KEY,
                    Data      TEXT NOT NULL,
                    ExpiresAt TEXT NOT NULL
                )
            """;
            await command.ExecuteNonQueryAsync();
        }
    }
}