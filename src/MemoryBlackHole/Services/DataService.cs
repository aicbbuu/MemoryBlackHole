1|using System;
2|using System.Collections.Generic;
3|using System.IO;
4|using System.Linq;
5|using System.Reflection;
6|using System.Security.Cryptography;
7|using System.Text.Json;
8|using Microsoft.Data.Sqlite;
9|using MemoryBlackHole.Models;
10|
11|namespace MemoryBlackHole.Services
12|{
13|    /// <summary>
14|    /// 本地存储服务：SQLite 存元数据 + FTS5 全文索引。
15|    /// 数据全部落在 EXE 同目录 .memoryblackhole/ 下，纯本地、无云端。
16|    /// v2.0.1: 文件数据全部存入 SQLite BLOB；中文搜索 LIKE；回收站+导出导入。
17|    /// </summary>
18|    public class DataService
19|    {
20|        private readonly string _dbPath;
21|        private readonly string _connectionString;
22|
23|        public DataService(string? dataDir = null)
24|        {
25|            // 默认存到 EXE 同目录下 .memoryblackhole/
26|            // Environment.ProcessPath 在单文件发布时返回真实 EXE 路径
27|            dataDir ??= Path.Combine(
28|                Path.GetDirectoryName(
29|                    Environment.ProcessPath ?? AppContext.BaseDirectory)!,
30|                ".memoryblackhole");
31|            Directory.CreateDirectory(dataDir);
32|
33|            _dbPath = Path.Combine(dataDir, "memory.db");
34|
35|            _connectionString = $"Data Source={_dbPath}";
36|            Init();
37|        }
38|
39|        private void Init()
40|        {
41|            using var conn = new SqliteConnection(_connectionString);
42|            conn.Open();
43|
44|            // 主表
45|            using (var cmd = conn.CreateCommand())
46|            {
47|                cmd.CommandText = @"
48|                    CREATE TABLE IF NOT EXISTS Items (
49|                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
50|                        Type TEXT NOT NULL,
51|                        Title TEXT NULL,
52|                        Content TEXT NULL,
53|                        FilePath TEXT NULL,
54|                        FileData BLOB NULL,
55|                        OriginalFileName TEXT NULL,
56|                        FileSizeBytes INTEGER DEFAULT 0,
57|                        Note TEXT NULL,
58|                        Tags TEXT NULL,
59|                        CreatedAt TEXT NOT NULL,
60|                        IsFavorite INTEGER DEFAULT 0
61|                    );";
62|                cmd.ExecuteNonQuery();
63|            }
64|            // 兼容已存在的旧数据库
65|            using (var cmd = conn.CreateCommand())
66|            {
67|                cmd.CommandText = "ALTER TABLE Items ADD COLUMN FileData BLOB NULL;";
68|                try { cmd.ExecuteNonQuery(); } catch (SqliteException) { }
69|            }
70|            // v2.0.0: 回收站列
71|            using (var cmd = conn.CreateCommand())
72|            {
73|                cmd.CommandText = "ALTER TABLE Items ADD COLUMN IsDeleted INTEGER DEFAULT 0;";
74|                try { cmd.ExecuteNonQuery(); } catch (SqliteException) { }
75|            }
76|            using (var cmd = conn.CreateCommand())
77|            {
78|                cmd.CommandText = "ALTER TABLE Items ADD COLUMN DeletedAt TEXT NULL;";
79|                try { cmd.ExecuteNonQuery(); } catch (SqliteException) { }
80|            }
81|
82|            // v1.0.1: FTS5 改用 unicode61 分词器，CJK 每字为独立 token，配合逐字 AND 查询精准匹配
83|            using (var cmd = conn.CreateCommand())
84|            {
85|                cmd.CommandText = @"
86|                    DROP TABLE IF EXISTS ItemsFts;
87|                    CREATE VIRTUAL TABLE ItemsFts USING fts5(
88|                        Title, Content, Note, Tags, Content='Items', Content_Rowid='Id',
89|                        tokenize='unicode61'
90|                    );";
91|                cmd.ExecuteNonQuery();
92|            }
93|
94|            // 重建 unicode61 索引（覆盖已有数据）
95|            using (var cmd = conn.CreateCommand())
96|            {
97|                cmd.CommandText = "INSERT INTO ItemsFts(ItemsFts) VALUES('rebuild');";
98|                try { cmd.ExecuteNonQuery(); } catch { /* 表刚建时已空 */ }
99|            }
100|
101|            // v1.0.1: 常用字段加索引，加速过滤查询
102|            using (var cmd = conn.CreateCommand())
103|            {
104|                cmd.CommandText = @"
105|                    CREATE INDEX IF NOT EXISTS IX_Items_Type ON Items(Type);
106|                    CREATE INDEX IF NOT EXISTS IX_Items_CreatedAt ON Items(CreatedAt);
107|                    CREATE INDEX IF NOT EXISTS IX_Items_Favorite ON Items(IsFavorite);";
108|                cmd.ExecuteNonQuery();
109|            }
110|
111|            // Settings 表 + FTS 触发器（自动同步增删改到索引）
112|            using (var cmd = conn.CreateCommand())
113|            {
114|                cmd.CommandText = @"
115|                    CREATE TABLE IF NOT EXISTS Settings (
116|                        Key TEXT PRIMARY KEY,
117|                        Value TEXT NOT NULL
118|                    );
119|                    CREATE TRIGGER IF NOT EXISTS Items_AfterInsert AFTER INSERT ON Items BEGIN
120|                        INSERT INTO ItemsFts(rowid, Title, Content, Note, Tags)
121|                        VALUES (new.Id, new.Title, new.Content, new.Note, new.Tags);
122|                    END;
123|                    CREATE TRIGGER IF NOT EXISTS Items_AfterUpdate AFTER UPDATE ON Items BEGIN
124|                        INSERT INTO ItemsFts(ItemsFts, rowid, Title, Content, Note, Tags)
125|                        VALUES ('delete', old.Id, old.Title, old.Content, old.Note, old.Tags);
126|                        INSERT INTO ItemsFts(rowid, Title, Content, Note, Tags)
127|                        VALUES (new.Id, new.Title, new.Content, new.Note, new.Tags);
128|                    END;
129|                    CREATE TRIGGER IF NOT EXISTS Items_AfterDelete AFTER DELETE ON Items BEGIN
130|                        INSERT INTO ItemsFts(ItemsFts, rowid, Title, Content, Note, Tags)
131|                        VALUES ('delete', old.Id, old.Title, old.Content, old.Note, old.Tags);
132|                    END;";
133|                cmd.ExecuteNonQuery();
134|            }
135|        }
136|
137|        /// <summary>新增一条记忆，返回自增Id。文件数据直接存入 SQLite BLOB。</summary>
138|        public long Add(MemoryItem item)
139|        {
140|            using var conn = new SqliteConnection(_connectionString);
141|            conn.Open();
142|            using var cmd = conn.CreateCommand();
143|            cmd.CommandText = @"
144|                INSERT INTO Items(Type, Title, Content, FilePath, FileData,
145|                                  OriginalFileName, FileSizeBytes, Note, Tags, CreatedAt, IsFavorite)
146|                VALUES ($type, $title, $content, $file, $fdata, $ofn, $fsize, $note, $tags, $created, $fav);
147|                SELECT last_insert_rowid();";
148|            cmd.Parameters.AddWithValue("$type", item.Type);
149|            cmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
150|            cmd.Parameters.AddWithValue("$content", (object?)item.Content ?? DBNull.Value);
151|            cmd.Parameters.AddWithValue("$file", (object?)item.FilePath ?? DBNull.Value);
152|            cmd.Parameters.AddWithValue("$fdata", (object?)item.FileData ?? DBNull.Value);
153|            cmd.Parameters.AddWithValue("$ofn", (object?)item.OriginalFileName ?? DBNull.Value);
154|            cmd.Parameters.AddWithValue("$fsize", item.FileSizeBytes);
155|            cmd.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
156|            cmd.Parameters.AddWithValue("$tags", (object?)item.Tags ?? DBNull.Value);
157|            cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("o"));
158|            cmd.Parameters.AddWithValue("$fav", item.IsFavorite ? 1 : 0);
159|            var id = (long)cmd.ExecuteScalar()!;
160|            return id;
161|        }
162|
163|        /// <summary>按关键词全文搜索。中文/CJK 用 LIKE 模糊匹配（字节级 100% 可靠）；
164|        /// 纯英文/数字用 FTS5 前缀匹配；无关键词按条件列出。
165|        /// 支持按标签过滤（逗号分隔，不区分大小写）。</summary>
166|        public List<MemoryItem> Search(string keyword, string? type = null, bool? favorite = null, string? tag = null)
167|        {
168|            var result = new List<MemoryItem>();
169|            using var conn = new SqliteConnection(_connectionString);
170|            conn.Open();
171|
172|            string sql;
173|            using var cmd = conn.CreateCommand();
174|
175|            if (string.IsNullOrWhiteSpace(keyword))
176|            {
177|                // 无关键词：按条件列出
178|                sql = "SELECT * FROM Items WHERE 1=1";
179|                if (!string.IsNullOrEmpty(type)) sql += " AND Type=$type";
180|                if (favorite == true) sql += " AND IsFavorite=1";
181|                sql += " ORDER BY CreatedAt DESC LIMIT 200";
182|                cmd.CommandText = sql;
183|                if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue("$type", type);
184|            }
185|            else if (ContainsCJK(keyword))
186|            {
187|                // 含中文/CJK：LIKE 模糊匹配 — 字节级，100% 可靠
188|                // 每个空格分隔的 term 逐词 AND，如"会议记录" → LIKE '%会议记录%'
189|                var terms = keyword.Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
190|                var likeClauses = new List<string>();
191|                int paramIdx = 0;
192|                foreach (var t in terms)
193|                {
194|                    string p = $"$kw{paramIdx}";
195|                    likeClauses.Add($"(i.Title LIKE {p} OR i.Content LIKE {p} OR i.Note LIKE {p} OR i.Tags LIKE {p})");
196|                    cmd.Parameters.AddWithValue(p, $"%{t}%");
197|                    paramIdx++;
198|                }
199|                sql = $"SELECT DISTINCT i.* FROM Items i WHERE {string.Join(" AND ", likeClauses)}";
200|                if (!string.IsNullOrEmpty(type)) sql += " AND i.Type=$type";
201|                if (favorite == true) sql += " AND i.IsFavorite=1";
202|                sql += " ORDER BY i.CreatedAt DESC LIMIT 200";
203|                cmd.CommandText = sql;
204|                if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue("$type", type);
205|            }
206|            else
207|            {
208|                // 纯英文/数字：FTS5 前缀匹配（高效）
209|                sql = @"SELECT i.* FROM ItemsFts f
210|                        JOIN Items i ON i.Id = f.Rowid
211|                        WHERE ItemsFts MATCH $kw";
212|                if (!string.IsNullOrEmpty(type)) sql += " AND i.Type=$type";
213|                if (favorite == true) sql += " AND i.IsFavorite=1";
214|                sql += " ORDER BY bm25(ItemsFts), i.CreatedAt DESC LIMIT 200";
215|                cmd.CommandText = sql;
216|                cmd.Parameters.AddWithValue("$kw", BuildMatchQuery(keyword));
217|                if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue("$type", type);
218|            }
219|
220|            using var rd = cmd.ExecuteReader();
221|            while (rd.Read())
222|            {
223|                result.Add(new MemoryItem
224|                {
225|                    Id = rd.GetInt64(rd.GetOrdinal("Id")),
226|                    Type = rd.GetString(rd.GetOrdinal("Type")),
227|                    Title = rd.IsDBNull(rd.GetOrdinal("Title")) ? null : rd.GetString(rd.GetOrdinal("Title")),
228|                    Content = rd.IsDBNull(rd.GetOrdinal("Content")) ? null : rd.GetString(rd.GetOrdinal("Content")),
229|                    FilePath = rd.IsDBNull(rd.GetOrdinal("FilePath")) ? null : rd.GetString(rd.GetOrdinal("FilePath")),
230|                    FileData = rd.IsDBNull(rd.GetOrdinal("FileData")) ? null : (byte[])rd["FileData"],
231|                    OriginalFileName = rd.IsDBNull(rd.GetOrdinal("OriginalFileName")) ? null : rd.GetString(rd.GetOrdinal("OriginalFileName")),
232|                    FileSizeBytes = rd.GetInt64(rd.GetOrdinal("FileSizeBytes")),
233|                    Note = rd.IsDBNull(rd.GetOrdinal("Note")) ? null : rd.GetString(rd.GetOrdinal("Note")),
234|                    Tags = rd.IsDBNull(rd.GetOrdinal("Tags")) ? null : rd.GetString(rd.GetOrdinal("Tags")),
235|                    CreatedAt = DateTime.Parse(rd.GetString(rd.GetOrdinal("CreatedAt"))),
236|                    IsFavorite = rd.GetInt64(rd.GetOrdinal("IsFavorite")) == 1
237|                });
238|            }
239|
240|            // 按标签过滤（内存过滤，标签是逗号分隔文本）
241|            if (!string.IsNullOrEmpty(tag) && result.Count > 0)
242|            {
243|                var tagLower = tag.Trim().ToLowerInvariant();
244|                result = result.Where(item =>
245|                    !string.IsNullOrEmpty(item.Tags) &&
246|                    item.Tags.Split(new[] { ',', '，' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
247|                        .Any(t => t.ToLowerInvariant() == tagLower)
248|                ).ToList();
249|            }
250|
251|            return result;
252|        }
253|
254|        /// <summary>把英文/数字关键词转成 FTS5 前缀匹配表达式（空格分词，逐词 AND+前缀）。</summary>
255|                private static string BuildMatchQuery(string keyword)
256|                {
257|                    var terms = keyword.Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
258|                    var parts = new List<string>();
259|                    foreach (var t in terms)
260|                    {
261|                        var esc = t.Replace("\"", "\"\"");
262|                        parts.Add($"\"{esc}\"*");
263|                    }
264|                    return string.Join(" AND ", parts);
265|                }
266|
267|                /// <summary>检查字符串是否包含 CJK 字符。</summary>
268|                private static bool ContainsCJK(string text)
269|                {
270|                    foreach (char c in text)
271|                        if (IsCJK(c)) return true;
272|                    return false;
273|                }
274|
275|                /// <summary>判断字符是否为中日韩统一表意文字。</summary>
276|                private static bool IsCJK(char c)
277|                {
278|                    return (c >= 0x4E00 && c <= 0x9FFF) ||
279|                           (c >= 0x3400 && c <= 0x4DBF);
280|                }
281|
282|                public bool HasPassword()
283|        {
284|            using var conn = new SqliteConnection(_connectionString);
285|            conn.Open();
286|            using var cmd = conn.CreateCommand();
287|            cmd.CommandText = "SELECT COUNT(*) FROM Settings WHERE Key='password_hash'";
288|            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
289|        }
290|
291|        public void SetPassword(string password)
292|        {
293|            byte[] salt = RandomNumberGenerator.GetBytes(16);
294|            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120000, HashAlgorithmName.SHA256, 32);
295|            SaveSetting("password_salt", Convert.ToBase64String(salt));
296|            SaveSetting("password_hash", Convert.ToBase64String(hash));
297|        }
298|
299|        public bool VerifyPassword(string password)
300|        {
301|            string? saltText = ReadSetting("password_salt");
302|            string? hashText = ReadSetting("password_hash");
303|            if (saltText == null || hashText == null) return false;
304|            byte[] salt = Convert.FromBase64String(saltText);
305|            byte[] expected = Convert.FromBase64String(hashText);
306|            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120000, HashAlgorithmName.SHA256, expected.Length);
307|            return CryptographicOperations.FixedTimeEquals(actual, expected);
308|        }
309|
310|        public void Update(MemoryItem item)
311|        {
312|            using var conn = new SqliteConnection(_connectionString);
313|            conn.Open();
314|            using var cmd = conn.CreateCommand();
315|            cmd.CommandText = "UPDATE Items SET Title=$title, Content=$content, Note=$note, Tags=$tags WHERE Id=$id";
316|            cmd.Parameters.AddWithValue("$id", item.Id);
317|            cmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
318|            cmd.Parameters.AddWithValue("$content", (object?)item.Content ?? DBNull.Value);
319|            cmd.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
320|            cmd.Parameters.AddWithValue("$tags", (object?)item.Tags ?? DBNull.Value);
321|            cmd.ExecuteNonQuery();
322|        }
323|
324|        /// <summary>永久删除一条记忆。</summary>
        public void Delete(long id)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Items WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        private void SaveSetting(string key, string value)        private void SaveSetting(string key, string value)
471|        {
472|            using var conn = new SqliteConnection(_connectionString);
473|            conn.Open();
474|            using var cmd = conn.CreateCommand();
475|            cmd.CommandText = "INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=$value";
476|            cmd.Parameters.AddWithValue("$key", key);
477|            cmd.Parameters.AddWithValue("$value", value);
478|            cmd.ExecuteNonQuery();
479|        }
480|
481|        private string? ReadSetting(string key)
482|        {
483|            using var conn = new SqliteConnection(_connectionString);
484|            conn.Open();
485|            using var cmd = conn.CreateCommand();
486|            cmd.CommandText = "SELECT Value FROM Settings WHERE Key=$key";
487|            cmd.Parameters.AddWithValue("$key", key);
488|            return cmd.ExecuteScalar() as string;
489|        }
490|
491|        /// <summary>获取所有标签及其出现次数（按次数降序）。</summary>
492|        public List<KeyValuePair<string, int>> GetTagCounts()
493|        {
494|            var result = new Dictionary<string, int>();
495|            using var conn = new SqliteConnection(_connectionString);
496|            conn.Open();
497|            using var cmd = conn.CreateCommand();
498|            cmd.CommandText = "SELECT Tags FROM Items WHERE Tags IS NOT NULL AND Tags != ''";
499|            using var rd = cmd.ExecuteReader();
500|            while (rd.Read())
501|            {
502|                var tags = rd.GetString(0).Split(new[] { ',', '，' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
503|                foreach (var tag in tags)
504|                {
505|                    var lower = tag.ToLowerInvariant();
506|                    result.TryGetValue(lower, out int count);
507|                    result[lower] = count + 1;
508|                }
509|            }
510|            var list = result.ToList();
511|            list.Sort((a, b) => b.Value.CompareTo(a.Value));
512|            return list;
513|        }
514|
515|        /// <summary>获取记忆统计：总数、类型分布、占用空间。</summary>
516|        public (int Total, int Text, int Image, int Audio, int Video, int File, long TotalSizeBytes) GetStats()
517|        {
518|            using var conn = new SqliteConnection(_connectionString);
519|            conn.Open();
520|            using var cmd = conn.CreateCommand();
521|            cmd.CommandText = @"
522|                SELECT Type, COUNT(*), COALESCE(SUM(FileSizeBytes), 0)
523|                FROM Items GROUP BY Type";
524|            int total = 0, text = 0, image = 0, audio = 0, video = 0, file = 0;
525|            long totalBytes = 0;
526|            using var rd = cmd.ExecuteReader();
527|            while (rd.Read())
528|            {
529|                var type = rd.GetString(0);
530|                var count = rd.GetInt32(1);
531|                var bytes = rd.GetInt64(2);
532|                total += count;
533|                totalBytes += bytes;
534|                switch (type)
535|                {
536|                    case "Text": text += count; break;
537|                    case "Image": image += count; break;
538|                    case "Audio": audio += count; break;
539|                    case "Video": video += count; break;
540|                    case "File": file += count; break;
541|                }
542|            }
543|            return (total, text, image, audio, video, file, totalBytes);
544|        }
545|    }
546|}