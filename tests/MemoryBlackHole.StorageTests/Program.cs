using System;
using System.IO;
using System.Security.Cryptography;
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
