using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LordHelm.Scout;

public interface ICliSpecStore
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Upsert a new STM observation for (vendor, version). If the flag digest matches
    /// the previous STM row, increment its stability counter; on 3 stable cycles we
    /// auto-promote to LTM. Drift (different digest from the active LTM) archives
    /// the old LTM and starts a fresh STM cycle. Returns any mutations detected.
    /// </summary>
    Task<IReadOnlyList<MutationEvent>> RecordAsync(CliSpec spec, int stabilityThreshold = 3, CancellationToken ct = default);

    Task<CliSpec?> GetActiveAsync(string vendorId, CancellationToken ct = default);

    Task<IReadOnlyList<MutationEvent>> RecentMutationsAsync(int limit = 50, CancellationToken ct = default);
}

public sealed class SqliteCliSpecStore : ICliSpecStore
{
    private readonly string _cs;

    public SqliteCliSpecStore(string dbPath)
    {
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var c = new SqliteConnection(_cs);
            await c.OpenAsync(ct);
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS cli_specs (
                  vendor TEXT NOT NULL,
                  version TEXT NOT NULL,
                  flag_digest TEXT NOT NULL,
                  stability INTEGER NOT NULL,
                  lifecycle TEXT NOT NULL, -- 'stm' | 'ltm' | 'archived'
                  captured_at INTEGER NOT NULL,
                  spec_json TEXT NOT NULL,
                  PRIMARY KEY (vendor, version, flag_digest)
                );
                CREATE INDEX IF NOT EXISTS idx_cli_active ON cli_specs(vendor, lifecycle);
                CREATE TABLE IF NOT EXISTS cli_mutations (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  vendor TEXT NOT NULL,
                  from_version TEXT NOT NULL,
                  to_version TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  flag_name TEXT NOT NULL,
                  detail TEXT,
                  at INTEGER NOT NULL
                );
            """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<IReadOnlyList<MutationEvent>> RecordAsync(CliSpec spec, int stabilityThreshold = 3, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);

        var active = await GetActiveInternal(c, tx, spec.VendorId, ct);
        var mutations = new List<MutationEvent>();

        if (active is not null && active.FlagDigest == spec.FlagDigest)
        {
            var bump = c.CreateCommand();
            bump.Transaction = tx;
            bump.CommandText = """
                UPDATE cli_specs SET stability = stability + 1, captured_at = $t
                WHERE vendor = $v AND version = $ver AND flag_digest = $d AND lifecycle != 'archived';
            """;
            bump.Parameters.AddWithValue("$v", spec.VendorId);
            bump.Parameters.AddWithValue("$ver", active.Version);
            bump.Parameters.AddWithValue("$d", active.FlagDigest);
            bump.Parameters.AddWithValue("$t", spec.CapturedAt.ToUnixTimeMilliseconds());
            await bump.ExecuteNonQueryAsync(ct);

            var curStability = await ReadStability(c, tx, spec.VendorId, active.Version, active.FlagDigest, ct);
            if (curStability >= stabilityThreshold)
            {
                var promote = c.CreateCommand();
                promote.Transaction = tx;
                promote.CommandText = "UPDATE cli_specs SET lifecycle='ltm' WHERE vendor=$v AND lifecycle='stm';";
                promote.Parameters.AddWithValue("$v", spec.VendorId);
                await promote.ExecuteNonQueryAsync(ct);
                mutations.Add(new MutationEvent(spec.VendorId, active.Version, active.Version, MutationKind.Promoted, "*", $"stability={curStability}", spec.CapturedAt));
                await LogMutationsAsync(c, tx, mutations, ct);
            }
        }
        else
        {
            if (active is not null)
            {
                var diffs = Diff(active, spec);
                mutations.AddRange(diffs);
                var arch = c.CreateCommand();
                arch.Transaction = tx;
                arch.CommandText = "UPDATE cli_specs SET lifecycle='archived' WHERE vendor=$v AND lifecycle IN ('stm','ltm');";
                arch.Parameters.AddWithValue("$v", spec.VendorId);
                await arch.ExecuteNonQueryAsync(ct);
                mutations.Add(new MutationEvent(spec.VendorId, active.Version, spec.Version, MutationKind.Archived, "*", null, spec.CapturedAt));
            }

            var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR REPLACE INTO cli_specs (vendor, version, flag_digest, stability, lifecycle, captured_at, spec_json)
                VALUES ($v, $ver, $d, 1, 'stm', $t, $j);
            """;
            ins.Parameters.AddWithValue("$v", spec.VendorId);
            ins.Parameters.AddWithValue("$ver", spec.Version);
            ins.Parameters.AddWithValue("$d", spec.FlagDigest);
            ins.Parameters.AddWithValue("$t", spec.CapturedAt.ToUnixTimeMilliseconds());
            ins.Parameters.AddWithValue("$j", JsonSerializer.Serialize(spec));
            await ins.ExecuteNonQueryAsync(ct);

            await LogMutationsAsync(c, tx, mutations, ct);
        }

