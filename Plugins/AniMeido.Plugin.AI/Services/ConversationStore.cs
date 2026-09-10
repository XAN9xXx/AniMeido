using AniMeido.Plugin.AI.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Services;

internal sealed class ConversationStore
{
    private const int ExportSchemaVersion = 1;
    private const long MaximumImportBytes = 50L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public ConversationStore(AiPluginPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.ConversationsPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task<IReadOnlyList<AiConversation>> GetConversationsAsync(
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ConversationId, Title, TaskKind, Provider, Model,
                   CreatedAt, UpdatedAt, SnapshotJson, SnapshotRevision
            FROM ai_conversations
            WHERE @search = '' OR Title LIKE '%' || @search || '%'
            ORDER BY UpdatedAt DESC
            """;
        command.Parameters.AddWithValue("@search", search?.Trim() ?? string.Empty);
        var result = new List<AiConversation>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadConversation(reader));
        }

        return result;
    }

    public async Task<AiConversation?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ConversationId, Title, TaskKind, Provider, Model,
                   CreatedAt, UpdatedAt, SnapshotJson, SnapshotRevision
            FROM ai_conversations WHERE ConversationId = @id
            """;
        command.Parameters.AddWithValue("@id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadConversation(reader)
            : null;
    }

    public async Task<AiConversation> CreateConversationAsync(
        AiTaskKind taskKind,
        string title,
        AiSettings settings,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new AiConversation(
            Guid.NewGuid().ToString("N"),
            title.Trim(),
            taskKind,
            settings.Provider,
            settings.Model,
            now,
            now,
            null,
            0);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_conversations(
                ConversationId, Title, TaskKind, Provider, Model,
                CreatedAt, UpdatedAt, SnapshotJson, SnapshotRevision)
            VALUES(@id, @title, @task, @provider, @model,
                   @created, @updated, NULL, 0)
            """;
        AddConversationParameters(command, conversation);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return conversation;
    }

    public async Task RenameConversationAsync(
        string conversationId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_conversations
            SET Title = @title, UpdatedAt = @updated
            WHERE ConversationId = @id
            """;
        command.Parameters.AddWithValue("@id", conversationId);
        command.Parameters.AddWithValue("@title", title.Trim());
        command.Parameters.AddWithValue(
            "@updated",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveSnapshotAsync(
        string conversationId,
        string snapshotJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ai_conversations
            SET SnapshotJson = @snapshot,
                SnapshotRevision = SnapshotRevision + 1,
                UpdatedAt = @updated
            WHERE ConversationId = @id
            """;
        command.Parameters.AddWithValue("@id", conversationId);
        command.Parameters.AddWithValue("@snapshot", snapshotJson);
        command.Parameters.AddWithValue(
            "@updated",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiMessage>> GetMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MessageId, ConversationId, Role, Body, CreatedAt,
                   InputTokens, OutputTokens, ToolSummary
            FROM ai_messages
            WHERE ConversationId = @id
            ORDER BY CreatedAt, rowid
            """;
        command.Parameters.AddWithValue("@id", conversationId);
        var result = new List<AiMessage>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadMessage(reader));
        }

        return result;
    }

    public async Task AddMessageAsync(
        AiMessage message,
        CancellationToken cancellationToken = default)
        => await AddMessagesAsync([message], cancellationToken);

    public async Task AddTurnAsync(
        AiMessage userMessage,
        AiMessage assistantMessage,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                userMessage.ConversationId,
                assistantMessage.ConversationId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "同一轮消息必须属于同一个会话。",
                nameof(assistantMessage));
        }

        await AddMessagesAsync(
            [userMessage, assistantMessage],
            cancellationToken);
    }

