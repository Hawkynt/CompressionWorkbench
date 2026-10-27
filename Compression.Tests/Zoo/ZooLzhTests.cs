using System.Reflection;
using FileFormat.Zoo;

namespace Compression.Tests.Zoo;

/// <summary>
/// Zoo packing method 2 (LZH, "zoo ah"): our writer and reader, the Zoo 2.10 reference archive
/// (Zoo/ReferenceVectors), and Zoo 2.10 as a live oracle in both directions.
/// </summary>
[TestFixture]
public class ZooLzhTests {

  private static readonly byte[] FoxText =
    System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 50)));

  private static byte[] Noise(int length) {
    var b = new byte[length];
    var x = 0x12345678u;
    for (var i = 0; i < length; ++i) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; b[i] = i % 7 == 0 ? (byte)x : (byte)(i >> 6); }
    return b;
  }

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_zoolzh_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static byte[] CreateArchive(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var writer = new ZooWriter(ms, leaveOpen: true))
      foreach (var (name, data) in files)
        writer.AddEntry(name, data, ZooCompressionMethod.Lzh, new DateTime(2001, 2, 3, 4, 5, 6));
    return ms.ToArray();
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void GivenCompressibleData_WhenWrittenAsLzh_ThenMethod2WithExtractVersion21AndIdenticalBytes() {
    var archive = CreateArchive(("fox.txt", FoxText));

    using var reader = new ZooReader(new MemoryStream(archive));
    var e = reader.Entries.Single();
    Assert.That(e.CompressionMethod, Is.EqualTo(ZooCompressionMethod.Lzh));
    Assert.That((e.MajorVersion, e.MinorVersion), Is.EqualTo(((byte)2, (byte)1)));
    Assert.That(e.CompressedSize, Is.LessThan(FoxText.Length));
    Assert.That(reader.ExtractEntry(e), Is.EqualTo(FoxText));
  }

  [Test, Category("RoundTrip")]
  public void GivenDataSpanningManyBlocksAndWindows_WhenWrittenAsLzh_ThenRoundTrips() {
    // > 8 KiB window and > 16 Ki tokens per block, so several blocks and window wraps occur.
    var data = Noise(300_000);
    var archive = CreateArchive(("big.bin", data), ("fox.txt", FoxText));

    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(FoxText));
  }

  [Test, Category("Boundary")]
  public void GivenEmptyFile_WhenWrittenAsLzh_ThenStored() {
    var archive = CreateArchive(("empty.txt", []));

    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZooCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void GivenSingleByte_WhenWrittenAsLzh_ThenFallsBackToStore() {
    var archive = CreateArchive(("one.bin", [0x42]));

    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZooCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(new byte[] { 0x42 }));
  }

  [Test, Category("Exception")]
  public void GivenCorruptLzhPayload_WhenExtracted_ThenThrowsInsteadOfReturningWrongBytes() {
    var archive = CreateArchive(("fox.txt", FoxText));
    long dataOffset;
    using (var probe = new ZooReader(new MemoryStream(archive)))
      dataOffset = probe.Entries[0].DataOffset;
    archive[dataOffset + 3] ^= 0x5A;

    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(() => reader.ExtractEntry(reader.Entries[0]), Throws.Exception);
  }

  [Test, Category("ThemVsUs")]
  public void GivenZoo210ReferenceArchive_WhenRead_ThenLzhMembersDecodeToTheSourceFiles() {
    using var reader = new ZooReader(new MemoryStream(LoadVector("lzh-zoo210.zoo")));
    var text = reader.Entries.Single(e => e.EffectiveName == "text.txt");
    var runs = reader.Entries.Single(e => e.EffectiveName == "runs.txt");
    var hello = reader.Entries.Single(e => e.EffectiveName == "hello.txt");

    Assert.That(text.CompressionMethod, Is.EqualTo(ZooCompressionMethod.Lzh));
    Assert.That(runs.CompressionMethod, Is.EqualTo(ZooCompressionMethod.Lzh));
    Assert.That(reader.ExtractEntry(text), Is.EqualTo(FoxText));
    Assert.That(reader.ExtractEntry(runs), Is.EqualTo(Enumerable.Repeat((byte)'a', 20_000).ToArray()));
    Assert.That(reader.ExtractEntry(hello), Is.EqualTo("hello zoo\n"u8.ToArray()));
  }

  [Test, Category("ExternalInterop")]
  public void GivenOurLzhArchive_WhenZooTestsAndExtractsIt_ThenEveryByteMatches() {
    RequireZoo();
    var big = Noise(100_000);
    var path = Path.Combine(this._tmpDir, "ours.zoo");
    File.WriteAllBytes(path, CreateArchive(("fox.txt", FoxText), ("big.bin", big)));
    var dir = FsInteropToolbox.WinToWsl(this._tmpDir);

    var t = FsInteropToolbox.RunWsl($"cd '{dir}' && zoo -test ours.zoo");
    Assert.That(t.StdOut + t.StdErr, Does.Contain("Archive seems OK"), $"zoo -test:\n{t.StdOut}\n{t.StdErr}");

    var x = FsInteropToolbox.RunWsl($"mkdir -p '{dir}/out' && cd '{dir}/out' && zoo -extract ../ours.zoo");
    Assert.That(x.ExitCode, Is.EqualTo(0), x.StdOut + x.StdErr);
    Assert.That(File.Exists(Path.Combine(this._tmpDir, "out", "big.bin")), $"zoo -extract did not write big.bin:\n{x.StdOut}\n{x.StdErr}\n{t.StdOut}");
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmpDir, "out", "fox.txt")), Is.EqualTo(FoxText));
    Assert.That(File.ReadAllBytes(Path.Combine(this._tmpDir, "out", "big.bin")), Is.EqualTo(big));
  }

  [Test, Category("ExternalInterop")]
  public void GivenZooWritesLzh_WhenWeReadIt_ThenEveryByteMatches() {
    RequireZoo();
    var big = Noise(100_000);
    File.WriteAllBytes(Path.Combine(this._tmpDir, "fox.txt"), FoxText);
    File.WriteAllBytes(Path.Combine(this._tmpDir, "big.bin"), big);
    var dir = FsInteropToolbox.WinToWsl(this._tmpDir);

    var a = FsInteropToolbox.RunWsl($"cd '{dir}' && zoo ah theirs.zoo fox.txt big.bin");
    Assert.That(a.ExitCode, Is.EqualTo(0), a.StdOut + a.StdErr);

    using var reader = new ZooReader(File.OpenRead(Path.Combine(this._tmpDir, "theirs.zoo")));
    var fox = reader.Entries.Single(e => e.EffectiveName == "fox.txt");
    Assert.That(fox.CompressionMethod, Is.EqualTo(ZooCompressionMethod.Lzh));
    Assert.That(reader.ExtractEntry(fox), Is.EqualTo(FoxText));
    Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.EffectiveName == "big.bin")), Is.EqualTo(big));
  }

  private static void RequireZoo() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("zoo"))
      Assert.Ignore("Zoo 2.10 is not installed (Ubuntu 24.04 dropped the package; build it from source).");
  }

  private static byte[] LoadVector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ZooVectors.{name}")
      ?? throw new InvalidOperationException($"missing embedded vector {name}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
