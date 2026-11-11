using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using SerialBridge.Core.Logging;

namespace SerialBridge.Core.History;

/// <summary>A session's persisted metadata. Traffic is counted at the TCP connection, not the serial writer.</summary>
public sealed record ClientHistoryEntry(
    Guid Id, Guid MappingId, string RemoteAddress, DateTime ConnectedAtUtc,
    DateTime? DisconnectedAtUtc, long? DurationMs, long SentBytes, long ReceivedBytes,
    string? DisconnectReason);

/// <summary>
/// Single ordered SQLite writer. Producers never do disk I/O on the TCP accept/receive threads.
/// Only successful connections are recorded. Each mapping retains its latest 1000 completed sessions.
/// </summary>
public sealed class ClientHistoryStore : IAsyncDisposable
{
    public const int MaxCompletedPerMapping = 1000;
    private readonly string _connectionString;
    private readonly Channel<WriteJob> _queue = Channel.CreateUnbounded<WriteJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _worker;
    private readonly object _enqueueGate = new();
    private readonly HashSet<Guid> _deleted = new();
    private bool _disposed;

    private sealed record WriteJob(Action<SqliteConnection> Action, TaskCompletionSource<bool>? Completion);

    public static ClientHistoryStore? Shared { get; private set; }

    public static void ConfigureShared(string databasePath)
    {
        if (Shared is not null) throw new InvalidOperationException("客户端历史数据库已初始化");
        Shared = new ClientHistoryStore(databasePath);
    }

    public ClientHistoryStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        // Initialize before accepting sessions. On unexpected shutdown, a still-open
        // session's actual end time is unknown and must not be invented.
        using (var db = Open())
        {
            using var create = db.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS ClientHistory (
                  Id TEXT PRIMARY KEY,
                  MappingId TEXT NOT NULL,
                  RemoteAddress TEXT NOT NULL,
                  ConnectedAtUtc INTEGER NOT NULL,
                  DisconnectedAtUtc INTEGER NULL,
                  DurationMs INTEGER NULL,
                  SentBytes INTEGER NOT NULL DEFAULT 0,
                  ReceivedBytes INTEGER NOT NULL DEFAULT 0,
                  DisconnectReason TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ClientHistory_Mapping_Time
                  ON ClientHistory(MappingId, ConnectedAtUtc DESC, Id DESC);
                UPDATE ClientHistory SET DisconnectReason='上次运行异常中断'
                  WHERE DisconnectedAtUtc IS NULL;
                """;
            create.ExecuteNonQuery();
        }
        _worker = Task.Run(WriterLoopAsync);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    public void RecordConnected(Guid mappingId, Guid connectionId, string remote, DateTime connectedAtUtc)
    {
        Enqueue(mappingId, db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO ClientHistory(Id,MappingId,RemoteAddress,ConnectedAtUtc)
                VALUES($id,$mapping,$remote,$connected);
                """;
            cmd.Parameters.AddWithValue("$id", connectionId.ToString("D"));
            cmd.Parameters.AddWithValue("$mapping", mappingId.ToString("D"));
            cmd.Parameters.AddWithValue("$remote", remote);
            cmd.Parameters.AddWithValue("$connected", new DateTimeOffset(connectedAtUtc).ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        });
    }

