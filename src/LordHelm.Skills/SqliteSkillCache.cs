using System.Text.Json;
using LordHelm.Core;
using Microsoft.Data.Sqlite;

namespace LordHelm.Skills;

public interface ISkillCache
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<bool> HasHashAsync(string hash, CancellationToken ct = default);
    Task UpsertAsync(string filePath, SkillManifest manifest, CancellationToken ct = default);
    Task<SkillManifest?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<SkillManifest>> ListAsync(CancellationToken ct = default);
    Task RemoveByFilePathAsync(string filePath, CancellationToken ct = default);
}

public sealed class SqliteSkillCache : ISkillCache
{
    private readonly string _connectionString;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteSkillCache(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS skills (
                  content_hash TEXT PRIMARY KEY,
                  skill_id TEXT NOT NULL,
                  version TEXT NOT NULL,
                  file_path TEXT NOT NULL,
                  loaded_at INTEGER NOT NULL,
                  manifest_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_skills_id ON skills(skill_id);
                CREATE INDEX IF NOT EXISTS idx_skills_file ON skills(file_path);
            """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> HasHashAsync(string hash, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM skills WHERE content_hash = $h LIMIT 1;";
        cmd.Parameters.AddWithValue("$h", hash);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    public async Task UpsertAsync(string filePath, SkillManifest manifest, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM skills WHERE file_path = $p;";
        del.Parameters.AddWithValue("$p", filePath);
        await del.ExecuteNonQueryAsync(ct);

        var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO skills (content_hash, skill_id, version, file_path, loaded_at, manifest_json)
            VALUES ($h, $id, $v, $p, $t, $j);
        """;
        ins.Parameters.AddWithValue("$h", manifest.ContentHashSha256);
        ins.Parameters.AddWithValue("$id", manifest.Id);
        ins.Parameters.AddWithValue("$v", manifest.Version.ToString());
        ins.Parameters.AddWithValue("$p", filePath);
        ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        ins.Parameters.AddWithValue("$j", JsonSerializer.Serialize(manifest, JsonOptions));
        await ins.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task<SkillManifest?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT manifest_json FROM skills WHERE skill_id = $id ORDER BY loaded_at DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        var json = (string?)await cmd.ExecuteScalarAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<SkillManifest>(json, JsonOptions);
    }

    public async Task<IReadOnlyList<SkillManifest>> ListAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var list = new List<SkillManifest>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT manifest_json FROM skills ORDER BY skill_id;";
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var m = JsonSerializer.Deserialize<SkillManifest>(rdr.GetString(0), JsonOptions);
            if (m is not null) list.Add(m);
        }
        return list;
    }

    public async Task RemoveByFilePathAsync(string filePath, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM skills WHERE file_path = $p;";
        cmd.Parameters.AddWithValue("$p", filePath);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
    };
}
