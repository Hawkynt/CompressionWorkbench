#pragma warning disable CS1591
using FileFormat.Zip;
using SysZipArchive = System.IO.Compression.ZipArchive;
using SysZipArchiveMode = System.IO.Compression.ZipArchiveMode;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;

namespace Compression.Tests.Zip;

/// <summary>
/// Archives grown with the streaming <see cref="ZipModifier"/> checked by third-party
/// readers: Info-ZIP <c>unzip -t</c>/<c>zipinfo -v</c> (via WSL on Windows) and 7-Zip.
/// Both tiers are advisory: the tools are not guaranteed on every runner, and the ZIP64
/// case writes more than 4 GiB.
/// </summary>
[TestFixture]
public class ZipModifierExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_zipadd_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("ExternalInterop")]
  public void UnzipTest_GivenEntriesAddedByStreaming_ThenInfoZipVerifiesEveryCrc() {
    RequireWslTool("unzip");
    var path = this.BuildMixedArchive();

    var r = FsInteropToolbox.RunWsl($"unzip -t {FsInteropToolbox.WinToWsl(path)}");

    Assert.That(r.ExitCode, Is.EqualTo(0), $"unzip -t rejected the archive:\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("No errors detected"));
    foreach (var name in new[] { "deflated.txt", "stored.bin", "empty.txt", "forward.txt" })
      Assert.That(r.StdOut, Does.Contain(name));
  }

  [Test, Category("ExternalInterop")]
  public void ZipInfo_GivenEntryAddedWithTimestamp_ThenInfoZipReportsItsDosTime() {
    RequireWslTool("zipinfo");
    var path = this.BuildMixedArchive();

    var r = FsInteropToolbox.RunWsl($"zipinfo -v {FsInteropToolbox.WinToWsl(path)} deflated.txt");

    Assert.That(r.ExitCode, Is.EqualTo(0), $"zipinfo -v failed:\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("2023 May 17 13:45:30"));
    Assert.That(r.StdOut, Does.Contain("deflated"));
  }

  [Test, Category("ExternalInterop")]
  public void SevenZipTest_GivenEntriesAddedByStreaming_ThenSevenZipVerifiesEveryCrc() {
    FsInteropToolbox.Require7z();
    var path = this.BuildMixedArchive();

    var r = FsInteropToolbox.Run7z($"t \"{path}\"");

    Assert.That(r.ExitCode, Is.EqualTo(0), $"7z t rejected the archive:\n{r.StdOut}\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("Everything is Ok"));
  }

  /// <summary>
  /// Grows an archive past 4 GiB: a stored 4 GiB + 1 MiB seed entry, then a small file
  /// whose local header sits above 4 GiB (ZIP64 offset, ZIP64 end records), then a
  /// 4 GiB + 1 MiB forward-only source (ZIP64 sizes in both headers).
  /// </summary>
  [Test, Category("Performance")]
  public void AddFile_GivenArchiveAndSourceBeyondFourGiB_ThenZip64IsReadByEveryOracle() {
    const long big = (4L << 30) + (1 << 20);
    var free = new DriveInfo(Path.GetPathRoot(this._tmpDir)!).AvailableFreeSpace;
    if (free < 12L << 30)
      Assert.Ignore($"needs 12 GiB free in the temp folder, {free >> 30} GiB available");

    var path = Path.Combine(this._tmpDir, "zip64.zip");
    using (var fs = File.Create(path))
    using (var sys = new SysZipArchive(fs, SysZipArchiveMode.Create)) {
      using var s = sys.CreateEntry("seed-zeros.bin", SysCompressionLevel.NoCompression).Open();
      CopyZeros(s, big);
    }

    var small = ZipModifierStreamingTests.Compressible(10_000);
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      ZipModifier.AddFile(fs, "after-4gib.txt", new MemoryStream(small), new DateTime(2022, 1, 2, 3, 4, 6));
      ZipModifier.AddFile(fs, "big-zeros.bin", new ZeroStream(big));
    }

    using (var fs = File.OpenRead(path)) {
      var ours = new ZipReader(fs).Entries.ToDictionary(e => e.FileName);
      Assert.That(ours["after-4gib.txt"].LocalHeaderOffset, Is.GreaterThan(uint.MaxValue));
      Assert.That(ours["big-zeros.bin"].UncompressedSize, Is.EqualTo(big));

      fs.Position = 0;
      using var sys = new SysZipArchive(fs, SysZipArchiveMode.Read);
      Assert.That(sys.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "seed-zeros.bin", "after-4gib.txt", "big-zeros.bin" }));
      var afterEntry = sys.GetEntry("after-4gib.txt")!;
      using (var s = afterEntry.Open()) {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.That(ms.ToArray(), Is.EqualTo(small));
      }
      Assert.That(afterEntry.LastWriteTime.DateTime, Is.EqualTo(new DateTime(2022, 1, 2, 3, 4, 6)));

      var zeros = sys.GetEntry("big-zeros.bin")!;
      Assert.That(zeros.Length, Is.EqualTo(big));
      var (length, allZero, crc) = Drain(zeros);
      Assert.That(length, Is.EqualTo(big));
      Assert.That(allZero, Is.True);
      Assert.That(crc, Is.EqualTo(zeros.Crc32));
      Assert.That(crc, Is.EqualTo(ZeroCrc(big)));
    }

    if (FsInteropToolbox.SevenZipAvailable) {
      var r = FsInteropToolbox.Run7z($"t \"{path}\"");
      Assert.That(r.ExitCode, Is.EqualTo(0), $"7z t rejected the ZIP64 archive:\n{r.StdOut}\n{r.StdErr}");
    }
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("unzip")) {
      var r = FsInteropToolbox.RunWsl($"unzip -t {FsInteropToolbox.WinToWsl(path)}");
      Assert.That(r.ExitCode, Is.EqualTo(0), $"unzip -t rejected the ZIP64 archive:\n{r.StdOut}\n{r.StdErr}");
    }
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private string BuildMixedArchive() {
    var path = Path.Combine(this._tmpDir, "grown.zip");
    using (var fs = File.Create(path)) {
      var w = new ZipWriter(fs, leaveOpen: true);
      w.AddEntry("seed.txt", "seed-content"u8.ToArray(), ZipCompressionMethod.Store);
      w.Finish();
    }
    var noise = new byte[50_000];
    new Random(1).NextBytes(noise);
    using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
      ZipModifier.AddFile(fs, "deflated.txt", new MemoryStream(ZipModifierStreamingTests.Compressible(200_000)),
        new DateTime(2023, 5, 17, 13, 45, 31));
      ZipModifier.AddFile(fs, "stored.bin", new MemoryStream(noise));
      ZipModifier.AddFile(fs, "empty.txt", new MemoryStream());
      ZipModifier.AddFile(fs, "forward.txt",
        new ZipModifierStreamingTests.ForwardOnlyStream(ZipModifierStreamingTests.Compressible(80_000)));
    }
    return path;
  }

  private static void RequireWslTool(string tool) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Run `wsl --install`, then `sudo apt install -y unzip` inside the distro.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"'{tool}' is not installed. Run inside WSL: `sudo apt install -y unzip`.");
  }

  private static void CopyZeros(Stream destination, long count) {
    var buffer = new byte[1 << 20];
    for (var left = count; left > 0;) {
      var n = (int)Math.Min(buffer.Length, left);
      destination.Write(buffer, 0, n);
      left -= n;
    }
  }

  private static (long Length, bool AllZero, uint Crc) Drain(System.IO.Compression.ZipArchiveEntry entry) {
    var crc = new System.IO.Hashing.Crc32();
    var buffer = new byte[1 << 20];
    long length = 0;
    var allZero = true;
    using var s = entry.Open();
    int n;
    while ((n = s.Read(buffer, 0, buffer.Length)) > 0) {
      var chunk = buffer.AsSpan(0, n);
      crc.Append(chunk);
      allZero &= !chunk.ContainsAnyExcept((byte)0);
      length += n;
    }
    return (length, allZero, crc.GetCurrentHashAsUInt32());
  }

  private static uint ZeroCrc(long count) {
    var crc = new System.IO.Hashing.Crc32();
    var zeros = new byte[1 << 20];
    for (var left = count; left > 0;) {
      var n = (int)Math.Min(zeros.Length, left);
      crc.Append(zeros.AsSpan(0, n));
      left -= n;
    }
    return crc.GetCurrentHashAsUInt32();
  }

  /// <summary>Forward-only source of <c>length</c> zero bytes; nothing is held in memory.</summary>
  private sealed class ZeroStream(long length) : Stream {
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) {
      var n = (int)Math.Min(count, length - this._position);
      buffer.AsSpan(offset, n).Clear();
      this._position += n;
      return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
