using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ESbirka.Client;

public sealed record ESbirkaCacheEntry(string Key, string PublicationNumber, string Kind, string? CanonicalPath,
    string Payload, DateTimeOffset FetchedAt, DateTimeOffset LastValidatedAt);

public sealed record ESbirkaCacheRecord(string Key, string Kind, string? CanonicalPath, long Bytes);
public sealed record CachePurgeResult(int Records, long Bytes, bool DryRun);

public interface IESbirkaCache
{
    Task<ESbirkaCacheEntry?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteBatchAsync(IReadOnlyList<ESbirkaCacheEntry> entries, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ESbirkaCacheRecord>> ListAsync(string publicationNumber, CancellationToken cancellationToken = default);
    Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default);
}

public interface IESbirkaCacheAdministration
{
    Task RevalidateCurrentAsync(string publicationNumber, CancellationToken cancellationToken = default);
    Task PurgeCurrentAliasAsync(string publicationNumber, CancellationToken cancellationToken = default);
    Task PurgeProvisionAsync(string publicationNumber, LawVersionSelector version, LegalProvisionSelector provision, CancellationToken cancellationToken = default);
    Task PurgeVersionAsync(string publicationNumber, DateOnly effectiveFrom, CancellationToken cancellationToken = default);
    Task<CachePurgeResult> PurgeDocumentAsync(string publicationNumber, bool includeHistoricalVersions,
        bool dryRun = false, CancellationToken cancellationToken = default);
}

public sealed class SqliteESbirkaCache : IESbirkaCache
{
    private readonly string _connectionString;
    private readonly long _maximumBytes;

    public SqliteESbirkaCache(ESbirkaClientOptions options)
    {
        options.Validate();
        var path = Path.GetFullPath(options.CachePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        _maximumBytes = options.MaximumCacheBytes;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS cache (
                key TEXT PRIMARY KEY, publication TEXT NOT NULL, kind TEXT NOT NULL,
                canonical TEXT, payload BLOB NOT NULL, hash TEXT NOT NULL,
                fetched TEXT NOT NULL, validated TEXT NOT NULL, accessed TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_cache_publication ON cache(publication, canonical);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<ESbirkaCacheEntry?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT publication,kind,canonical,payload,hash,fetched,validated FROM cache WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        ESbirkaCacheEntry? entry = null;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var bytes = (byte[])reader[3];
                if (Convert.ToHexString(SHA256.HashData(bytes)) != reader.GetString(4))
                    throw new ESbirkaException("cache_corrupt", "Cached payload failed its integrity check; purge the affected version.");
                using var compressed = new MemoryStream(bytes);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var text = new StreamReader(gzip, Encoding.UTF8);
                entry = new(key, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    await text.ReadToEndAsync(cancellationToken), DateTimeOffset.Parse(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6)));
            }
        }
        command.CommandText = "UPDATE cache SET accessed=$now WHERE key=$key";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        ESbirkaMetrics.CacheReads.Add(1, new KeyValuePair<string, object?>("status", entry is null ? "miss" : "hit"));
        return entry;
    }

    public async Task WriteBatchAsync(IReadOnlyList<ESbirkaCacheEntry> entries, CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes(entry.Payload), cancellationToken);
            var payload = buffer.ToArray();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO cache(key,publication,kind,canonical,payload,hash,fetched,validated,accessed)
                VALUES($key,$publication,$kind,$canonical,$payload,$hash,$fetched,$validated,$now)
                ON CONFLICT(key) DO UPDATE SET canonical=excluded.canonical,payload=excluded.payload,hash=excluded.hash,
                    fetched=excluded.fetched,validated=excluded.validated,accessed=excluded.accessed;
                """;
            command.Parameters.AddWithValue("$key", entry.Key);
            command.Parameters.AddWithValue("$publication", entry.PublicationNumber);
            command.Parameters.AddWithValue("$kind", entry.Kind);
            command.Parameters.AddWithValue("$canonical", (object?)entry.CanonicalPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(payload)));
            command.Parameters.AddWithValue("$fetched", entry.FetchedAt.ToString("O"));
            command.Parameters.AddWithValue("$validated", entry.LastValidatedAt.ToString("O"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        using var quota = connection.CreateCommand();
        quota.Transaction = transaction;
        quota.CommandText = "SELECT COALESCE(SUM(length(payload)),0) FROM cache";
        var size = Convert.ToInt64(await quota.ExecuteScalarAsync(cancellationToken));
        if (size > _maximumBytes)
        {
            var protectedKeys = entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            quota.CommandText = "SELECT key,length(payload) FROM cache WHERE kind='snapshot' AND canonical NOT IN (SELECT canonical FROM cache WHERE kind='current' AND canonical IS NOT NULL) ORDER BY accessed";
            var candidates = new List<(string Key, long Bytes)>();
            using (var reader = await quota.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) candidates.Add((reader.GetString(0), reader.GetInt64(1)));
            foreach (var candidate in candidates)
            {
                if (size <= _maximumBytes) break;
                if (protectedKeys.Contains(candidate.Key)) continue;
                quota.CommandText = "DELETE FROM cache WHERE key=$key";
                quota.Parameters.Clear();
                quota.Parameters.AddWithValue("$key", candidate.Key);
                await quota.ExecuteNonQueryAsync(cancellationToken);
                size -= candidate.Bytes;
            }
            if (size > _maximumBytes)
                throw new ESbirkaException("cache_full", "Cache quota is exhausted by active snapshots; increase the quota or purge unused documents.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ESbirkaCacheRecord>> ListAsync(string publicationNumber, CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key,kind,canonical,length(payload) FROM cache WHERE publication=$publication";
        command.Parameters.AddWithValue("$publication", publicationNumber);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<ESbirkaCacheRecord>();
        while (await reader.ReadAsync(cancellationToken))
            records.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3)));
        return records;
    }

    public async Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var key in keys)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM cache WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}