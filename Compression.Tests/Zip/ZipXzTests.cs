using System.Reflection;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// ZIP method 95 (XZ, APPNOTE 4.4.5): our writer and reader, the 7-Zip 26.03 reference archive
/// (Zip/ReferenceVectors), and 7-Zip as a live oracle in both directions.
/// </summary>
[TestFixture]
public class ZipXzTests {

  private static readonly byte[] FoxText =
    System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 50)));

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_zipxz_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void GivenCompressibleData_WhenWrittenAsXz_ThenReadsBackIdenticalWithMethod95() {
    var archive = CreateArchive(("fox.txt", FoxText));

    using var reader = new ZipReader(new MemoryStream(archive));
    var entry = reader.Entries.Single();
    Assert.That((ushort)entry.CompressionMethod, Is.EqualTo(95));
    Assert.That(entry.CompressedSize, Is.LessThan(FoxText.Length));
    Assert.That(reader.ExtractEntry(entry), Is.EqualTo(FoxText));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void GivenSeveralEntries_WhenWrittenAsXz_ThenEachEntryCarriesItsOwnXzStream() {
    var a = FoxText;
    var b = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7 % 13)).ToArray();
    var archive = CreateArchive(("a.txt", a), ("b.bin", b));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(a));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(b));
  }

  [Test, Category("Boundary")]
  public void GivenEmptyFile_WhenWrittenAsXz_ThenStoredBecauseXzWouldBeLarger() {
    var archive = CreateArchive(("empty.txt", []));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.Empty);
  }

  [Test, Category("Boundary")]
  public void GivenSingleByte_WhenWrittenAsXz_ThenFallsBackToStore() {
    var archive = CreateArchive(("one.bin", [0x42]));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(new byte[] { 0x42 }));
  }

  [Test, Category("Exception")]
  public void GivenCorruptXzPayload_WhenExtracted_ThenThrowsInsteadOfReturningWrongBytes() {
    var archive = CreateArchive(("fox.txt", FoxText));
    using (var probe = new ZipReader(new MemoryStream(archive))) {
      // Damage a byte inside the compressed payload (past the 30-byte local header + name).
      var dataStart = (int)probe.Entries[0].LocalHeaderOffset + 30 + "fox.txt".Length;
      archive[dataStart + 20] ^= 0xFF;
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(() => reader.ExtractEntry(reader.Entries[0]), Throws.InstanceOf<Exception>());
  }

  [Test, Category("ThemVsUs")]
  public void GivenSevenZipReferenceArchive_WhenRead_ThenXzEntryDecodesToTheSourceText() {
    using var reader = new ZipReader(new MemoryStream(LoadVector("xz-7zip2603.zip")));
    var text = reader.Entries.Single(e => e.FileName == "text.txt");
    var hello = reader.Entries.Single(e => e.FileName == "hello.txt");

    Assert.That(text.CompressionMethod, Is.EqualTo(ZipCompressionMethod.Xz));
    Assert.That(text.Crc32, Is.EqualTo(0x5FE03AFBu));
    Assert.That(reader.ExtractEntry(text), Is.EqualTo(FoxText));
    Assert.That(reader.ExtractEntry(hello), Is.EqualTo("hello xz\n"u8.ToArray()));
  }

  [Test, Category("ExternalInterop")]
  public void GivenOurXzArchive_WhenSevenZipTestsAndExtractsIt_ThenEveryByteMatches() {
    FsInteropToolbox.Require7z();
    var b = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7 % 13)).ToArray();
    var path = Path.Combine(this._tmpDir, "ours.zip");
    File.WriteAllBytes(path, CreateArchive(("fox.txt", FoxText), ("b.bin", b)));

    var t = FsInteropToolbox.Run7z($"t \"{path}\"");
    Assert.That(t.ExitCode, Is.EqualTo(0), $"7z t rejected the archive:\n{t.StdOut}\n{t.StdErr}");
    var l = FsInteropToolbox.Run7z($"l -slt \"{path}\"");
    Assert.That(l.StdOut, Does.Contain("Method = xz"));

    var outDir = Path.Combine(this._tmpDir, "out");
    var x = FsInteropToolbox.Run7z($"x -y \"-o{outDir}\" \"{path}\"");
    Assert.That(x.ExitCode, Is.EqualTo(0), x.StdOut + x.StdErr);
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "fox.txt")), Is.EqualTo(FoxText));
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "b.bin")), Is.EqualTo(b));
  }

  [Test, Category("ExternalInterop")]
  public void GivenSevenZipWritesXzZip_WhenWeReadIt_ThenEveryByteMatches() {
    FsInteropToolbox.Require7z();
    var src = Path.Combine(this._tmpDir, "src");
    Directory.CreateDirectory(src);
    var b = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 % 251)).ToArray();
    File.WriteAllBytes(Path.Combine(src, "fox.txt"), FoxText);
    File.WriteAllBytes(Path.Combine(src, "b.bin"), b);
    var path = Path.Combine(this._tmpDir, "theirs.zip");

    var a = FsInteropToolbox.Run7z($"a -tzip -mm=xz \"{path}\" \"{Path.Combine(src, "fox.txt")}\" \"{Path.Combine(src, "b.bin")}\"");
    if (a.ExitCode != 0)
      Assert.Ignore($"this 7-Zip cannot write ZIP method 95:\n{a.StdOut}\n{a.StdErr}");

    using var reader = new ZipReader(File.OpenRead(path));
    Assert.That(reader.Entries.Select(e => e.CompressionMethod), Has.All.EqualTo(ZipCompressionMethod.Xz));
    Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "fox.txt")), Is.EqualTo(FoxText));
    Assert.That(reader.ExtractEntry(reader.Entries.Single(e => e.FileName == "b.bin")), Is.EqualTo(b));
  }

  private static byte[] CreateArchive(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true))
      foreach (var (name, data) in files)
        writer.AddEntry(name, data, ZipCompressionMethod.Xz);
    return ms.ToArray();
  }

  private static byte[] LoadVector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ZipVectors.{name}")
      ?? throw new InvalidOperationException($"missing embedded vector {name}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
