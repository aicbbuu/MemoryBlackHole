using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MemoryBlackHole.Models;

namespace MemoryBlackHole.Services
{
    /// <summary>
    /// 本地存储服务：SQLite 存元数据 + FTS5 全文索引。
    /// 数据全部落在 EXE 同目录 .memoryblackhole/ 下，纯本地、无云端。
    /// v2.0.1: 文件数据全部存入 SQLite BLOB；中文搜索 LIKE；回收站+导出导入。
    /// </summary>
    public class DataService
    {
        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly string _fileStoreDir;

        public DataService(string? dataDir = null)
        {
            // 默认存到 EXE 同目录下 .memoryblackhole/
            // Environment.ProcessPath 在单文件发布时返回真实 EXE 路径
            dataDir ??= Path.Combine(
                Path.GetDirectoryName(
                    Environment.ProcessPath ?? AppContext.BaseDirectory)!,
                ".memoryblackhole");
            Directory.CreateDirectory(dataDir);

            _dbPath = Path.Combine(dataDir, "memory.db");
            _fileStoreDir = Path.Combine(dataDir, "files");
            Directory.CreateDirectory(_fileStoreDir);

            _connectionString = $"Data Source={_dbPath}";
            Init();
        }

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
            // v2.0.0: 回收站列
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE Items ADD COLUMN IsDeleted INTEGER DEFAULT 0;";
                try { cmd.ExecuteNonQuery(); } catch (SqliteException) { }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE Items ADD COLUMN DeletedAt TEXT NULL;";
                try { cmd.ExecuteNonQuery(); } catch (SqliteException) { }
            }