    public void RecordClosed(Guid mappingId, Guid connectionId, DateTime connectedAtUtc,
        DateTime disconnectedAtUtc, long sentBytes, long receivedBytes, string reason)
    {
        Enqueue(mappingId, db =>
        {
            using var transaction = db.BeginTransaction();
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    UPDATE ClientHistory
                    SET DisconnectedAtUtc=$ended, DurationMs=$duration,
                        SentBytes=$sent, ReceivedBytes=$received, DisconnectReason=$reason
                    WHERE Id=$id AND MappingId=$mapping AND DisconnectedAtUtc IS NULL;
                    """;
                cmd.Parameters.AddWithValue("$ended", new DateTimeOffset(disconnectedAtUtc).ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$duration", Math.Max(0L, (long)(disconnectedAtUtc - connectedAtUtc).TotalMilliseconds));
                cmd.Parameters.AddWithValue("$sent", sentBytes);
                cmd.Parameters.AddWithValue("$received", receivedBytes);
                cmd.Parameters.AddWithValue("$reason", reason);
                cmd.Parameters.AddWithValue("$id", connectionId.ToString("D"));
                cmd.Parameters.AddWithValue("$mapping", mappingId.ToString("D"));
                cmd.ExecuteNonQuery();
            }
            using (var prune = db.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText = """
                    DELETE FROM ClientHistory
                    WHERE MappingId=$mapping AND DisconnectedAtUtc IS NOT NULL
                      AND Id NOT IN (
                        SELECT Id FROM ClientHistory
                        WHERE MappingId=$mapping AND DisconnectedAtUtc IS NOT NULL
                        ORDER BY ConnectedAtUtc DESC, Id DESC LIMIT $limit
                      );
                    """;
                prune.Parameters.AddWithValue("$mapping", mappingId.ToString("D"));
                prune.Parameters.AddWithValue("$limit", MaxCompletedPerMapping);
                prune.ExecuteNonQuery();
            }
            transaction.Commit();
        });
    }

    private void Enqueue(Guid mappingId, Action<SqliteConnection> action)
    {
        lock (_enqueueGate)
        {
            if (_disposed || _deleted.Contains(mappingId)) return;
            if (!_queue.Writer.TryWrite(new WriteJob(action, null)))
                LogX.Error("客户端历史数据库写入队列已停止");
        }
    }

    public async Task DeleteMappingAsync(Guid mappingId)
    {
        Task completion;
        lock (_enqueueGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientHistoryStore));
            _deleted.Add(mappingId); // Reject late events while delete is queued.
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_queue.Writer.TryWrite(new WriteJob(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "DELETE FROM ClientHistory WHERE MappingId=$mapping";
                cmd.Parameters.AddWithValue("$mapping", mappingId.ToString("D"));
                cmd.ExecuteNonQuery();
            }, signal))) throw new InvalidOperationException("客户端历史数据库写入队列已关闭");
            completion = signal.Task;
        }
        await completion.ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClientHistoryEntry>> GetHistoryAsync(Guid mappingId)
    {
        await FlushAsync().ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var result = new List<ClientHistoryEntry>();
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT Id,MappingId,RemoteAddress,ConnectedAtUtc,DisconnectedAtUtc,
                       DurationMs,SentBytes,ReceivedBytes,DisconnectReason
                FROM ClientHistory WHERE MappingId=$mapping
                ORDER BY ConnectedAtUtc DESC, Id DESC LIMIT 1000;
                """;
            cmd.Parameters.AddWithValue("$mapping", mappingId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(new ClientHistoryEntry(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).UtcDateTime,
                    reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).UtcDateTime,
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.GetInt64(6), reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            return (IReadOnlyList<ClientHistoryEntry>)result;
        }).ConfigureAwait(false);
    }

    public async Task FlushAsync()
    {
        Task completion;
        lock (_enqueueGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientHistoryStore));
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_queue.Writer.TryWrite(new WriteJob(_ => { }, signal)))
                throw new InvalidOperationException("客户端历史数据库写入队列已关闭");
            completion = signal.Task;
        }
        await completion.ConfigureAwait(false);
    }

    private async Task WriterLoopAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                using var db = Open();
                job.Action(db);
                job.Completion?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                LogX.Error("客户端历史数据库写入失败：" + ex.Message, ex);
                job.Completion?.TrySetException(ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_enqueueGate)
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Writer.TryComplete();
        }
        await _worker.ConfigureAwait(false);
        if (ReferenceEquals(Shared, this)) Shared = null;
    }
}
