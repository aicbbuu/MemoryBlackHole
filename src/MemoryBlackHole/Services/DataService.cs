using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MemoryBlackHole.Models;

namespace MemoryBlackHole.Services
{
    /// <summary>
    /// 本地存储服务：SQLite 存元数据 + FTS5 全文索引。
    /// 数据全部落在 ~/.memoryblackhole/ 下的库文件与媒体目录，纯本地、无云端。
    /// v1.0.1: 改用 trigram 分词器优化中文搜索；移除 BLOB 存储（文件全走文件系统）；
    ///         常用字段加索引加速过滤查询。
    /// </summary>
    public class DataService
    {
        private readonly string _dbPath;
        private readonly string _mediaDir;
        private readonly string _connectionString;

        public DataService(string? dataDir = null)
        {
            // 默认存到用户目录，可被覆盖（便于测试）
            dataDir ??= Path.Combine(AppContext.BaseDirectory, ".memoryblackhole");
            Directory.CreateDirectory(dataDir);

            _dbPath = Path.Combine(dataDir, "memory.db");
            _mediaDir = Path.Combine(dataDir, "media");
            Directory.CreateDirectory(_mediaDir);

            _connectionString = $"Data Source={_dbPath}";
            Init();
        }

        public string MediaDirectory => _mediaDir;

