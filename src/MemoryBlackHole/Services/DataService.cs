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
    /// 文件 ≤ <see cref="LargeFileThreshold"/> 时流式分块写入 SQLite BLOB；
    /// 超过时复制到 .memoryblackhole/files/，数据库只保存路径。
    /// v2.1.3: 连接串统一走 <see cref="BuildConnectionString"/>，并通过 PRAGMA
    ///   开启 WAL / 调大 cache_size / 启用 mmap_size，降低大事务的内存峰值，
    ///   避免在 win-x64 下写入几百 MB BLOB 时仍触发 SQLITE_NOMEM。
    ///   注意：本项目只发布 win-x64；win-x86 因地址空间限制不再支持大 BLOB。
    /// </summary>
    public class DataService : IDisposable
    {
        /// <summary>单个 SQLite BLOB 的最大文件大小；超过此大小改为外部副本。（800MB 十进制，刻意小于 SQLite 默认上限 ~953MB）</summary>
        public const long LargeFileThreshold = 800_000_000; // 800 MB（十进制）

        /// <summary>BLOB 写入时的内存拷贝分块大小（4 MiB）。</summary>
        internal const int BlobChunkSize = 4 * 1024 * 1024;

        // v3.1.0: 列表投影列：显式列出，排除 FileData(大 BLOB 预览时按流提取)，保留 Thumbnail(列表缩略图)。
        // 避免 SELECT * 把单行最大近 800MB 的 BLOB 整列带进结果集。
        private const string ItemColumns = "Id, Type, Title, Content, FilePath, Thumbnail, OriginalFileName, FileSizeBytes, Note, Tags, CreatedAt, IsFavorite";
        private const string ItemColumnsAliased = "i.Id, i.Type, i.Title, i.Content, i.FilePath, i.Thumbnail, i.OriginalFileName, i.FileSizeBytes, i.Note, i.Tags, i.CreatedAt, i.IsFavorite";

        // PRAGMA 数值集中放置，便于统一调整。cache_size 单位是"页"或"KB"，
        // 负值表示 KB 绝对值；这里取 -65536 ≈ 64 MiB 的页缓存。
        private const int CacheSizeKB = -65536;
        // mmap_size 单位字节；30 GiB 是个安全上界(64-bit 下),够大但不会过度提交。
        private const long MmapSizeBytes = 30L * 1024 * 1024 * 1024;
        private const int PageSizeBytes = 4096;

        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly string _fileStoreDir;
        private readonly long _largeFileThreshold;
        // v3.0.7: Search 专用长连接(单例),避免每次 Search 都冷启新连接 + 7 个 PRAGMA
        // 写操作(AddFile/Update/Delete)仍用 using 短连接,保证写并发安全
        private SqliteConnection? _searchConn;
        private readonly object _searchConnLock = new();
        // v3.0.9: Settings 表(SaveSetting/ReadSetting)专用长连接,启动/密码验证时省冷启 ~30ms
        // 与 Search 同一模式:lazy init + lock,线程安全
        private SqliteConnection? _settingConn;
        private readonly object _settingConnLock = new();

        /// <summary>
        /// 创建本地存储服务。默认文件大小阈值为 800MB（十进制）；可传入较小阈值用于自动化测试。
        /// </summary>
        public DataService(string? dataDir = null, long largeFileThreshold = LargeFileThreshold)
        {
            // 默认存到 EXE 同目录下 .memoryblackhole/
            // Environment.ProcessPath 在单文件发布时返回真实 EXE 路径
            dataDir ??= Path.Combine(
                Path.GetDirectoryName(
                    Environment.ProcessPath ?? AppContext.BaseDirectory)!,
                ".memoryblackhole");
            if (largeFileThreshold < 0)
                throw new ArgumentOutOfRangeException(nameof(largeFileThreshold));

            Directory.CreateDirectory(dataDir);

            _largeFileThreshold = largeFileThreshold;
            _dbPath = Path.Combine(dataDir, "memory.db");
            _fileStoreDir = Path.Combine(dataDir, "files");
            Directory.CreateDirectory(_fileStoreDir);

            _connectionString = BuildConnectionString(_dbPath);
            Init();
        }

        /// <summary>
        /// 构造连接串。Pool=True 配合共享缓存(Shared Cache)让 PRAGMA 跨连接生效。
        /// 关键 PRAGMA(wal/cache_size/mmap_size/page_size)在 <see cref="OpenAndConfigure"/>
        /// 中针对每个连接重新设置，避免复用连接时设置丢失。
        /// </summary>
        internal static string BuildConnectionString(string dbPath)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString();
        }

        /// <summary>
        /// 打开连接并应用 PRAGMA。WAL 模式下 cache_size 作用于所有连接；
        /// mmap_size 是 per-connection, 必须每次都设。page_size 只能在建库前设,
        /// 已有库时 SQLITE 会忽略,所以这里也安全无副作用。
        /// </summary>
        private static void OpenAndConfigure(SqliteConnection conn)
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous  = NORMAL;
                PRAGMA cache_size   = {CacheSizeKB};
                PRAGMA mmap_size    = {MmapSizeBytes};
                PRAGMA page_size    = {PageSizeBytes};
                PRAGMA temp_store   = MEMORY;";
            cmd.ExecuteNonQuery();
        }

        private void Init()
        {
            using var conn = new SqliteConnection(_connectionString);
            OpenAndConfigure(conn);

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
            // v3.1.4: 缩略图列(仅 Image 类型,AddFile 时生成 100x100 PNG 存此)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "ALTER TABLE Items ADD COLUMN Thumbnail BLOB NULL;";
                try { cmd.ExecuteNonQuery(); } catch (SqliteException) { }
            }

            // v1.0.1: FTS5 改用 unicode61 分词器，CJK 每字为独立 token，配合逐字 AND 查询精准匹配
            // v3.0.8: 加 prefix='1 2 3 4' 让 1~4 字符前缀都建索引,支持单字/单字母搜索
            //   (默认 prefix='2 3 4' 跳过 1 字符索引,导致 "机" "a" 等单字搜不到)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    DROP TABLE IF EXISTS ItemsFts;
                    CREATE VIRTUAL TABLE ItemsFts USING fts5(
                        Title, Content, Note, Tags, Content='Items', Content_Rowid='Id',
                        tokenize='unicode61', prefix='1 2 3 4'
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
                    CREATE INDEX IF NOT EXISTS IX_Items_Favorite ON Items(IsFavorite);
                -- v3.0.7: 配合 Search 的非文本类过滤(Type 走索引已够)
                CREATE INDEX IF NOT EXISTS IX_Items_OriginalFileName ON Items(OriginalFileName);";
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
        /// <remarks>对 &gt; <see cref="LargeFileThreshold"/> 的 FileData 直接拒绝,防止 <see cref="Add(MemoryItem)"/>
        /// 走 byte[] 整体拷贝导致 OOM;大文件应走 <see cref="AddFile(MemoryItem, string)"/> 分块流式路径。</remarks>
        public long Add(MemoryItem item)
        {
            if (item.FileData != null && item.FileData.Length > LargeFileThreshold)
                throw new ArgumentException(
                    $"FileData 长度 {item.FileData.Length} 超过阈值 {LargeFileThreshold},请改用 AddFile 分块流式写入。",
                    nameof(item));
            using var conn = new SqliteConnection(_connectionString);
            OpenAndConfigure(conn);
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
        /// 新增一条文件类记忆。文件大小不超过阈值时通过 SqliteBlob 分块流式写入 SQLite；
        /// 超过阈值时复制到 .memoryblackhole/files/，数据库只保存副本路径。
        /// </summary>
        public void AddFile(MemoryItem item, string sourcePath, byte[]? precomputedThumbnail = null)
        {
            var fileInfo = new FileInfo(sourcePath);
            item.FileSizeBytes = fileInfo.Length;

            // v3.1.4: 仅 Image 类型有缩略图。v3.1.0: 缩略图由调用方在 UI 线程生成
            // (GenerateThumbnail 依赖 WPF STA),这里只落库,避免在后台线程触碰 WPF 成像 API。
            if (item.Type == "Image")
                item.Thumbnail = precomputedThumbnail; // 可为 null(损坏图/失败),不阻塞添加

            if (fileInfo.Length <= _largeFileThreshold)
            {
                AddBlobFile(item, sourcePath);
                return;
            }

            AddExternalFile(item, sourcePath);
        }

        /// <summary>
        /// v3.1.4: 生成图片缩略图(默认 100x100,PNG 编码)。
        /// 用 BitmapImage(StreamSource)+ RenderTargetBitmap 缩放,避免把大图全量加载到内存。
        /// 失败返回 null(损坏图、格式不支持等),不抛异常。
        /// </summary>
        public static byte[]? GenerateThumbnail(string imagePath, int targetSize)
        {
            try
            {
                using var fs = File.OpenRead(imagePath);
                var src = new System.Windows.Media.Imaging.BitmapImage();
                src.BeginInit();
                src.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                src.StreamSource = fs;
                src.DecodePixelWidth = targetSize;
                src.EndInit();
                src.Freeze();

                double scale = (double)targetSize / Math.Max(src.PixelWidth, src.PixelHeight);
                int w = (int)Math.Round(src.PixelWidth * scale);
                int h = (int)Math.Round(src.PixelHeight * scale);

                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(src, new System.Windows.Rect(0, 0, w, h));
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(dv);

                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把 ≤ <see cref="_largeFileThreshold"/> 的文件流式分块写入 SQLite BLOB。
        /// 关键点：
        /// 1) 用 zeroblob($size) 预占空间，再通过 SqliteBlob 增量写入，避免整块加载到内存。
        /// 2) 显式 4 MiB 分块循环 Write，不依赖 SqliteBlob.CopyTo 的隐式分块。
        ///    大 BLOB 在 commit 阶段容易触发 SQLITE_NOMEM(7)，分块更稳。
        /// 3) WAL 模式下大写事务 commit 后做一次 PRAGMA wal_checkpoint(PASSIVE)，
        ///    把 WAL 帧落盘、释放页缓存，避免下一次写入继续累积导致 OOM。
        /// </summary>
        private void AddBlobFile(MemoryItem item, string sourcePath)
        {
            using var conn = new SqliteConnection(_connectionString);
            OpenAndConfigure(conn);
            using var transaction = conn.BeginTransaction();
            try
            {
                // 先释放 INSERT 命令，再打开增量 BLOB；否则 SQLite 会保留活动语句而拒绝提交事务。
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO Items(Type, Title, Content, FilePath, FileData, Thumbnail,
                                          OriginalFileName, FileSizeBytes, Note, Tags, CreatedAt, IsFavorite)
                        VALUES ($type, $title, $content, NULL, zeroblob($blobSize), $thumb,
                                $ofn, $fsize, $note, $tags, $created, $fav);
                        SELECT last_insert_rowid();";
                    AddItemParameters(cmd, item);
                    cmd.Parameters.AddWithValue("$blobSize", item.FileSizeBytes);
                    cmd.Parameters.AddWithValue("$thumb", (object?)item.Thumbnail ?? DBNull.Value);
                    item.Id = (long)cmd.ExecuteScalar()!;
                }

                // 显式分块写入：input.Read(buffer) → output.Write(buffer, 0, read)
                // 4 MiB 是经验值：既减少 I/O 次数,也保证单次页缓冲在 win-x64 下不会爆。
                using (var input = File.OpenRead(sourcePath))
                using (var output = new SqliteBlob(conn, "Items", "FileData", item.Id, readOnly: false))
                {
                    byte[] buffer = new byte[BlobChunkSize];
                    long written = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        written += read;
                    }
                    if (written != item.FileSizeBytes)
                        throw new IOException($"BLOB 写入不完整: 期望 {item.FileSizeBytes} 字节, 实际 {written} 字节。");
                }

                transaction.Commit();

                // 大 BLOB commit 后把 WAL 帧落盘,降低后续写入的内存峰值。
                // checkpoint 失败不应中断主流程,所以单独 try。
                try
                {
                    using var cp = conn.CreateCommand();
                    cp.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                    cp.ExecuteNonQuery();
                }
                catch { /* checkpoint 是优化,失败无所谓 */ }

                item.FilePath = null;
                item.FileData = null;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private void AddExternalFile(MemoryItem item, string sourcePath)
        {
            var extension = Path.GetExtension(sourcePath);
            var destination = Path.Combine(_fileStoreDir, $"{Guid.NewGuid():N}{extension}");
            File.Copy(sourcePath, destination, overwrite: false);

            try
            {
                using var conn = new SqliteConnection(_connectionString);
                OpenAndConfigure(conn);
                using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Items(Type, Title, Content, FilePath, FileData, Thumbnail,
                                  OriginalFileName, FileSizeBytes, Note, Tags, CreatedAt, IsFavorite)
                VALUES ($type, $title, $content, $file, $fdata, $thumb,
                        $ofn, $fsize, $note, $tags, $created, $fav);
                SELECT last_insert_rowid();";
                AddItemParameters(cmd, item);
                cmd.Parameters.AddWithValue("$file", destination);
                cmd.Parameters.AddWithValue("$fdata", (object?)item.FileData ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$thumb", (object?)item.Thumbnail ?? DBNull.Value);
                item.Id = (long)cmd.ExecuteScalar()!;
                item.FilePath = destination;
                item.FileData = null;
            }
            catch
            {
                try { File.Delete(destination); } catch { }
                throw;
            }
        }

        private static void AddItemParameters(SqliteCommand cmd, MemoryItem item)
        {
            cmd.Parameters.AddWithValue("$type", item.Type);
            cmd.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$content", (object?)item.Content ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ofn", (object?)item.OriginalFileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fsize", item.FileSizeBytes);
            cmd.Parameters.AddWithValue("$note", (object?)item.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tags", (object?)item.Tags ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$fav", item.IsFavorite ? 1 : 0);
        }

        /// <summary>按关键词全文搜索。
        /// v3.0.7 改造:
        ///   - 走专用长连接(_searchConn)避免每次冷启新连接 + 7 PRAGMA
        ///   - 所有关键词(含 CJK)统一走 FTS5(ItemsFts 用 unicode61 分词器,CJK 逐字 AND)
        ///     不再走 LIKE 全表扫(几万条数据时 LIKE 慢 100~500ms,FTS5 几 ms)
        ///   - 标签过滤先用 SQL 的 Tags GLOB '*tag*' 在数据库端粗筛,内存只做精确切分
        ///   - 非文本类(Type=Image/Audio/Video/File)只搜 Title + OriginalFileName + Tags,
        ///     Content 字段对这些类型是文件名(重复)或 URL,搜了无意义还拖慢 FTS5
        /// 支持按 type/favorite/tag 过滤。
        /// </summary>
        public List<MemoryItem> Search(string keyword, string? type = null, bool? favorite = null, string? tag = null)
        {
            var result = new List<MemoryItem>();
            var conn = GetOrOpenSearchConn();
            using var cmd = conn.CreateCommand();

            // 构造 type/keyword 过滤
            bool hasKeyword = !string.IsNullOrWhiteSpace(keyword);
            string? trimmedKeyword = hasKeyword ? keyword!.Trim() : null;

            // 非文本类:Type != Text/Link 时,只搜 Title + OriginalFileName + Tags(跳过 Content)
            // Text/Link:搜 Title + Content + Note + Tags
            bool onlyFileName = !string.IsNullOrEmpty(type) && type != "Text" && type != "Link";

            if (!hasKeyword)
            {
                // 无关键词:按条件列出(走 Type/CreatedAt 索引)
                string sql = "SELECT " + ItemColumns + " FROM Items WHERE 1=1";
                if (!string.IsNullOrEmpty(type)) sql += " AND Type=$type";
                if (favorite == true) sql += " AND IsFavorite=1";
                if (!string.IsNullOrEmpty(tag)) sql += " AND LOWER(Tags) GLOB $tagG";   // v3.0.9: LOWER 让 tag 过滤大小写不敏感
                sql += " ORDER BY CreatedAt DESC LIMIT 200";
                cmd.CommandText = sql;
                if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue("$type", type);
                if (!string.IsNullOrEmpty(tag)) cmd.Parameters.AddWithValue("$tagG", "*" + EscapeGlob(tag!.ToLowerInvariant()) + "*");
            }
            else
            {
                // 有关键词:全部走 FTS5(unicode61 对 CJK 逐字分词,英文/数字按词)。
                // v3.1.0: 非文本类的"只搜文件名/标题/标签"改为对 FTS 列(f.Title/f.Content/f.Tags)做 MATCH;
                //   不能再对普通表 i 的列用 MATCH(会抛 "unable to use function MATCH")。
                string sql = onlyFileName
                    ? @"SELECT " + ItemColumnsAliased + @" FROM ItemsFts f
                            JOIN Items i ON i.Id = f.Rowid
                            WHERE ItemsFts MATCH $kw
                              AND (f.Title MATCH $kw OR f.Content MATCH $kw OR f.Tags MATCH $kw)"
                    : @"SELECT " + ItemColumnsAliased + @" FROM ItemsFts f
                            JOIN Items i ON i.Id = f.Rowid
                            WHERE ItemsFts MATCH $kw";
                if (!string.IsNullOrEmpty(type)) sql += " AND i.Type=$type";
                if (favorite == true) sql += " AND i.IsFavorite=1";
                if (!string.IsNullOrEmpty(tag)) sql += " AND LOWER(i.Tags) GLOB $tagG";   // v3.0.9: 同上
                // 排序:bm25 相关性优先,CreatedAt 次之
                sql += " ORDER BY bm25(ItemsFts), i.CreatedAt DESC LIMIT 200";
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$kw", BuildMatchQuery(trimmedKeyword!));
                if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue("$type", type);
                if (!string.IsNullOrEmpty(tag)) cmd.Parameters.AddWithValue("$tagG", "*" + EscapeGlob(tag!.ToLowerInvariant()) + "*");
            }

            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                // 搜索列表只加载元数据；文件 BLOB 在预览时按流提取(Select 已排除 FileData 列)。
                result.Add(MapRow(rd));
            }

            // 标签精确匹配(内存中切分 Tags 文本,SQL GLOB 已粗筛过)
            // SQL 的 Tags GLOB '*tag*' 已剔除不含该子串的行;这里只做精确化(逗号分隔的某个 tag 完整等于)
            if (!string.IsNullOrEmpty(tag) && result.Count > 0)
            {
                var tagLower = tag!.Trim().ToLowerInvariant();
                result = result.Where(item =>
                    !string.IsNullOrEmpty(item.Tags) &&
                    item.Tags!.Split(new[] { ',', '，' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Any(t => t.ToLowerInvariant() == tagLower)
                ).ToList();
            }

            return result;
        }

        /// <summary>取/打开 Search 专用长连接(线程安全单例)。</summary>
        private SqliteConnection GetOrOpenSearchConn()
        {
            lock (_searchConnLock)
            {
                if (_searchConn == null)
                {
                    _searchConn = new SqliteConnection(_connectionString);
                    OpenAndConfigure(_searchConn);
                }
                else if (_searchConn.State != System.Data.ConnectionState.Open)
                {
                    OpenAndConfigure(_searchConn);
                }
                return _searchConn;
            }
        }

        /// <summary>GLOB 模式特殊字符转义(* ? [ ] \)。</summary>
        private static string EscapeGlob(string s)
        {
            return s.Replace("\\", "\\\\")
                    .Replace("*", "\\*")
                    .Replace("?", "\\?")
                    .Replace("[", "\\[")
                    .Replace("]", "\\]");
        }

        /// <summary>
        /// v3.1.4: 按字符类型分别构造 FTS5 MATCH 表达式:
        ///   - 纯 ASCII 字母/数字 term → "\"term\"*"  (前缀匹配,让 prefix='1 2 3 4' 起作用,搜 "a" 命中 "apple")
        ///   - 含 CJK 字符 term → "\"term\""  (精确匹配,unicode61 已按字分 token,单字精确匹配即可)
        ///   - 多 term 用 AND 组合
        /// </summary>
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

        public bool HasPassword()
        {
            using var conn = new SqliteConnection(_connectionString);
            OpenAndConfigure(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Settings WHERE Key='password_hash'";
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        public async Task SetPassword(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            // v3.1.0: PBKDF2(120000 迭代)耗时,移出 UI 线程异步派生;迭代数不降低(安全性优先)。
            byte[] hash = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(password, salt, 120000, HashAlgorithmName.SHA256, 32));
            SaveSetting("password_salt", Convert.ToBase64String(salt));
            SaveSetting("password_hash", Convert.ToBase64String(hash));
        }

        public async Task<bool> VerifyPassword(string password)
        {
            string? saltText = ReadSetting("password_salt");
            string? hashText = ReadSetting("password_hash");
            if (saltText == null || hashText == null) return false;
            byte[] salt = Convert.FromBase64String(saltText);
            byte[] expected = Convert.FromBase64String(hashText);
            byte[] actual = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(password, salt, 120000, HashAlgorithmName.SHA256, expected.Length));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        public void Update(MemoryItem item)
        {
            using var conn = new SqliteConnection(_connectionString);
            OpenAndConfigure(conn);
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
            OpenAndConfigure(conn);

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
            OpenAndConfigure(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Id=$id AND FileData IS NULL AND FilePath IS NOT NULL";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        /// <summary>判断指定记忆是否有 BLOB 数据（兼容旧版数据库）。</summary>
        public bool HasBlobData(long id)
        {
            using var conn = new SqliteConnection(_connectionString);
            OpenAndConfigure(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Items WHERE Id=$id AND FileData IS NOT NULL";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        /// <summary>
        /// v3.0.9: 按 OriginalFileName + FileSizeBytes 查重(忽略 IsDeleted=1 的回收站项)。
        /// 返回首个匹配项;无重复返回 null。仅对文件类(非 Text/Link)有效。
        /// </summary>
        public MemoryItem? FindDuplicate(string? originalFileName, long fileSizeBytes)
        {
            if (string.IsNullOrEmpty(originalFileName) || fileSizeBytes <= 0)
                return null;
            using var conn = new SqliteConnection(_connectionString);
            OpenAndConfigure(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT " + ItemColumns + @"
                                  FROM Items
                                  WHERE IsDeleted = 0 AND OriginalFileName = $name AND FileSizeBytes = $size
                                  ORDER BY CreatedAt DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$name", originalFileName);
            cmd.Parameters.AddWithValue("$size", fileSizeBytes);
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return null;
            return MapRow(rd);
        }

        /// <summary>
        /// v3.1.0: 从 SqliteDataReader 投影为 MemoryItem(列表元数据,不含 FileData)。
        /// Search 与 FindDuplicate 共用,避免重复。列清单见 <see cref="ItemColumns"/>。
        /// </summary>
        private static MemoryItem MapRow(SqliteDataReader rd)
        {
            return new MemoryItem
            {
                Id = rd.GetInt64(rd.GetOrdinal("Id")),
                Type = rd.GetString(rd.GetOrdinal("Type")),
                Title = rd.IsDBNull(rd.GetOrdinal("Title")) ? null : rd.GetString(rd.GetOrdinal("Title")),
                Content = rd.IsDBNull(rd.GetOrdinal("Content")) ? null : rd.GetString(rd.GetOrdinal("Content")),
                FilePath = rd.IsDBNull(rd.GetOrdinal("FilePath")) ? null : rd.GetString(rd.GetOrdinal("FilePath")),
                FileData = null,
                // v3.1.4: 缩略图 BLOB(小,KB 级)— 列表行显示
                Thumbnail = rd.IsDBNull(rd.GetOrdinal("Thumbnail")) ? null : (byte[]?)rd.GetValue(rd.GetOrdinal("Thumbnail")),
                OriginalFileName = rd.IsDBNull(rd.GetOrdinal("OriginalFileName")) ? null : rd.GetString(rd.GetOrdinal("OriginalFileName")),
                FileSizeBytes = rd.GetInt64(rd.GetOrdinal("FileSizeBytes")),
                Note = rd.IsDBNull(rd.GetOrdinal("Note")) ? null : rd.GetString(rd.GetOrdinal("Note")),
                Tags = rd.IsDBNull(rd.GetOrdinal("Tags")) ? null : rd.GetString(rd.GetOrdinal("Tags")),
                CreatedAt = DateTime.Parse(rd.GetString(rd.GetOrdinal("CreatedAt"))),
                IsFavorite = rd.GetInt64(rd.GetOrdinal("IsFavorite")) == 1
            };
        }

        /// <summary>将 BLOB 数据流式提取到临时文件（分块读取，不产生大 Byte[]）。</summary>
        public void ExtractBlobToFile(long id, string outputPath)
        {
            using var conn = new SqliteConnection(_connectionString);
            OpenAndConfigure(conn);
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

        /// <summary>取/打开 Settings 专用长连接(线程安全单例)。</summary>
        private SqliteConnection GetOrOpenSettingConn()
        {
            lock (_settingConnLock)
            {
                if (_settingConn == null)
                {
                    _settingConn = new SqliteConnection(_connectionString);
                    OpenAndConfigure(_settingConn);
                }
                else if (_settingConn.State != System.Data.ConnectionState.Open)
                {
                    OpenAndConfigure(_settingConn);
                }
                return _settingConn;
            }
        }

        private void SaveSetting(string key, string value)
        {
            var conn = GetOrOpenSettingConn();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=$value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }

        private string? ReadSetting(string key)
        {
            var conn = GetOrOpenSettingConn();
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
            OpenAndConfigure(conn);
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
            OpenAndConfigure(conn);
            using var cmd = conn.CreateCommand();
            // v3.0.9: 加 WHERE IsDeleted=0 过滤(虽目前 UI 没回收站,IsDeleted 恒 0,但防未来)
            cmd.CommandText = @"
                SELECT Type, COUNT(*), COALESCE(SUM(FileSizeBytes), 0)
                FROM Items WHERE IsDeleted = 0 GROUP BY Type";
            int total = 0, text = 0, image = 0, audio = 0, video = 0, file = 0;
            long totalBytes = 0;
            // v3.0.9: 加 WHERE IsDeleted=0 过滤(虽目前 UI 没回收站,IsDeleted 恒 0,但防未来)
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

        /// <summary>v3.1.0: 空闲/退出时执行 WAL checkpoint(TRUNCATE),把 WAL 落盘并截断,防止库文件只增不减。</summary>
        public void Checkpoint()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                OpenAndConfigure(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { App.Log("wal_checkpoint(TRUNCATE) 失败: " + ex.Message); }
        }

        /// <summary>v3.1.0: 手动 VACUUM 入口(删除大量数据后按需调用)。注意:VACUUM 会锁库,勿在写入过程中调用。</summary>
        public void Vacuum()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                OpenAndConfigure(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM;";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { App.Log("VACUUM 失败: " + ex.Message); }
        }

        /// <summary>v3.1.0: 释放两个长连接(Search/Settings)。保持 lazy init + lock 复用,仅退出时调用。</summary>
        public void Dispose()
        {
            lock (_searchConnLock)
            {
                _searchConn?.Dispose();
                _searchConn = null;
            }
            lock (_settingConnLock)
            {
                _settingConn?.Dispose();
                _settingConn = null;
            }
        }
    }
}