        await tx.CommitAsync(ct);
        return mutations;
    }

    public async Task<CliSpec?> GetActiveAsync(string vendorId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync(ct);
        return await GetActiveInternal(c, null, vendorId, ct);
    }

    public async Task<IReadOnlyList<MutationEvent>> RecentMutationsAsync(int limit = 50, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var list = new List<MutationEvent>();
        await using var c = new SqliteConnection(_cs);
        await c.OpenAsync(ct);
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT vendor, from_version, to_version, kind, flag_name, detail, at FROM cli_mutations ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new MutationEvent(
                rdr.GetString(0), rdr.GetString(1), rdr.GetString(2),
                Enum.Parse<MutationKind>(rdr.GetString(3)),
                rdr.GetString(4),
                rdr.IsDBNull(5) ? null : rdr.GetString(5),
                DateTimeOffset.FromUnixTimeMilliseconds(rdr.GetInt64(6))));
        }
        return list;
    }

    private static async Task<CliSpec?> GetActiveInternal(SqliteConnection c, SqliteTransaction? tx, string vendorId, CancellationToken ct)
    {
        var cmd = c.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = "SELECT spec_json FROM cli_specs WHERE vendor=$v AND lifecycle IN ('stm','ltm') ORDER BY lifecycle DESC, captured_at DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$v", vendorId);
        var json = (string?)await cmd.ExecuteScalarAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<CliSpec>(json);
    }

    private static async Task<int> ReadStability(SqliteConnection c, SqliteTransaction tx, string vendor, string version, string digest, CancellationToken ct)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT stability FROM cli_specs WHERE vendor=$v AND version=$ver AND flag_digest=$d;";
        cmd.Parameters.AddWithValue("$v", vendor);
        cmd.Parameters.AddWithValue("$ver", version);
        cmd.Parameters.AddWithValue("$d", digest);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task LogMutationsAsync(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<MutationEvent> mutations, CancellationToken ct)
    {
        foreach (var m in mutations)
        {
            var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO cli_mutations (vendor, from_version, to_version, kind, flag_name, detail, at)
                VALUES ($v, $fv, $tv, $k, $fn, $d, $at);
            """;
            cmd.Parameters.AddWithValue("$v", m.VendorId);
            cmd.Parameters.AddWithValue("$fv", m.FromVersion);
            cmd.Parameters.AddWithValue("$tv", m.ToVersion);
            cmd.Parameters.AddWithValue("$k", m.Kind.ToString());
            cmd.Parameters.AddWithValue("$fn", m.FlagName);
            cmd.Parameters.AddWithValue("$d", (object?)m.Detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", m.At.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static IEnumerable<MutationEvent> Diff(CliSpec old, CliSpec fresh)
    {
        var oldFlags = old.Flags.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var newFlags = fresh.Flags.ToDictionary(f => f.Name, StringComparer.Ordinal);

        foreach (var (name, flag) in newFlags)
        {
            if (!oldFlags.ContainsKey(name))
                yield return new MutationEvent(fresh.VendorId, old.Version, fresh.Version, MutationKind.Added, name, flag.Description, fresh.CapturedAt);
            else
            {
                var o = oldFlags[name];
                if (!string.Equals(o.Type, flag.Type, StringComparison.Ordinal))
                    yield return new MutationEvent(fresh.VendorId, old.Version, fresh.Version, MutationKind.ChangedType, name, $"{o.Type} -> {flag.Type}", fresh.CapturedAt);
                if (!string.Equals(o.Default, flag.Default, StringComparison.Ordinal))
                    yield return new MutationEvent(fresh.VendorId, old.Version, fresh.Version, MutationKind.ChangedDefault, name, $"{o.Default} -> {flag.Default}", fresh.CapturedAt);
            }
        }
        foreach (var (name, _) in oldFlags)
        {
            if (!newFlags.ContainsKey(name))
                yield return new MutationEvent(fresh.VendorId, old.Version, fresh.Version, MutationKind.Removed, name, null, fresh.CapturedAt);
        }
    }
}
