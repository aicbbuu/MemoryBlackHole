using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MemoryBlackHole.Models;
using MemoryBlackHole.Services;

var root = Path.Combine(Path.GetTempPath(), $"MemoryBlackHoleStorageTests_{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    await RunAsync();
    Console.WriteLine("PASS: ≤threshold files are stored as SQLite BLOBs; >threshold files use external copies; extracted data hashes match.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

async Task RunAsync()
{
    const int threshold = 1024;
    var sourceDirectory = Path.Combine(root, "source");
    var dataDirectory = Path.Combine(root, "data");
    Directory.CreateDirectory(sourceDirectory);

    var smallSource = Path.Combine(sourceDirectory, "small.bin");
    var largeSource = Path.Combine(sourceDirectory, "large.bin");
    await File.WriteAllBytesAsync(smallSource, CreateBytes(512));
    await File.WriteAllBytesAsync(largeSource, CreateBytes(2048));

    var service = new DataService(dataDirectory, threshold);

    var small = NewFileItem("small.bin", 512);
    service.AddFile(small, smallSource);
    Assert(small.Id > 0, "Small-file insert did not return an ID.");
    Assert(small.FilePath is null, "A file at or below the threshold must not get an external path.");
    Assert(service.HasBlobData(small.Id), "A file at or below the threshold must be stored in FileData BLOB.");
    var extractedSmall = Path.Combine(root, "small-extracted.bin");
    service.ExtractBlobToFile(small.Id, extractedSmall);
    Assert(Hash(smallSource) == Hash(extractedSmall), "Extracted BLOB bytes differ from the original small file.");
    var results = service.Search("small.bin");
    Assert(results.Count == 1 && results[0].FileData is null, "Search results must not materialize FileData into memory.");

    var large = NewFileItem("large.bin", 2048);
    service.AddFile(large, largeSource);
    Assert(large.Id > 0, "Large-file insert did not return an ID.");
    Assert(!string.IsNullOrWhiteSpace(large.FilePath) && File.Exists(large.FilePath), "A file above the threshold must have an external copy.");
    Assert(!service.HasBlobData(large.Id), "A file above the threshold must not be stored in FileData BLOB.");
    Assert(Hash(largeSource) == Hash(large.FilePath!), "External file copy bytes differ from the original large file.");

    service.Delete(large.Id);
    Assert(!File.Exists(large.FilePath), "Deleting an external-file item must remove its managed copy.");

    // v2.1.3: 大文件分块写入+读回完整性测试。
    // 默认关闭,避免日常开发/CI 写 800 MB。设置 MBH_LARGE_TEST=1 启用。
    var enableLarge = string.Equals(
        Environment.GetEnvironmentVariable("MBH_LARGE_TEST"),
        "1", StringComparison.Ordinal);
    if (enableLarge)
    {
        await RunLargeBlobRoundtripAsync(root, dataDirectory, 300); // 300 MiB
        await RunLargeBlobRoundtripAsync(root, dataDirectory, 500); // 500 MiB
        Console.WriteLine("PASS: 300 MiB / 500 MiB chunked-write + readback roundtrip verified via SHA-256.");
    }
    else
    {
        Console.WriteLine("SKIP: large blob roundtrip test (set MBH_LARGE_TEST=1 to enable).");
    }
}

static async Task RunLargeBlobRoundtripAsync(string root, string dataDirectory, int sizeMiB)
{
    var largeDataDir = Path.Combine(root, $"data-{sizeMiB}m");
    var largeSourceDir = Path.Combine(root, $"source-{sizeMiB}m");
    Directory.CreateDirectory(largeSourceDir);

    var sourcePath = Path.Combine(largeSourceDir, $"random-{sizeMiB}m.bin");

    // 1) 写随机数据到临时源文件,记下 SHA-256。
    var sourceHash = await WriteRandomFileAsync(sourcePath, sizeMiB);
    var sourceSize = new FileInfo(sourcePath).Length;
    Console.WriteLine($"  · 生成 {sizeMiB} MiB 随机源文件, SHA-256={sourceHash[..16]}..., 字节数={sourceSize}");

    // 2) 默认阈值 800MB(十进制) > 300/500 MiB,文件必然走 AddBlobFile(分块写 BLOB)路径。
    var service = new DataService(largeDataDir);

    var item = NewFileItem(Path.GetFileName(sourcePath), sourceSize);
    service.AddFile(item, sourcePath);
    Assert(item.Id > 0, $"[{sizeMiB} MiB] Insert did not return an ID.");
    Assert(item.FilePath is null, $"[{sizeMiB} MiB] Should be stored as BLOB, not external copy.");
    Assert(service.HasBlobData(item.Id), $"[{sizeMiB} MiB] BLOB was not stored.");

    // 3) ExtractBlobToFile 读回到新文件,比对 SHA-256。
    var extractedPath = Path.Combine(root, $"extracted-{sizeMiB}m.bin");
    if (File.Exists(extractedPath)) File.Delete(extractedPath);
    service.ExtractBlobToFile(item.Id, extractedPath);
    var extractedHash = Hash(extractedPath);
    var extractedSize = new FileInfo(extractedPath).Length;
    Console.WriteLine($"  · 读回 {sizeMiB} MiB BLOB,    SHA-256={extractedHash[..16]}..., 字节数={extractedSize}");

    Assert(extractedSize == sourceSize, $"[{sizeMiB} MiB] Size mismatch after readback.");
    Assert(extractedHash == sourceHash, $"[{sizeMiB} MiB] SHA-256 mismatch after chunked write + readback.");

    // 4) 清理,免得下一个尺寸测试的库文件互相干扰。
    service.Delete(item.Id);
}

static async Task<string> WriteRandomFileAsync(string path, int sizeMiB)
{
    const int chunkSize = 4 * 1024 * 1024; // 4 MiB
    using var rng = RandomNumberGenerator.Create();
    long total = (long)sizeMiB * 1024 * 1024;
    using var fs = File.Create(path);
    byte[] buffer = new byte[chunkSize];
    long written = 0;
    while (written < total)
    {
        int toWrite = (int)Math.Min(buffer.Length, total - written);
        rng.GetBytes(buffer, 0, toWrite);
        await fs.WriteAsync(buffer, 0, toWrite);
        written += toWrite;
    }
    return Hash(path);
}

static MemoryItem NewFileItem(string name, long size) => new()
{
    Type = "File",
    Title = name,
    Content = name,
    OriginalFileName = name,
    FileSizeBytes = size
};

static byte[] CreateBytes(int length)
{
    var bytes = new byte[length];
    for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
    return bytes;
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
