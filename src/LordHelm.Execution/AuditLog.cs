using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LordHelm.Execution;

public sealed record AuditEntry(
    long Id,
    string PrevHashHex,
    string EntryHashHex,
    string SkillId,
    string RiskTier,
    string Decision,
    string OperatorId,
    string SessionId,
    string? Detail,
    DateTimeOffset At);

public interface IAuditLog
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<AuditEntry> AppendAsync(string skillId, string riskTier, string decision, string operatorId, string sessionId, string? detail, CancellationToken ct = default);
    Task<bool> VerifyChainAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> RecentAsync(int limit = 100, CancellationToken ct = default);
}

public sealed class SqliteAuditLog : IAuditLog
{
    private readonly string _cs;

    public SqliteAuditLog(string dbPath)
    {
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync(ct);
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS audit (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              prev_hash TEXT NOT NULL,
              entry_hash TEXT NOT NULL UNIQUE,
              skill_id TEXT NOT NULL,
              risk_tier TEXT NOT NULL,
              decision TEXT NOT NULL,
              operator_id TEXT NOT NULL,
              session_id TEXT NOT NULL,
              detail TEXT,
              at INTEGER NOT NULL
            );
        """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AuditEntry> AppendAsync(string skillId, string riskTier, string decision, string operatorId, string sessionId, string? detail, CancellationToken ct = default)
    {
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);

        var prevCmd = c.CreateCommand();
        prevCmd.Transaction = tx;
        prevCmd.CommandText = "SELECT entry_hash FROM audit ORDER BY id DESC LIMIT 1;";
        var prev = (string?)await prevCmd.ExecuteScalarAsync(ct) ?? new string('0', 64);

        var at = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new { prev, skillId, riskTier, decision, operatorId, sessionId, detail, at = at.ToUnixTimeMilliseconds() });
        var entryHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO audit (prev_hash, entry_hash, skill_id, risk_tier, decision, operator_id, session_id, detail, at)
            VALUES ($p, $e, $s, $r, $d, $o, $ss, $det, $at);
            SELECT last_insert_rowid();
        """;
        ins.Parameters.AddWithValue("$p", prev);
        ins.Parameters.AddWithValue("$e", entryHash);
        ins.Parameters.AddWithValue("$s", skillId);
        ins.Parameters.AddWithValue("$r", riskTier);
        ins.Parameters.AddWithValue("$d", decision);
        ins.Parameters.AddWithValue("$o", operatorId);
        ins.Parameters.AddWithValue("$ss", sessionId);
        ins.Parameters.AddWithValue("$det", (object?)detail ?? DBNull.Value);
        ins.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        var id = (long)(await ins.ExecuteScalarAsync(ct) ?? 0L);
        await tx.CommitAsync(ct);

        return new AuditEntry(id, prev, entryHash, skillId, riskTier, decision, operatorId, sessionId, detail, at);
    }

    public async Task<bool> VerifyChainAsync(CancellationToken ct = default)
    {
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync(ct);
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT prev_hash, entry_hash, skill_id, risk_tier, decision, operator_id, session_id, detail, at FROM audit ORDER BY id ASC;";
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var expected = new string('0', 64);
        while (await rdr.ReadAsync(ct))
        {
            var prev = rdr.GetString(0);
            if (prev != expected) return false;
            var entry = rdr.GetString(1);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(rdr.GetInt64(8));
            var payload = JsonSerializer.Serialize(new
            {
                prev,
                skillId = rdr.GetString(2),
                riskTier = rdr.GetString(3),
                decision = rdr.GetString(4),
                operatorId = rdr.GetString(5),
                sessionId = rdr.GetString(6),
                detail = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                at = at.ToUnixTimeMilliseconds(),
            });
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            if (hash != entry) return false;
            expected = entry;
        }
        return true;
    }

    public async Task<IReadOnlyList<AuditEntry>> RecentAsync(int limit = 100, CancellationToken ct = default)
    {
        var list = new List<AuditEntry>();
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync(ct);
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, prev_hash, entry_hash, skill_id, risk_tier, decision, operator_id, session_id, detail, at FROM audit ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new AuditEntry(
                rdr.GetInt64(0), rdr.GetString(1), rdr.GetString(2),
                rdr.GetString(3), rdr.GetString(4), rdr.GetString(5),
                rdr.GetString(6), rdr.GetString(7),
                rdr.IsDBNull(8) ? null : rdr.GetString(8),
                DateTimeOffset.FromUnixTimeMilliseconds(rdr.GetInt64(9))));
        }
        return list;
    }
}