        private void Init()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // 主表
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Items (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Type TEXT NOT NULL,
                        Title TEXT NULL,
                        Content TEXT NULL,
                        FilePath TEXT NULL,
                        FileData BLOB NULL,
                        OriginalFileName TEXT NULL,
                        FileSizeBytes INTEGER DEFAULT 0,
                        Note TEXT NULL,
                        Tags TEXT NULL,
                        CreatedAt TEXT NOT NULL,
                        IsFavorite INTEGER DEFAULT 0
                    );";
                cmd.ExecuteNonQuery();
            }
            // 兼容已存在的旧数据库
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE Items ADD COLUMN FileData BLOB NULL;";
                try { cmd.ExecuteNonQuery(); } catch (SqliteException) { }
            }

            // v1.0.1: FTS5 改用 trigram 分词器，对中文 CJK 连续词做 3-gram 索引，大幅提升匹配质量
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    DROP TABLE IF EXISTS ItemsFts;
                    CREATE VIRTUAL TABLE ItemsFts USING fts5(
                        Title, Content, Note, Tags, Content='Items', Content_Rowid='Id',
                        tokenize='trigram'
                    );";
                cmd.ExecuteNonQuery();
            }

            // 重建 trigram 索引（覆盖已有数据）
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO ItemsFts(ItemsFts) VALUES('rebuild');";
                try { cmd.ExecuteNonQuery(); } catch { /* 表刚建时已空 */ }
            }

            // v1.0.1: 常用字段加索引，加速过滤查询
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE INDEX IF NOT EXISTS IX_Items_Type ON Items(Type);
                    CREATE INDEX IF NOT EXISTS IX_Items_CreatedAt ON Items(CreatedAt);
                    CREATE INDEX IF NOT EXISTS IX_Items_Favorite ON Items(IsFavorite);";
                cmd.ExecuteNonQuery();
            }

            // Settings 表 + FTS 触发器（自动同步增删改到索引）
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Settings (
                        Key TEXT PRIMARY KEY,
                        Value TEXT NOT NULL
                    );
                    CREATE TRIGGER IF NOT EXISTS Items_AfterInsert AFTER INSERT ON Items BEGIN
                        INSERT INTO ItemsFts(rowid, Title, Content, Note, Tags)
                        VALUES (new.Id, new.Title, new.Content, new.Note, new.Tags);
                    END;
                    CREATE TRIGGER IF NOT EXISTS Items_AfterUpdate AFTER UPDATE ON Items BEGIN
                        INSERT INTO ItemsFts(ItemsFts, rowid, Title, Content, Note, Tags)
                        VALUES ('delete', old.Id, old.Title, old.Content, old.Note, old.Tags);
                        INSERT INTO ItemsFts(rowid, Title, Content, Note, Tags)
                        VALUES (new.Id, new.Title, new.Content, new.Note, new.Tags);
                    END;
                    CREATE TRIGGER IF NOT EXISTS Items_AfterDelete AFTER DELETE ON Items BEGIN
                        INSERT INTO ItemsFts(ItemsFts, rowid, Title, Content, Note, Tags)
                        VALUES ('delete', old.Id, old.Title, old.Content, old.Note, old.Tags);
                    END;";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>新增一条记忆，返回自增Id。
        /// v1.0.1: 不再接收 FileData BLOB，文件全走文件系统路径存储。</summary>
        public long Add(MemoryItem item)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Items(Type, Title, Content, FilePath,
                                  OriginalFileName, FileSizeBytes, Note, Tags, CreatedAt, IsFavorite)
                VALUES ($type, $title, $content, $file, $ofn, $fsize, $note, $tags, $created, $fav);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$type", item.Type);
            cmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$content", (object?)item.Content ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$file", (object?)item.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ofn", (object?)item.OriginalFileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fsize", item.FileSizeBytes);
            cmd.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tags", (object?)item.Tags ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$fav", item.IsFavorite ? 1 : 0);
            var id = (long)cmd.ExecuteScalar()!;
            return id;
        }

        /// <summary>按关键词全文搜索（FTS5），也支持过滤（类型/收藏）。</summary>
        public List<MemoryItem> Search(string keyword, string? type = null, bool? favorite = null)
        {
            var result = new List<MemoryItem>();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            if (string.IsNullOrWhiteSpace(keyword))
            {
                // 安全默认：没有搜索词时不暴露任何记忆
                return result;
            }

            string sql;
            using var cmd = conn.CreateCommand();

            if (string.IsNullOrWhiteSpace(keyword))
            {
                // 无关键词：按条件列出
                sql = "SELECT * FROM Items WHERE 1=1";
                if (!string.IsNullOrEmpty(type)) sql += " AND Type=$type";
                if (favorite == true) sql += " AND IsFavorite=1";
                sql += " ORDER BY CreatedAt DESC LIMIT 200";
                cmd.CommandText = sql;
                if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue("$type", type);
            }
            else
            {
                // 有关键词：FTS5 匹配 — v1.0.1 trigram 分词器对中文天然友好
                sql = @"SELECT i.* FROM ItemsFts f
                        JOIN Items i ON i.Id = f.Rowid
                        WHERE ItemsFts MATCH $kw";
                if (!string.IsNullOrEmpty(type)) sql += " AND i.Type=$type";
                if (favorite == true) sql += " AND i.IsFavorite=1";
                sql += " ORDER BY bm25(ItemsFts), i.CreatedAt DESC LIMIT 200";
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$kw", BuildMatchQuery(keyword));
                if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue("$type", type);
            }

            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                result.Add(new MemoryItem
                {
                    Id = rd.GetInt64(rd.GetOrdinal("Id")),
                    Type = rd.GetString(rd.GetOrdinal("Type")),
                    Title = rd.IsDBNull(rd.GetOrdinal("Title")) ? null : rd.GetString(rd.GetOrdinal("Title")),
                    Content = rd.IsDBNull(rd.GetOrdinal("Content")) ? null : rd.GetString(rd.GetOrdinal("Content")),
                    FilePath = rd.IsDBNull(rd.GetOrdinal("FilePath")) ? null : rd.GetString(rd.GetOrdinal("FilePath")),
                    FileData = rd.IsDBNull(rd.GetOrdinal("FileData")) ? null : (byte[])rd["FileData"],
                    OriginalFileName = rd.IsDBNull(rd.GetOrdinal("OriginalFileName")) ? null : rd.GetString(rd.GetOrdinal("OriginalFileName")),
                    FileSizeBytes = rd.GetInt64(rd.GetOrdinal("FileSizeBytes")),
                    Note = rd.IsDBNull(rd.GetOrdinal("Note")) ? null : rd.GetString(rd.GetOrdinal("Note")),
                    Tags = rd.IsDBNull(rd.GetOrdinal("Tags")) ? null : rd.GetString(rd.GetOrdinal("Tags")),
                    CreatedAt = DateTime.Parse(rd.GetString(rd.GetOrdinal("CreatedAt"))),
                    IsFavorite = rd.GetInt64(rd.GetOrdinal("IsFavorite")) == 1
                });
            }
            return result;
        }

        /// <summary>把用户关键词转成安全的 FTS5 match 表达式（空格分词，逐词 AND+前缀）。
        /// v1.0.1: trigram 分词器在字节级做 3-gram，对 CJK 中文自然友好，
        ///         搜"记忆"即匹配含"记忆"内容，无需额外逐字拆分。</summary>
        private static string BuildMatchQuery(string keyword)
        {
            var terms = keyword.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
            var parts = new List<string>();
            foreach (var t in terms)
            {
                var esc = t.Replace("\"", "\"\"");
                parts.Add($"\"{esc}\"*");
            }
            return string.Join(" AND ", parts);
        }

        public bool HasPassword()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Settings WHERE Key='password_hash'";
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        public void SetPassword(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120000, HashAlgorithmName.SHA256, 32);
            SaveSetting("password_salt", Convert.ToBase64String(salt));
            SaveSetting("password_hash", Convert.ToBase64String(hash));
        }

        public bool VerifyPassword(string password)
        {
            string? saltText = ReadSetting("password_salt");
            string? hashText = ReadSetting("password_hash");
            if (saltText == null || hashText == null) return false;
            byte[] salt = Convert.FromBase64String(saltText);
            byte[] expected = Convert.FromBase64String(hashText);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120000, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        public void Update(MemoryItem item)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Items SET Title=$title, Content=$content, Note=$note, Tags=$tags WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$content", (object?)item.Content ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tags", (object?)item.Tags ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private void SaveSetting(string key, string value)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=$value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }

        private string? ReadSetting(string key)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key=$key";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }

        /// <summary>复制媒体文件到媒体目录，返回落盘路径。超过 maxBytes 则返回 null（改存原始路径）。
        /// v1.0.1: maxBytes 从 200MB 缩为 100MB；改用 File.Copy 流式复制，杜绝 File.ReadAllBytes 内存暴涨。</summary>
        public string? StoreMedia(string sourcePath, long maxBytes = 100L * 1024 * 1024)
        {
            var info = new FileInfo(sourcePath);
            if (info.Length > maxBytes) return null; // 超限：不复制，落库时用原始路径
            var dest = Path.Combine(_mediaDir, $"media_{Guid.NewGuid():N}{info.Extension}");
            File.Copy(sourcePath, dest, true);
            return dest;
        }
    }
}