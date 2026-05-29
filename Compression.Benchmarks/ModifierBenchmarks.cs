#pragma warning disable CS1591

using BenchmarkDotNet.Attributes;
using Compression.Lib;
using FileSystem.D64;
using FileSystem.Fat;
using FileFormat.Zip;

namespace Compression.Benchmarks;

/// <summary>
/// Benchmarks the in-place modifier paths: D64 AddFile and ZIP AddFile are true
/// O(touched bytes) modifiers. FAT AddFile is a rebuild path included for comparison.
/// A ByteCountingStream verifies that D64 and ZIP IO ratio stays below 5%.
/// </summary>
[Config(typeof(InProcessConfig))]
public class ModifierBenchmarks {

  private byte[] _fatImage = null!;
  private byte[] _d64Image = null!;
  private byte[] _zipImage = null!;

  private byte[] _fatPayload = null!;
  private byte[] _d64Payload = null!;
  private byte[] _zipPayload = null!;

  [GlobalSetup]
  public void Setup() {
    FormatRegistration.EnsureInitialized();

    // FAT image: ~1 MB with 10 files
    var fatWriter = new FatWriter();
    for (var i = 0; i < 10; i++)
      fatWriter.AddFile($"FILE{i:D4}.TXT", new byte[1024]);
    _fatImage = fatWriter.Build(totalSectors: 2048);

    // D64 image: standard 174848 bytes with 5 files
    var d64Writer = new D64Writer();
    for (var i = 0; i < 5; i++)
      d64Writer.AddFile($"FILE{i}", new byte[256]);
    _d64Image = d64Writer.Build();

    // ZIP image: small archive with a few files
    using var zipMs = new MemoryStream();
    using (var zipWriter = new ZipWriter(zipMs, leaveOpen: true))
      for (var i = 0; i < 5; i++)
        zipWriter.AddEntry($"file{i}.txt", new byte[512]);
    _zipImage = zipMs.ToArray();

    // Payloads
    _fatPayload = new byte[1024];
    Array.Fill(_fatPayload, (byte)0x41);
    _d64Payload = new byte[256];
    Array.Fill(_d64Payload, (byte)0x42);
    _zipPayload = new byte[1024];
    Array.Fill(_zipPayload, (byte)0x43);
  }

  [Benchmark(Description = "FAT AddFile (rebuild)")]
  public void FatAddFile() {
    // FAT uses a full rebuild path via the descriptor
    var ms = new MemoryStream((byte[])_fatImage.Clone(), writable: true);
    ms.SetLength(_fatImage.Length);
    var reader = new FatReader(ms);
    var combined = new FatWriter();
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory))
      combined.AddFile(entry.Name, reader.Extract(entry));
    combined.AddFile("NEWFILE.TXT", _fatPayload);
    var rebuilt = combined.Build(totalSectors: _fatImage.Length / 512);
    ms.Position = 0;
    ms.Write(rebuilt);
    ms.SetLength(rebuilt.Length);
  }

  [Benchmark(Description = "D64 AddFile (in-place)")]
  public void D64AddFile() {
    using var stream = new ByteCountingStream(new MemoryStream((byte[])_d64Image.Clone(), writable: true));
    D64Modifier.AddFile(stream, "NEWFILE", _d64Payload);
    VerifyIoRatio(stream, _d64Image.Length);
  }

  [Benchmark(Description = "ZIP AddFile (in-place)")]
  public void ZipAddFile() {
    var ms = new MemoryStream();
    ms.Write(_zipImage);
    ms.Position = 0;
    using var stream = new ByteCountingStream(ms);
    ZipModifier.AddFile(stream, "newfile.txt", _zipPayload);
    VerifyIoRatio(stream, _zipImage.Length);
  }

  private static void VerifyIoRatio(ByteCountingStream stream, long imageSize) {
    var totalIo = stream.BytesRead + stream.BytesWritten;
    var ratio = imageSize > 0 ? (double)totalIo / imageSize : 0;
    if (ratio > 0.05)
      throw new InvalidOperationException(
        $"IO ratio {ratio:P1} exceeds 5%: read={stream.BytesRead}, written={stream.BytesWritten}, image={imageSize}");
  }
}
