// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Microsoft.Data.Sqlite;

namespace Eztools.ClipboardLib;

/// <summary>
/// 剪贴板历史库（W5-剪贴板-设计方案.md §3.4）：SQLite 单文件 + FTS5 trigram。
///
/// <para><b>搜索双路（R2 对冲）</b>：trigram 分词对 &lt;3 字符是硬盲区（两字中文词 FTS 查不到），
/// 查询 ≥3 字符走 FTS MATCH（子串语义），&lt;3 字符走 LIKE 兜底——这不是可选优化，
/// 是 trigram 的硬限制对冲，selftest 三档断言锁死。</para>
///
/// <para><b>并发模型</b>：进程内单连接 + WAL。W5-a 的调用方是 CLI（一次性进程）；
/// W5-c 接入 Desktop 后监听与面板共用同一实例，靠 <see cref="_gate"/> 串行；
/// CLI 与 Desktop 跨进程并发靠 WAL 允许读写并存，不依赖长事务。</para>
///
/// <para><b>清理守恒</b>：上限裁剪只删未 Pin 的最旧条目（FR-4）；删除图片条目时
/// 同步删盘上 PNG（R8 孤儿文件对账的写入侧防线）。</para>
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const int DefaultMaxItems = 1000;
    public const int PreviewLimit = CaptureService.PreviewLength;
    private const string SchemaVersion = "1";
    private static readonly string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly SqliteConnection _conn;
    private readonly string? _imagesDir;
    private readonly object _gate = new();

    public string DbPath { get; }

    /// <summary>图片文件目录（未配置 = null；删除图片条目时按它回收文件）。</summary>
    public string? ImagesDirectory => _imagesDir;

    public HistoryStore(string dbPath, string? imagesDir = null)
    {
        DbPath = Path.GetFullPath(dbPath);
        _imagesDir = imagesDir is null ? null : Path.GetFullPath(imagesDir);

        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        if (_imagesDir is not null)
        {
            Directory.CreateDirectory(_imagesDir);
        }

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        _conn.Open();

        // WAL：跨进程读写并存的前提（CLI 查询时 Desktop 正在写入的场景，W5-c 起）。
        Execute("PRAGMA journal_mode=WAL");
        EnsureSchema();
    }

    // ────────────────────────────────────────────── schema

    private void EnsureSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS meta (
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS clips (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              kind         INTEGER NOT NULL,
              content      TEXT,
              preview      TEXT NOT NULL,
              image_path   TEXT,
              image_bytes  INTEGER NOT NULL DEFAULT 0,
              hash         TEXT NOT NULL,
              source_app   TEXT,
              pinned       INTEGER NOT NULL DEFAULT 0,
              copy_count   INTEGER NOT NULL DEFAULT 1,
              created_at   TEXT NOT NULL,
              last_used_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_clips_used ON clips(pinned DESC, last_used_at DESC);
            """);

        // FTS5（外部内容表 + 三触发器）。SQLite 编译不含 FTS5 时这里会抛——
        // 这是必须显式失败的环境问题，禁静默降级（S2 红线）。
        Execute("""
            CREATE VIRTUAL TABLE IF NOT EXISTS clips_fts USING fts5(
              content, content='clips', content_rowid='id', tokenize='trigram'
            );
            CREATE TRIGGER IF NOT EXISTS clips_ai AFTER INSERT ON clips BEGIN
              INSERT INTO clips_fts(rowid, content) VALUES (new.id, COALESCE(new.content, ''));
            END;
            CREATE TRIGGER IF NOT EXISTS clips_ad AFTER DELETE ON clips BEGIN
              INSERT INTO clips_fts(clips_fts, rowid, content)
                VALUES ('delete', old.id, COALESCE(old.content, ''));
            END;
            CREATE TRIGGER IF NOT EXISTS clips_au AFTER UPDATE ON clips BEGIN
              INSERT INTO clips_fts(clips_fts, rowid, content)
                VALUES ('delete', old.id, COALESCE(old.content, ''));
              INSERT INTO clips_fts(rowid, content) VALUES (new.id, COALESCE(new.content, ''));
            END;
            """);

        var current = QueryScalarString("SELECT value FROM meta WHERE key='schema_version'");
        if (current is null)
        {
            Execute("INSERT INTO meta(key, value) VALUES ('schema_version', $v)", ("$v", SchemaVersion));
        }
        else if (current != SchemaVersion)
        {
            // 未来加列时的显式迁移出口；现在任何不一致都说明库被外来程序动过。
            throw new InvalidOperationException(
                $"剪贴板库 schema 版本不匹配：库={current}，程序={SchemaVersion}（路径 {DbPath}）");
        }
    }

    // ────────────────────────────────────────────── 写路径

    public sealed record UpsertResult(ClipEntry Entry, bool IsNew, IReadOnlyList<string> DeletedImageFiles);

    /// <summary>
    /// 入库（FR-1/FR-3/FR-4）：命中 hash → 置顶到"最近使用"并 <c>copy_count+1</c>；
    /// 未命中 → 插入并裁剪超限最旧（pinned 豁免）。
    /// </summary>
    public UpsertResult Upsert(ClipEntry draft, int? maxItems = null)
    {
        if (string.IsNullOrWhiteSpace(draft.Hash))
        {
            throw new ArgumentException("draft.Hash 为空：调用方必须先经 CaptureService 计算指纹", nameof(draft));
        }

        var limit = maxItems ?? DefaultMaxItems;
        var now = DateTime.Now.ToString(TimestampFormat);

        lock (_gate)
        {
            // 原生 BEGIN/COMMIT（不用 ADO SqliteTransaction 对象——它要求每个命令显式
            // 关联事务，会传染到全部内部助手；SQLite 同连接 BEGIN 后语句天然在事务内）。
            Execute("BEGIN IMMEDIATE");
            long id;
            bool isNew;
            var deletedImages = new List<string>();
            try
            {
                var existingId = QueryScalarLong(
                    "SELECT id FROM clips WHERE hash = $h LIMIT 1", ("$h", draft.Hash));

                if (existingId is { } found)
                {
                    isNew = false;
                    Execute(
                        """
                        UPDATE clips
                           SET copy_count = copy_count + 1,
                               last_used_at = $now,
                               source_app = COALESCE($src, source_app)
                         WHERE id = $id
                        """,
                        ("$now", now), ("$src", (string?)draft.SourceApp), ("$id", found));
                    id = found;
                }
                else
                {
                    isNew = true;
                    id = ExecuteScalarLong(
                        """
                        INSERT INTO clips(kind, content, preview, image_path, image_bytes,
                                          hash, source_app, pinned, copy_count, created_at, last_used_at)
                        VALUES ($kind, $content, $preview, $imagePath, $imageBytes,
                                $hash, $src, $pinned, 1, $now, $now);
                        SELECT last_insert_rowid();
                        """,
                        ("$kind", (long)draft.Kind), ("$content", draft.Content), ("$preview", draft.Preview),
                        ("$imagePath", draft.ImagePath), ("$imageBytes", draft.ImageBytes),
                        ("$hash", draft.Hash), ("$src", draft.SourceApp),
                        ("$pinned", draft.Pinned ? 1L : 0L), ("$now", now));
                }

                deletedImages = TrimToLimit(limit);
                Execute("COMMIT");
            }
            catch
            {
                Execute("ROLLBACK");
                throw;
            }

            var entry = GetById(id)!;
            return new UpsertResult(entry, isNew, deletedImages);
        }
    }

    /// <summary>裁到上限（pinned 豁免），返回被删条目的图片相对路径（文件随删，R8）。</summary>
    private List<string> TrimToLimit(int limit)
    {
        var deleted = new List<string>();
        var total = QueryScalarLong("SELECT COUNT(*) FROM clips") ?? 0;
        var excess = total - limit;
        if (excess <= 0)
        {
            return deleted;
        }

        var victims = Query(
            """
            SELECT id, image_path FROM clips
             WHERE pinned = 0
             ORDER BY last_used_at ASC, id ASC
             LIMIT $n
            """,
            read => (read.GetInt64(0), read.IsDBNull(1) ? null : read.GetString(1)),
            ("$n", excess));

        foreach (var (id, imagePath) in victims)
        {
            Execute("DELETE FROM clips WHERE id = $id", ("$id", id));
            deleted.Add(DeleteImageFile(imagePath));
        }

        return deleted;
    }

    /// <summary>FR-4：图片保留期清理（kind=Image 且非 pinned 且 created_at 早于截止）。返回删除数。</summary>
    public int PurgeExpiredImages(int retentionDays)
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays).ToString(TimestampFormat);
        lock (_gate)
        {
            var victims = Query(
                "SELECT id, image_path FROM clips WHERE kind = 1 AND pinned = 0 AND created_at < $c",
                read => (read.GetInt64(0), read.IsDBNull(1) ? null : read.GetString(1)),
                ("$c", cutoff));

            foreach (var (id, imagePath) in victims)
            {
                Execute("DELETE FROM clips WHERE id = $id", ("$id", id));
                DeleteImageFile(imagePath);
            }

            return victims.Count;
        }
    }

    /// <summary>单条删除（FR-9）。返回是否真删了（id 不存在 = false）。</summary>
    public bool Delete(long id)
    {
        lock (_gate)
        {
            var imagePath = QueryScalarString(
                "SELECT image_path FROM clips WHERE id = $id", ("$id", id));
            if (imagePath is null && QueryScalarLong("SELECT COUNT(*) FROM clips WHERE id = $id", ("$id", id)) == 0)
            {
                return false;
            }

            Execute("DELETE FROM clips WHERE id = $id", ("$id", id));
            DeleteImageFile(imagePath);
            return true;
        }
    }

    /// <summary>一键清空（FR-9）。keepPinned=true 时 Pin 条目豁免。返回删除数。</summary>
    public int Clear(bool keepPinned)
    {
        lock (_gate)
        {
            var where = keepPinned ? " WHERE pinned = 0" : "";
            var victims = Query(
                "SELECT id, image_path FROM clips" + where,
                read => (read.GetInt64(0), read.IsDBNull(1) ? null : read.GetString(1)));

            Execute("DELETE FROM clips" + where);
            foreach (var (_, imagePath) in victims)
            {
                DeleteImageFile(imagePath);
            }

            return victims.Count;
        }
    }

    /// <summary>Pin / Unpin（FR-8）。id 不存在返回 false。</summary>
    public bool SetPinned(long id, bool pinned)
    {
        lock (_gate)
        {
            return ExecuteAffected(
                "UPDATE clips SET pinned = $p WHERE id = $id",
                ("$p", pinned ? 1L : 0L), ("$id", id)) > 0;
        }
    }

    // ────────────────────────────────────────────── 读路径

    /// <summary>列表（FR-6）：pinned 置顶 + 最近使用优先，可按类型过滤。</summary>
    public List<ClipEntry> List(ClipKind? kind = null, int limit = 50)
    {
        lock (_gate)
        {
            return kind is { } k
                ? Query(
                    SelectColumns + " FROM clips WHERE kind = $k ORDER BY pinned DESC, last_used_at DESC, id DESC LIMIT $n",
                    MapEntry, ("$k", (long)k), ("$n", limit))
                : Query(
                    SelectColumns + " FROM clips ORDER BY pinned DESC, last_used_at DESC, id DESC LIMIT $n",
                    MapEntry, ("$n", limit));
        }
    }

    public ClipEntry? GetById(long id)
    {
        lock (_gate)
        {
            return Query(SelectColumns + " FROM clips WHERE id = $id", MapEntry, ("$id", id))
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// 搜索（FR-7）：≥3 字符走 FTS trigram（子串语义），&lt;3 字符走 LIKE 兜底。
    /// 空查询 = 退化为 List。
    /// </summary>
    public List<ClipEntry> Search(string? query, int limit = 50)
    {
        var trimmed = query?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return List(limit: limit);
        }

        lock (_gate)
        {
            if (trimmed.Length >= 3)
            {
                // trigram 的 MATCH 子串语义：引号包住整个查询防 FTS 语法注入，内部引号双写。
                var match = "\"" + trimmed.Replace("\"", "\"\"") + "\"";
                var hits = Query(
                    """
                    SELECT c.id, c.kind, c.content, c.preview, c.image_path, c.image_bytes,
                           c.hash, c.source_app, c.pinned, c.copy_count, c.created_at, c.last_used_at
                    FROM clips_fts f
                    JOIN clips c ON c.id = f.rowid
                    WHERE clips_fts MATCH $q
                    ORDER BY c.pinned DESC, c.last_used_at DESC, c.id DESC
                    LIMIT $n
                    """,
                    MapEntry, ("$q", match), ("$n", limit));
                if (hits.Count > 0)
                {
                    return hits;
                }

                // FTS 空结果不是"该走 LIKE"的信号（真没命中很常见），直接如实返回。
                return hits;
            }

            var like = "%" + trimmed.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            return Query(
                """
                SELECT id, kind, content, preview, image_path, image_bytes, hash,
                       source_app, pinned, copy_count, created_at, last_used_at
                FROM clips
                WHERE content LIKE $like ESCAPE '\'
                ORDER BY pinned DESC, last_used_at DESC, id DESC
                LIMIT $n
                """,
                MapEntry, ("$like", like), ("$n", limit));
        }
    }

    public sealed record StoreStatus(
        int Total, int TextCount, int ImageCount, int FileListCount, int Pinned,
        long DbBytes, int ImageFiles, long ImageBytes, string? Oldest, string? Newest);

    public StoreStatus Status()
    {
        lock (_gate)
        {
            var total = QueryScalarLong("SELECT COUNT(*) FROM clips") ?? 0;
            var text = QueryScalarLong("SELECT COUNT(*) FROM clips WHERE kind = 0") ?? 0;
            var image = QueryScalarLong("SELECT COUNT(*) FROM clips WHERE kind = 1") ?? 0;
            var files = QueryScalarLong("SELECT COUNT(*) FROM clips WHERE kind = 2") ?? 0;
            var pinned = QueryScalarLong("SELECT COUNT(*) FROM clips WHERE pinned = 1") ?? 0;
            var oldest = QueryScalarString("SELECT MIN(created_at) FROM clips");
            var newest = QueryScalarString("SELECT MAX(last_used_at) FROM clips");

            var dbBytes = 0L;
            if (File.Exists(DbPath))
            {
                dbBytes = new FileInfo(DbPath).Length;
            }

            var imageFiles = 0;
            var imageBytes = 0L;
            if (_imagesDir is not null && Directory.Exists(_imagesDir))
            {
                foreach (var f in Directory.EnumerateFiles(_imagesDir))
                {
                    imageFiles++;
                    imageBytes += new FileInfo(f).Length;
                }
            }

            return new StoreStatus((int)total, (int)text, (int)image, (int)files, (int)pinned,
                dbBytes, imageFiles, imageBytes, oldest, newest);
        }
    }

    // ────────────────────────────────────────────── 图片文件

    /// <summary>删除图片文件（R8）。返回实际删掉的相对路径（没配 imagesDir / 文件不在 = 空串）。</summary>
    private string DeleteImageFile(string? relativePath)
    {
        if (relativePath is null || _imagesDir is null)
        {
            return relativePath ?? "";
        }

        // 相对路径里混入 ".." 一律拒绝——这个值将来可能来自库里，防目录穿越。
        var candidate = Path.GetFullPath(Path.Combine(_imagesDir, relativePath));
        if (!candidate.StartsWith(_imagesDir, StringComparison.OrdinalIgnoreCase))
        {
            return relativePath;
        }

        try
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
        catch (IOException)
        {
            // 文件被占用：留着给启动对账（W5-d）兜底，不让删除链路失败。
        }

        return relativePath;
    }

    // ────────────────────────────────────────────── ADO 助手

    private static ClipEntry MapEntry(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Kind = (ClipKind)r.GetInt64(1),
        Content = r.IsDBNull(2) ? null : r.GetString(2),
        Preview = r.GetString(3),
        ImagePath = r.IsDBNull(4) ? null : r.GetString(4),
        ImageBytes = r.GetInt64(5),
        Hash = r.GetString(6),
        SourceApp = r.IsDBNull(7) ? null : r.GetString(7),
        Pinned = r.GetInt64(8) != 0,
        CopyCount = (int)r.GetInt64(9),
        CreatedAt = DateTime.Parse(r.GetString(10)),
        LastUsedAt = DateTime.Parse(r.GetString(11)),
    };

    private static readonly string SelectColumns =
        "SELECT id, kind, content, preview, image_path, image_bytes, hash, source_app, pinned, copy_count, created_at, last_used_at";

    private void Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>执行并返回受影响行数（UPDATE/DELETE 的"是否真删了"判据）。</summary>
    private int ExecuteAffected(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd.ExecuteNonQuery();
    }

    /// <summary>执行并取首行首列的 long（INSERT...RETURNING last_insert_rowid() 一类）。</summary>
    private long ExecuteScalarLong(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>标量 long：行不存在 / NULL → null（调用方自行决定默认值——"空"与"0"不可混同）。</summary>
    private long? QueryScalarLong(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = cmd.ExecuteScalar();
        return result is null || result is DBNull ? null : Convert.ToInt64(result);
    }

    private string? QueryScalarString(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = cmd.ExecuteScalar();
        return result is null || result is DBNull ? null : (string)result;
    }

    private List<T> Query<T>(
        string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        var rows = new List<T>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _conn.Dispose();
        }
    }
}