    private async Task AddMessagesAsync(
        IReadOnlyList<AiMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        foreach (var message in messages)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO ai_messages(
                    MessageId, ConversationId, Role, Body, CreatedAt,
                    InputTokens, OutputTokens, ToolSummary)
                VALUES(@messageId, @conversationId, @role, @body, @created,
                       @input, @output, @tools)
                """;
            AddMessageParameters(command, message);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var latestMessage = messages.MaxBy(message => message.CreatedAt)!;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE ai_conversations SET UpdatedAt = @updated
                WHERE ConversationId = @id
                """;
            update.Parameters.AddWithValue(
                "@id",
                latestMessage.ConversationId);
            update.Parameters.AddWithValue(
                "@updated",
                latestMessage.CreatedAt.ToUniversalTime().ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM ai_conversations WHERE ConversationId = @id";
        command.Parameters.AddWithValue("@id", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ai_conversations";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ExportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var conversations = await GetConversationsAsync(
            cancellationToken: cancellationToken);
        var items = new List<ConversationExportItem>();
        foreach (var conversation in conversations)
        {
            items.Add(new ConversationExportItem(
                conversation,
                await GetMessagesAsync(
                    conversation.ConversationId,
                    cancellationToken)));
        }

        var export = new ConversationExport(
            ExportSchemaVersion,
            DateTimeOffset.UtcNow,
            items);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(
            tempPath,
            JsonSerializer.Serialize(export, JsonOptions),
            cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    public async Task ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumImportBytes)
        {
            throw new InvalidDataException("会话导入文件为空或超过 50 MB。");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var export = JsonSerializer.Deserialize<ConversationExport>(
            json,
            JsonOptions)
            ?? throw new InvalidDataException("会话导入 JSON 无效。");
        if (export.SchemaVersion != ExportSchemaVersion
            || export.Items.Count > 5_000)
        {
            throw new InvalidDataException("会话导入版本或条目数量无效。");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        foreach (var item in export.Items)
        {
            ValidateExportItem(item);
            await using var conversation = connection.CreateCommand();
            conversation.Transaction = (SqliteTransaction)transaction;
            conversation.CommandText = """
                INSERT OR IGNORE INTO ai_conversations(
                    ConversationId, Title, TaskKind, Provider, Model,
                    CreatedAt, UpdatedAt, SnapshotJson, SnapshotRevision)
                VALUES(@id, @title, @task, @provider, @model,
                       @created, @updated, @snapshot, @revision)
                """;
            AddConversationParameters(conversation, item.Conversation);
            await conversation.ExecuteNonQueryAsync(cancellationToken);
            foreach (var message in item.Messages)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT OR IGNORE INTO ai_messages(
                        MessageId, ConversationId, Role, Body, CreatedAt,
                        InputTokens, OutputTokens, ToolSummary)
                    VALUES(@messageId, @conversationId, @role, @body,
                           @created, @input, @output, @tools)
                    """;
                AddMessageParameters(insert, message);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private async Task EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS ai_conversations(
                    ConversationId TEXT PRIMARY KEY,
                    Title TEXT NOT NULL,
                    TaskKind INTEGER NOT NULL,
                    Provider INTEGER NOT NULL,
                    Model TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    SnapshotJson TEXT NULL,
                    SnapshotRevision INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS ai_messages(
                    MessageId TEXT PRIMARY KEY,
                    ConversationId TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    Body TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    InputTokens INTEGER NOT NULL DEFAULT 0,
                    OutputTokens INTEGER NOT NULL DEFAULT 0,
                    ToolSummary TEXT NOT NULL DEFAULT '',
                    FOREIGN KEY(ConversationId)
                        REFERENCES ai_conversations(ConversationId)
                        ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_ai_messages_conversation_time
                    ON ai_messages(ConversationId, CreatedAt);
                PRAGMA user_version=1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private static AiConversation ReadConversation(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            (AiTaskKind)reader.GetInt32(2),
            (AiProviderKind)reader.GetInt32(3),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8));

    private static AiMessage ReadMessage(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseTimestamp(reader.GetString(4)),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetString(7));

    private static void AddConversationParameters(
        SqliteCommand command,
        AiConversation conversation)
    {
        command.Parameters.AddWithValue("@id", conversation.ConversationId);
        command.Parameters.AddWithValue("@title", conversation.Title);
        command.Parameters.AddWithValue("@task", (int)conversation.TaskKind);
        command.Parameters.AddWithValue("@provider", (int)conversation.Provider);
        command.Parameters.AddWithValue("@model", conversation.Model);
        command.Parameters.AddWithValue(
            "@created",
            conversation.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "@updated",
            conversation.UpdatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue(
            "@snapshot",
            conversation.SnapshotJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@revision",
            conversation.SnapshotRevision);
    }

    private static void AddMessageParameters(
        SqliteCommand command,
        AiMessage message)
    {
        command.Parameters.AddWithValue("@messageId", message.MessageId);
        command.Parameters.AddWithValue(
            "@conversationId",
            message.ConversationId);
        command.Parameters.AddWithValue("@role", message.Role);
        command.Parameters.AddWithValue("@body", message.Body);
        command.Parameters.AddWithValue(
            "@created",
            message.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("@input", message.InputTokens);
        command.Parameters.AddWithValue("@output", message.OutputTokens);
        command.Parameters.AddWithValue("@tools", message.ToolSummary);
    }

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static void ValidateExportItem(ConversationExportItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Conversation.ConversationId)
            || string.IsNullOrWhiteSpace(item.Conversation.Title)
            || item.Conversation.Title.Length > 500
            || item.Messages.Count > 100)
        {
            throw new InvalidDataException("会话条目无效。");
        }

        foreach (var message in item.Messages)
        {
            if (message.ConversationId != item.Conversation.ConversationId
                || message.Body.Length > 500_000
                || message.Role is not ("user" or "assistant"))
            {
                throw new InvalidDataException("会话消息无效。");
            }
        }
    }

    private sealed record ConversationExport(
        int SchemaVersion,
        DateTimeOffset ExportedAt,
        IReadOnlyList<ConversationExportItem> Items);

    private sealed record ConversationExportItem(
        AiConversation Conversation,
        IReadOnlyList<AiMessage> Messages);
}