            // v1.0.1: FTS5 改用 unicode61 分词器，CJK 每字为独立 token，配合逐字 AND 查询精准匹配
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    DROP TABLE IF EXISTS ItemsFts;
                    CREATE VIRTUAL TABLE ItemsFts USING fts5(
                        Title, Content, Note, Tags, Content='Items', Content_Rowid='Id',
                        tokenize='unicode61'
                    );";
                cmd.ExecuteNonQuery();
            }

            // 重建 unicode61 索引（覆盖已有数据）
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

        /// <summary>新增一条记忆，返回自增Id。文件数据直接存入 SQLite BLOB。</summary>
        public long Add(MemoryItem item)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Items(Type, Title, Content, FilePath, FileData,
                                  OriginalFileName, FileSizeBytes, Note, Tags, CreatedAt, IsFavorite)
                VALUES ($type, $title, $content, $file, $fdata, $ofn, $fsize, $note, $tags, $created, $fav);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$type", item.Type);
            cmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$content", (object?)item.Content ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$file", (object?)item.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fdata", (object?)item.FileData ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ofn", (object?)item.OriginalFileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fsize", item.FileSizeBytes);
            cmd.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tags", (object?)item.Tags ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$fav", item.IsFavorite ? 1 : 0);
            var id = (long)cmd.ExecuteScalar()!;
            return id;
        }

        /// <summary>
        /// 新增一条文件类记忆（非文本/链接）。
        /// 文件复制到 .memoryblackhole/files/ 目录，数据库只存文件路径（避免 OOM）。
        /// </summary>
        public void AddFile(MemoryItem item, string sourcePath)
        {
            var fileInfo = new FileInfo(sourcePath);
            item.FileSizeBytes = fileInfo.Length;

            // 复制到外部目录
            var ext = Path.GetExtension(sourcePath);
            var fileName = $"{Guid.NewGuid():N}{ext}";
            var destPath = Path.Combine(_fileStoreDir, fileName);
            File.Copy(sourcePath, destPath, overwrite: false);

            item.FilePath = destPath;
            item.FileData = null;

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Items(Type, Title, Content, FilePath, FileData,
                                  OriginalFileName, FileSizeBytes, Note, Tags, CreatedAt, IsFavorite)
                VALUES ($type, $title, $content, $file, NULL,
                        $ofn, $fsize, $note, $tags, $created, $fav);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$type", item.Type);
            cmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$content", (object?)item.Content ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$file", destPath);
            cmd.Parameters.AddWithValue("$ofn", (object?)item.OriginalFileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fsize", item.FileSizeBytes);
            cmd.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tags", (object?)item.Tags ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$fav", item.IsFavorite ? 1 : 0);
            item.Id = (long)cmd.ExecuteScalar()!;
        }

        /// <summary>按关键词全文搜索。中文/CJK 用 LIKE 模糊匹配（字节级 100% 可靠）；
        /// 纯英文/数字用 FTS5 前缀匹配；无关键词按条件列出。
        /// 支持按标签过滤（逗号分隔，不区分大小写）。</summary>
        public List<MemoryItem> Search(string keyword, string? type = null, bool? favorite = null, string? tag = null)
        {
            var result = new List<MemoryItem>();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

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
            else if (ContainsCJK(keyword))
            {
                // 含中文/CJK：LIKE 模糊匹配 — 字节级，100% 可靠
                // 每个空格分隔的 term 逐词 AND，如"会议记录" → LIKE '%会议记录%'
                var terms = keyword.Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                var likeClauses = new List<string>();
                int paramIdx = 0;
                foreach (var t in terms)
                {
                    string p = $"$kw{paramIdx}";
                    likeClauses.Add($"(i.Title LIKE {p} OR i.Content LIKE {p} OR i.Note LIKE {p} OR i.Tags LIKE {p})");
                    cmd.Parameters.AddWithValue(p, $"%{t}%");
                    paramIdx++;
                }
                sql = $"SELECT DISTINCT i.* FROM Items i WHERE {string.Join(" AND ", likeClauses)}";
                if (!string.IsNullOrEmpty(type)) sql += " AND i.Type=$type";
                if (favorite == true) sql += " AND i.IsFavorite=1";
                sql += " ORDER BY i.CreatedAt DESC LIMIT 200";
                cmd.CommandText = sql;
                if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue("$type", type);
            }
            else
            {
                // 纯英文/数字：FTS5 前缀匹配（高效）
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
                    FileData = rd.IsDBNull(rd.GetOrdinal("FileData")) ? null :
                        rd.GetInt64(rd.GetOrdinal("FileSizeBytes")) <= 50L * 1024 * 1024 ? rd.GetFieldValue<byte[]>(rd.GetOrdinal("FileData")) : null,
                    OriginalFileName = rd.IsDBNull(rd.GetOrdinal("OriginalFileName")) ? null : rd.GetString(rd.GetOrdinal("OriginalFileName")),
                    FileSizeBytes = rd.GetInt64(rd.GetOrdinal("FileSizeBytes")),
                    Note = rd.IsDBNull(rd.GetOrdinal("Note")) ? null : rd.GetString(rd.GetOrdinal("Note")),
                    Tags = rd.IsDBNull(rd.GetOrdinal("Tags")) ? null : rd.GetString(rd.GetOrdinal("Tags")),
                    CreatedAt = DateTime.Parse(rd.GetString(rd.GetOrdinal("CreatedAt"))),
                    IsFavorite = rd.GetInt64(rd.GetOrdinal("IsFavorite")) == 1
                });
            }

            // 按标签过滤（内存过滤，标签是逗号分隔文本）
            if (!string.IsNullOrEmpty(tag) && result.Count > 0)
            {
                var tagLower = tag.Trim().ToLowerInvariant();
                result = result.Where(item =>
                    !string.IsNullOrEmpty(item.Tags) &&
                    item.Tags.Split(new[] { ',', '，' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Any(t => t.ToLowerInvariant() == tagLower)
                ).ToList();
            }

            return result;
        }

        /// <summary>把英文/数字关键词转成 FTS5 前缀匹配表达式（空格分词，逐词 AND+前缀）。</summary>
                private static string BuildMatchQuery(string keyword)
                {
                    var terms = keyword.Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                    var parts = new List<string>();
                    foreach (var t in terms)
                    {
                        var esc = t.Replace("\"", "\"\"");
                        parts.Add($"\"{esc}\"*");
                    }
                    return string.Join(" AND ", parts);
                }

                /// <summary>检查字符串是否包含 CJK 字符。</summary>
                private static bool ContainsCJK(string text)
                {
                    foreach (char c in text)
                        if (IsCJK(c)) return true;
                    return false;
                }

                /// <summary>判断字符是否为中日韩统一表意文字。</summary>
                private static bool IsCJK(char c)
                {
                    return (c >= 0x4E00 && c <= 0x9FFF) ||
                           (c >= 0x3400 && c <= 0x4DBF);
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

        /// <summary>永久删除一条记忆（连带清理外部文件）。</summary>
        public void Delete(long id)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // 先查询外部文件路径（仅当是外部存储时）
            string? externalPath = null;
            using (var query = conn.CreateCommand())
            {
                query.CommandText = "SELECT FilePath FROM Items WHERE Id=$id AND FileData IS NULL AND FilePath IS NOT NULL";
                query.Parameters.AddWithValue("$id", id);
                externalPath = query.ExecuteScalar() as string;
            }

            // 删除记录
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM Items WHERE Id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }

            // 清理外部文件
            if (!string.IsNullOrEmpty(externalPath) && File.Exists(externalPath))
            {
                try { File.Delete(externalPath); } catch { /* 静默：文件可能已被手动删除 */ }
            }
        }

        /// <summary>判断指定记忆是否使用外部文件存储。</summary>
        public bool IsExternalFile(long id)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Id=$id AND FileData IS NULL AND FilePath IS NOT NULL";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        /// <summary>判断指定记忆是否有 BLOB 数据（兼容旧版数据库）。</summary>
        public bool HasBlobData(long id)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Id=$id AND FileData IS NOT NULL";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        /// <summary>将 BLOB 数据流式提取到临时文件（分块读取，不产生大 Byte[]）。</summary>
        public void ExtractBlobToFile(long id, string outputPath)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FileData FROM Items WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader(System.Data.CommandBehavior.SequentialAccess);
            if (!reader.Read()) return;
            byte[] buffer = new byte[65536]; // 64KB chunks
            long offset = 0;
            using var fs = File.Create(outputPath);
            while (true)
            {
                long read = reader.GetBytes(0, offset, buffer, 0, buffer.Length);
                if (read == 0) break;
                fs.Write(buffer, 0, (int)read);
                offset += read;
            }
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

        /// <summary>获取所有标签及其出现次数（按次数降序）。</summary>
        public List<KeyValuePair<string, int>> GetTagCounts()
        {
            var result = new Dictionary<string, int>();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Tags FROM Items WHERE Tags IS NOT NULL AND Tags != ''";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var tags = rd.GetString(0).Split(new[] { ',', '，' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (var tag in tags)
                {
                    var lower = tag.ToLowerInvariant();
                    result.TryGetValue(lower, out int count);
                    result[lower] = count + 1;
                }
            }
            var list = result.ToList();
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }

        /// <summary>获取记忆统计：总数、类型分布、占用空间。</summary>
        public (int Total, int Text, int Image, int Audio, int Video, int File, long TotalSizeBytes) GetStats()
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Type, COUNT(*), COALESCE(SUM(FileSizeBytes), 0)
                FROM Items GROUP BY Type";
            int total = 0, text = 0, image = 0, audio = 0, video = 0, file = 0;
            long totalBytes = 0;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var type = rd.GetString(0);
                var count = rd.GetInt32(1);
                var bytes = rd.GetInt64(2);
                total += count;
                totalBytes += bytes;
                switch (type)
                {
                    case "Text": text += count; break;
                    case "Image": image += count; break;
                    case "Audio": audio += count; break;
                    case "Video": video += count; break;
                    case "File": file += count; break;
                }
            }
            return (total, text, image, audio, video, file, totalBytes);
        }
    }
}