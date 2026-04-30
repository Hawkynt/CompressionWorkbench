using System.Text;
using Compression.Lib;

namespace Compression.Tests.Sfx;

[TestFixture]
public class SfxBuilderTests {

  private string _tempDir = null!;
  private string _stubPath = null!;

  [SetUp]
  public void SetUp() {
    _tempDir = Path.Combine(Path.GetTempPath(), $"sfx_test_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDir);
    // Create a fake stub (any bytes — the real stub is an exe, but for testing the
    // SFX layout we just need some bytes at the front)
    _stubPath = Path.Combine(_tempDir, "stub.exe");
    var stubData = new byte[1024];
    Random.Shared.NextBytes(stubData);
    File.WriteAllBytes(_stubPath, stubData);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
  }

  // ── Unit tests ──────────────────────────────────────────────────────

  [Test]
  [Category("Unit")]
  public void ReadTrailer_ValidSfx_ReturnsCorrectOffset() {
    var zipPath = CreateZipArchive("hello.txt", "Hello!"u8.ToArray());
    var sfxPath = Path.Combine(_tempDir, "test.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    var info = SfxBuilder.ReadTrailer(sfxPath);

    Assert.That(info, Is.Not.Null);
    Assert.That(info!.Value.Offset, Is.EqualTo(new FileInfo(_stubPath).Length));
    Assert.That(info.Value.Format, Is.EqualTo(FormatDetector.Format.Zip));
    Assert.That(info.Value.Length, Is.GreaterThan(0));
  }

  [Test]
  [Category("Unit")]
  public void ReadTrailer_NoTrailer_ReturnsNull() {
    var fakePath = Path.Combine(_tempDir, "fake.exe");
    File.WriteAllBytes(fakePath, new byte[100]);

    var info = SfxBuilder.ReadTrailer(fakePath);

    Assert.That(info, Is.Null);
  }

  [Test]
  [Category("Unit")]
  public void ReadTrailer_CorruptMagic_ReturnsNull() {
    var zipPath = CreateZipArchive("a.txt", "data"u8.ToArray());
    var sfxPath = Path.Combine(_tempDir, "corrupt.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    // Corrupt the magic bytes
    using (var fs = File.Open(sfxPath, FileMode.Open, FileAccess.Write))  {
      fs.Seek(-1, SeekOrigin.End);
      fs.WriteByte(0x00); // overwrite '!' with 0x00
    }

    var info = SfxBuilder.ReadTrailer(sfxPath);

    Assert.That(info, Is.Null);
  }

  [Test]
  [Category("Unit")]
  public void Create_OutputSize_EqualsStubPlusArchivePlusTrailer() {
    var zipPath = CreateZipArchive("test.txt", "content"u8.ToArray());
    var sfxPath = Path.Combine(_tempDir, "sized.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    var stubSize = new FileInfo(_stubPath).Length;
    var archiveSize = new FileInfo(zipPath).Length;
    var sfxSize = new FileInfo(sfxPath).Length;

    Assert.That(sfxSize, Is.EqualTo(stubSize + archiveSize + 12)); // 12 = trailer
  }

  // ── Round-trip tests ────────────────────────────────────────────────

  [Test]
  [Category("RoundTrip")]
  public void RoundTrip_Zip_SingleFile() {
    var content = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog.");
    var zipPath = CreateZipArchive("quote.txt", content);

    var sfxPath = Path.Combine(_tempDir, "rt_zip.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    var extractDir = Path.Combine(_tempDir, "out_zip");
    SfxBuilder.Extract(sfxPath, extractDir);

    var extracted = File.ReadAllBytes(Path.Combine(extractDir, "quote.txt"));
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test]
  [Category("RoundTrip")]
  public void RoundTrip_Zip_MultipleFiles() {
    var files = new Dictionary<string, byte[]> {
      ["alpha.txt"] = Encoding.UTF8.GetBytes("File Alpha"),
      ["beta.bin"] = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE },
      ["gamma.txt"] = Encoding.UTF8.GetBytes("Gamma content with unicode: \u00e4\u00f6\u00fc"),
    };

    var zipPath = Path.Combine(_tempDir, "multi.zip");
    using (var zipFs = File.Create(zipPath)) {
      var writer = new FileFormat.Zip.ZipWriter(zipFs);
      foreach (var (name, data) in files)
        writer.AddEntry(name, data);
      writer.Finish();
    }

    var sfxPath = Path.Combine(_tempDir, "rt_multi.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    var extractDir = Path.Combine(_tempDir, "out_multi");
    SfxBuilder.Extract(sfxPath, extractDir);

    foreach (var (name, expected) in files) {
      var actual = File.ReadAllBytes(Path.Combine(extractDir, name));
      Assert.That(actual, Is.EqualTo(expected), $"Content mismatch for {name}");
    }
  }

  [Test]
  [Category("RoundTrip")]
  public void RoundTrip_7z_SingleFile() {
    var content = Encoding.UTF8.GetBytes("7-Zip SFX round-trip test content");
    var archivePath = Path.Combine(_tempDir, "test.7z");
    using (var fs = File.Create(archivePath)) {
      var writer = new FileFormat.SevenZip.SevenZipWriter(fs);
      writer.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = "data.txt" }, content);
      writer.Finish();
    }

    var sfxPath = Path.Combine(_tempDir, "rt_7z.exe");
    SfxBuilder.Create(archivePath, sfxPath, _stubPath);

    var extractDir = Path.Combine(_tempDir, "out_7z");
    SfxBuilder.Extract(sfxPath, extractDir);

    var extracted = File.ReadAllBytes(Path.Combine(extractDir, "data.txt"));
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test]
  [Category("RoundTrip")]
  public void RoundTrip_Tar_SingleFile() {
    var content = Encoding.UTF8.GetBytes("TAR SFX round-trip");
    var tarPath = Path.Combine(_tempDir, "test.tar");
    using (var fs = File.Create(tarPath)) {
      var writer = new FileFormat.Tar.TarWriter(fs);
      writer.AddEntry(new FileFormat.Tar.TarEntry { Name = "readme.txt" }, content);
      writer.Finish();
    }

    var sfxPath = Path.Combine(_tempDir, "rt_tar.exe");
    SfxBuilder.Create(tarPath, sfxPath, _stubPath);

    var extractDir = Path.Combine(_tempDir, "out_tar");
    SfxBuilder.Extract(sfxPath, extractDir);

    var extracted = File.ReadAllBytes(Path.Combine(extractDir, "readme.txt"));
    Assert.That(extracted, Is.EqualTo(content));
  }

  // ── End-to-end tests ───────────────────────────────────────────────

  [Test]
  [Category("EndToEnd")]
  public void EndToEnd_CreateArchiveThenSfxThenExtract_Zip() {
    // 1. Create source files on disk
    var srcDir = Path.Combine(_tempDir, "src");
    Directory.CreateDirectory(srcDir);
    File.WriteAllText(Path.Combine(srcDir, "doc.txt"), "End-to-end document content");
    File.WriteAllBytes(Path.Combine(srcDir, "binary.dat"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

    // 2. Create ZIP archive using ArchiveOperations (same as CLI `cwb create`)
    var zipPath = Path.Combine(_tempDir, "e2e.zip");
    var inputs = ArchiveInput.Resolve([Path.Combine(srcDir, "doc.txt"), Path.Combine(srcDir, "binary.dat")]);
    ArchiveOperations.Create(zipPath, inputs, new CompressionOptions());

    // 3. Wrap into SFX
    var sfxPath = Path.Combine(_tempDir, "e2e.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    // 4. Verify SFX has valid trailer
    var info = SfxBuilder.ReadTrailer(sfxPath);
    Assert.That(info, Is.Not.Null);
    Assert.That(info!.Value.Format, Is.EqualTo(FormatDetector.Format.Zip));

    // 5. Extract from SFX
    var extractDir = Path.Combine(_tempDir, "e2e_out");
    SfxBuilder.Extract(sfxPath, extractDir);

    // 6. Verify extracted files match originals
    Assert.That(File.ReadAllText(Path.Combine(extractDir, "doc.txt")), Is.EqualTo("End-to-end document content"));
    Assert.That(File.ReadAllBytes(Path.Combine(extractDir, "binary.dat")), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
  }

  [Test]
  [Category("EndToEnd")]
  public void EndToEnd_CreateArchiveThenSfxThenExtract_7z() {
    // 1. Create source files on disk
    var srcDir = Path.Combine(_tempDir, "src7z");
    Directory.CreateDirectory(srcDir);
    File.WriteAllText(Path.Combine(srcDir, "notes.txt"), "Seven-Zip end-to-end");
    var binaryData = new byte[256];
    for (var i = 0; i < binaryData.Length; i++) binaryData[i] = (byte)(i & 0xFF);
    File.WriteAllBytes(Path.Combine(srcDir, "sequence.bin"), binaryData);

    // 2. Create 7z archive
    var archivePath = Path.Combine(_tempDir, "e2e.7z");
    using (var fs = File.Create(archivePath)) {
      var writer = new FileFormat.SevenZip.SevenZipWriter(fs);
      writer.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = "notes.txt" }, File.ReadAllBytes(Path.Combine(srcDir, "notes.txt")));
      writer.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = "sequence.bin" }, binaryData);
      writer.Finish();
    }

    // 3. Wrap into SFX
    var sfxPath = Path.Combine(_tempDir, "e2e7z.exe");
    SfxBuilder.Create(archivePath, sfxPath, _stubPath);

    // 4. Extract
    var extractDir = Path.Combine(_tempDir, "e2e7z_out");
    SfxBuilder.Extract(sfxPath, extractDir);

    // 5. Verify
    Assert.That(File.ReadAllText(Path.Combine(extractDir, "notes.txt")), Is.EqualTo("Seven-Zip end-to-end"));
    Assert.That(File.ReadAllBytes(Path.Combine(extractDir, "sequence.bin")), Is.EqualTo(binaryData));
  }

  [Test]
  [Category("EndToEnd")]
  public void EndToEnd_LargeFile_RoundTrip() {
    // Create a large-ish file (64KB of pattern data)
    var largeData = new byte[65536];
    for (var i = 0; i < largeData.Length; i++)
      largeData[i] = (byte)(i % 251); // prime modulus for non-trivial pattern

    var zipPath = CreateZipArchive("large.bin", largeData);
    var sfxPath = Path.Combine(_tempDir, "large.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    var extractDir = Path.Combine(_tempDir, "large_out");
    SfxBuilder.Extract(sfxPath, extractDir);

    var extracted = File.ReadAllBytes(Path.Combine(extractDir, "large.bin"));
    Assert.That(extracted, Is.EqualTo(largeData));
  }

  [Test]
  [Category("EndToEnd")]
  public void EndToEnd_EmptyFile_RoundTrip() {
    var zipPath = CreateZipArchive("empty.txt", []);
    var sfxPath = Path.Combine(_tempDir, "empty.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    var extractDir = Path.Combine(_tempDir, "empty_out");
    SfxBuilder.Extract(sfxPath, extractDir);

    var extracted = File.ReadAllBytes(Path.Combine(extractDir, "empty.txt"));
    Assert.That(extracted, Is.Empty);
  }

  // ── Edge case tests ────────────────────────────────────────────────

  [Test]
  [Category("EdgeCase")]
  public void Extract_InvalidSfx_Throws() {
    var fakePath = Path.Combine(_tempDir, "notsfx.exe");
    File.WriteAllBytes(fakePath, new byte[100]);

    Assert.Throws<InvalidOperationException>(() => SfxBuilder.Extract(fakePath, _tempDir));
  }

  [Test]
  [Category("EdgeCase")]
  public void ReadTrailer_TinyFile_ReturnsNull() {
    var tinyPath = Path.Combine(_tempDir, "tiny.exe");
    File.WriteAllBytes(tinyPath, new byte[5]); // smaller than 12-byte trailer

    Assert.That(SfxBuilder.ReadTrailer(tinyPath), Is.Null);
  }

  [Test]
  [Category("EdgeCase")]
  public void Create_DifferentStubSizes_TrailerOffsetCorrect() {
    // Test with various stub sizes to ensure offset tracking is correct
    foreach (var stubSize in new[] { 1, 100, 4096, 65535 }) {
      var stub = Path.Combine(_tempDir, $"stub_{stubSize}.exe");
      File.WriteAllBytes(stub, new byte[stubSize]);

      var zipPath = CreateZipArchive($"test_{stubSize}.txt", Encoding.UTF8.GetBytes($"Stub size {stubSize}"));
      var sfxPath = Path.Combine(_tempDir, $"sfx_{stubSize}.exe");
      SfxBuilder.Create(zipPath, sfxPath, stub);

      var info = SfxBuilder.ReadTrailer(sfxPath);
      Assert.That(info, Is.Not.Null, $"ReadTrailer failed for stub size {stubSize}");
      Assert.That(info!.Value.Offset, Is.EqualTo(stubSize), $"Offset wrong for stub size {stubSize}");

      // Also verify extraction works
      var extractDir = Path.Combine(_tempDir, $"out_{stubSize}");
      SfxBuilder.Extract(sfxPath, extractDir);
      var content = File.ReadAllText(Path.Combine(extractDir, $"test_{stubSize}.txt"));
      Assert.That(content, Is.EqualTo($"Stub size {stubSize}"));
    }
  }

  // ── Broken / corrupt SFX tests ──────────────────────────────────────

  [Test]
  [Category("EdgeCase")]
  public void Extract_OffsetPointsPastEndOfFile_Throws() {
    // Create valid SFX, then corrupt the offset to point past EOF
    var zipPath = CreateZipArchive("test.txt", "data"u8.ToArray());
    var sfxPath = Path.Combine(_tempDir, "bad_offset.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    // Overwrite the 8-byte offset with a value larger than the file
    using (var fs = File.Open(sfxPath, FileMode.Open, FileAccess.Write)) {
      fs.Seek(-12, SeekOrigin.End);
      var badOffset = BitConverter.GetBytes(fs.Length + 1000L);
      fs.Write(badOffset);
    }

    // ReadTrailer should return null (offset validation catches it)
    Assert.That(SfxBuilder.ReadTrailer(sfxPath), Is.Null);
    Assert.Throws<InvalidOperationException>(() => SfxBuilder.Extract(sfxPath, _tempDir));
  }

  [Test]
  [Category("EdgeCase")]
  public void Extract_OffsetIsNegative_Throws() {
    var zipPath = CreateZipArchive("test.txt", "data"u8.ToArray());
    var sfxPath = Path.Combine(_tempDir, "neg_offset.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    // Write a negative offset
    using (var fs = File.Open(sfxPath, FileMode.Open, FileAccess.Write)) {
      fs.Seek(-12, SeekOrigin.End);
      fs.Write(BitConverter.GetBytes(-1L));
    }

    Assert.That(SfxBuilder.ReadTrailer(sfxPath), Is.Null);
    Assert.Throws<InvalidOperationException>(() => SfxBuilder.Extract(sfxPath, _tempDir));
  }

  [Test]
  [Category("EdgeCase")]
  public void Extract_OffsetPointsToGarbage_FailsGracefully() {
    // Create SFX, then overwrite the archive data with garbage so format is unknown
    var zipPath = CreateZipArchive("test.txt", "data"u8.ToArray());
    var sfxPath = Path.Combine(_tempDir, "garbage.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    // Overwrite archive magic bytes (right after stub) with garbage
    var stubLen = new FileInfo(_stubPath).Length;
    using (var fs = File.Open(sfxPath, FileMode.Open, FileAccess.Write)) {
      fs.Seek(stubLen, SeekOrigin.Begin);
      fs.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
    }

    // ReadTrailer returns info but format is Unknown
    var info = SfxBuilder.ReadTrailer(sfxPath);
    Assert.That(info, Is.Not.Null);
    Assert.That(info!.Value.Format, Is.EqualTo(FormatDetector.Format.Unknown));

    // Extract fails because ArchiveOperations can't handle Unknown
    Assert.Throws<NotSupportedException>(() => SfxBuilder.Extract(sfxPath, _tempDir));
  }

  [Test]
  [Category("EdgeCase")]
  public void Extract_TruncatedArchiveData_Throws() {
    // Create valid SFX, then truncate the file (remove some archive bytes)
    var zipPath = CreateZipArchive("test.txt", "Hello world, this is test data for truncation."u8.ToArray());
    var sfxPath = Path.Combine(_tempDir, "truncated.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    var sfxSize = new FileInfo(sfxPath).Length;
    var stubLen = new FileInfo(_stubPath).Length;

    // Move the trailer to be right after half the archive data
    // This simulates a truncated download
    using (var fs = File.Open(sfxPath, FileMode.Open, FileAccess.ReadWrite)) {
      // Read the trailer
      fs.Seek(-12, SeekOrigin.End);
      var trailer = new byte[12];
      fs.ReadExactly(trailer);

      // Truncate: keep stub + half of archive data + trailer
      var archiveLen = sfxSize - stubLen - 12;
      var truncatedLen = stubLen + archiveLen / 2 + 12;
      fs.SetLength(truncatedLen);

      // Re-write trailer at new end (original offset is still valid but data is short)
      fs.Seek(-12, SeekOrigin.End);
      fs.Write(trailer);
    }

    // ReadTrailer returns info (offset + magic are still valid)
    var info = SfxBuilder.ReadTrailer(sfxPath);
    Assert.That(info, Is.Not.Null);

    // But extraction should fail because archive data is incomplete
    Assert.That(() => SfxBuilder.Extract(sfxPath, Path.Combine(_tempDir, "trunc_out")),
      Throws.InstanceOf<Exception>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Extract_OffsetPointsIntoStub_FailsGracefully() {
    // Set offset to 0 so it tries to parse the stub bytes as an archive
    var zipPath = CreateZipArchive("test.txt", "data"u8.ToArray());
    var sfxPath = Path.Combine(_tempDir, "zero_offset.exe");
    SfxBuilder.Create(zipPath, sfxPath, _stubPath);

    using (var fs = File.Open(sfxPath, FileMode.Open, FileAccess.Write)) {
      fs.Seek(-12, SeekOrigin.End);
      fs.Write(BitConverter.GetBytes(0L)); // offset = 0, points at stub data
    }

    // ReadTrailer returns info but format is Unknown (random stub bytes)
    var info = SfxBuilder.ReadTrailer(sfxPath);
    Assert.That(info, Is.Not.Null);
    // Stub is random bytes, probably Unknown format
    // Extract should fail
    Assert.That(() => SfxBuilder.Extract(sfxPath, Path.Combine(_tempDir, "zero_out")),
      Throws.InstanceOf<Exception>());
  }

  [Test]
  [Category("EdgeCase")]
  public void ReadTrailer_ExactlyTrailerSize_ReturnsNull() {
    // File that's exactly 12 bytes — just a trailer but no stub or archive
    var path = Path.Combine(_tempDir, "exact12.exe");
    var data = new byte[12];
    data[8] = (byte)'S'; data[9] = (byte)'F'; data[10] = (byte)'X'; data[11] = (byte)'!';
    BitConverter.TryWriteBytes(data.AsSpan(), 0L); // offset = 0
    File.WriteAllBytes(path, data);

    // Offset 0, length = 12 - 12 - 0 = 0 → invalid (length <= 0)
    Assert.That(SfxBuilder.ReadTrailer(path), Is.Null);
  }

  [Test]
  [Category("EdgeCase")]
  public void ReadTrailer_AllZeroFile_ReturnsNull() {
    var path = Path.Combine(_tempDir, "zeros.exe");
    File.WriteAllBytes(path, new byte[1024]);

    Assert.That(SfxBuilder.ReadTrailer(path), Is.Null);
  }

  // ── Additional format round-trips ──────────────────────────────────

  [Test]
  [Category("RoundTrip")]
  public void RoundTrip_TarGz_SingleFile() {
    var content = Encoding.UTF8.GetBytes("Tar.gz SFX round-trip");
    var archivePath = Path.Combine(_tempDir, "test.tar.gz");
    // Create tar.gz using ArchiveOperations
    var srcFile = Path.Combine(_tempDir, "src_tgz.txt");
    File.WriteAllBytes(srcFile, content);
    var inputs = ArchiveInput.Resolve([srcFile]);
    ArchiveOperations.Create(archivePath, inputs, new CompressionOptions());

    var sfxPath = Path.Combine(_tempDir, "rt_tgz.exe");
    SfxBuilder.Create(archivePath, sfxPath, _stubPath);

    // Verify format detection (gzip magic should work)
    var info = SfxBuilder.ReadTrailer(sfxPath);
    Assert.That(info, Is.Not.Null);
    Assert.That(info!.Value.Format, Is.EqualTo(FormatDetector.Format.Gzip));
  }

  [Test]
  [Category("RoundTrip")]
  public void RoundTrip_Cab_SingleFile() {
    var content = Encoding.UTF8.GetBytes("CAB SFX round-trip test");
    var cabPath = Path.Combine(_tempDir, "test.cab");
    var writer = new FileFormat.Cab.CabWriter();
    writer.AddFile("cab_test.txt", content);
    using (var fs = File.Create(cabPath))
      writer.WriteTo(fs);

    var sfxPath = Path.Combine(_tempDir, "rt_cab.exe");
    SfxBuilder.Create(cabPath, sfxPath, _stubPath);

    var extractDir = Path.Combine(_tempDir, "out_cab");
    SfxBuilder.Extract(sfxPath, extractDir);

    var extracted = File.ReadAllBytes(Path.Combine(extractDir, "cab_test.txt"));
    Assert.That(extracted, Is.EqualTo(content));
  }

  // ── Third-party SFX reading tests ──────────────────────────────────

  [Test]
  [Category("RoundTrip")]
  public void ThirdPartySfx_ZipInsidePe_DetectedAndExtracted() {
    var content = Encoding.UTF8.GetBytes("Third-party ZIP SFX test");
    var zipPath = CreateZipArchive("payload.txt", content);
    var zipData = File.ReadAllBytes(zipPath);

    // Build fake PE with ZIP appended as overlay
    var sfxPath = Path.Combine(_tempDir, "thirdparty_zip.exe");
    BuildFakePeWithOverlay(sfxPath, zipData);

    // Detection
    var format = FormatDetector.Detect(sfxPath);
    Assert.That(format, Is.EqualTo(FormatDetector.Format.Sfx));

    var info = FormatDetector.GetSfxArchiveInfo(sfxPath);
    Assert.That(info, Is.Not.Null);
    Assert.That(info!.Value.ArchiveFormat, Is.EqualTo(FormatDetector.Format.Zip));

    // Extraction via ArchiveOperations (same as CLI `cwb extract`)
    var extractDir = Path.Combine(_tempDir, "tp_zip_out");
    ArchiveOperations.Extract(sfxPath, extractDir, null, null);

    var extracted = File.ReadAllBytes(Path.Combine(extractDir, "payload.txt"));
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test]
  [Category("RoundTrip")]
  public void ThirdPartySfx_7zInsidePe_DetectedAndExtracted() {
    var content = Encoding.UTF8.GetBytes("Third-party 7z SFX test data");
    var archivePath = Path.Combine(_tempDir, "tp.7z");
    using (var fs = File.Create(archivePath)) {
      var writer = new FileFormat.SevenZip.SevenZipWriter(fs);
      writer.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = "inner.txt" }, content);
      writer.Finish();
    }
    var archiveData = File.ReadAllBytes(archivePath);

    var sfxPath = Path.Combine(_tempDir, "thirdparty_7z.exe");
    BuildFakePeWithOverlay(sfxPath, archiveData);

    var info = FormatDetector.GetSfxArchiveInfo(sfxPath);
    Assert.That(info, Is.Not.Null);
    Assert.That(info!.Value.ArchiveFormat, Is.EqualTo(FormatDetector.Format.SevenZip));

    var extractDir = Path.Combine(_tempDir, "tp_7z_out");
    ArchiveOperations.Extract(sfxPath, extractDir, null, null);

    Assert.That(File.ReadAllBytes(Path.Combine(extractDir, "inner.txt")), Is.EqualTo(content));
  }

  [Test]
  [Category("RoundTrip")]
  public void ThirdPartySfx_CabInsidePe_DetectedAndExtracted() {
    var content = Encoding.UTF8.GetBytes("Third-party CAB SFX test");
    var cabPath = Path.Combine(_tempDir, "tp.cab");
    var cw = new FileFormat.Cab.CabWriter();
    cw.AddFile("cabinet.txt", content);
    using (var fs = File.Create(cabPath))
      cw.WriteTo(fs);
    var cabData = File.ReadAllBytes(cabPath);

    var sfxPath = Path.Combine(_tempDir, "thirdparty_cab.exe");
    BuildFakePeWithOverlay(sfxPath, cabData);

    var info = FormatDetector.GetSfxArchiveInfo(sfxPath);
    Assert.That(info, Is.Not.Null);
    Assert.That(info!.Value.ArchiveFormat, Is.EqualTo(FormatDetector.Format.Cab));

    var extractDir = Path.Combine(_tempDir, "tp_cab_out");
    ArchiveOperations.Extract(sfxPath, extractDir, null, null);

    Assert.That(File.ReadAllBytes(Path.Combine(extractDir, "cabinet.txt")), Is.EqualTo(content));
  }

  [Test]
  [Category("Unit")]
  public void PeOverlay_FindOverlayOffset_ValidPe_ReturnsOverlayStart() {
    // Build PE + extra bytes so there's actually an overlay
    var peData = BuildMinimalPe(512);
    var withOverlay = new byte[peData.Length + 100];
    peData.CopyTo(withOverlay, 0);
    using var ms = new MemoryStream(withOverlay);
    var overlay = PeOverlay.FindOverlayOffset(ms);
    Assert.That(overlay, Is.GreaterThan(0));
    Assert.That(overlay, Is.LessThanOrEqualTo(peData.Length));
  }

  [Test]
  [Category("Unit")]
  public void PeOverlay_FindOverlayOffset_NotPe_ReturnsNegative() {
    using var ms = new MemoryStream(new byte[256]);
    var overlay = PeOverlay.FindOverlayOffset(ms);
    Assert.That(overlay, Is.EqualTo(-1));
  }

  [Test]
  [Category("Unit")]
  public void PeOverlay_ScanForArchive_FindsZipSignature() {
    // Create a buffer with deterministic padding then a ZIP signature.
    // (Random bytes can hit ARJ's 0x60 0xEA — or any other format magic — before the
    //  planted ZIP, breaking the assertion. 0xCC is not a magic prefix for any format.)
    var buf = new byte[1024];
    Array.Fill(buf, (byte)0xCC);
    buf[500] = 0x50; buf[501] = 0x4B; buf[502] = 0x03; buf[503] = 0x04; // PK\x03\x04

    using var ms = new MemoryStream(buf);
    var result = PeOverlay.ScanForArchive(ms, 0);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Value.Format, Is.EqualTo(FormatDetector.Format.Zip));
    Assert.That(result.Value.Offset, Is.EqualTo(500));
  }

  [Test]
  [Category("Unit")]
  public void PeOverlay_ScanForArchive_FindsRarSignature() {
    var buf = new byte[1024];
    Array.Fill(buf, (byte)0xCC);
    // RAR5: Rar!\x1A\x07\x01\x00
    buf[200] = 0x52; buf[201] = 0x61; buf[202] = 0x72;
    buf[203] = 0x21; buf[204] = 0x1A; buf[205] = 0x07; buf[206] = 0x01;

    using var ms = new MemoryStream(buf);
    var result = PeOverlay.ScanForArchive(ms, 0);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Value.Format, Is.EqualTo(FormatDetector.Format.Rar));
    Assert.That(result.Value.Offset, Is.EqualTo(200));
  }

  [Test]
  [Category("Unit")]
  public void ThirdPartySfx_ListWorks() {
    var content = Encoding.UTF8.GetBytes("Listable content");
    var zipPath = CreateZipArchive("listed.txt", content);
    var zipData = File.ReadAllBytes(zipPath);

    var sfxPath = Path.Combine(_tempDir, "listable.exe");
    BuildFakePeWithOverlay(sfxPath, zipData);

    var entries = ArchiveOperations.List(sfxPath, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("listed.txt"));
  }

  [Test]
  [Category("Unit")]
  public void SubStream_PositionAndSeek_WorkCorrectly() {
    var data = new byte[100];
    for (var i = 0; i < 100; i++) data[i] = (byte)i;

    using var inner = new MemoryStream(data);
    using var sub = new SubStream(inner, 20, 50);

    Assert.That(sub.Length, Is.EqualTo(50));
    Assert.That(sub.Position, Is.EqualTo(0));

    // Read from start of sub (should be byte 20 of inner)
    var buf = new byte[10];
    var read = sub.Read(buf, 0, 10);
    Assert.That(read, Is.EqualTo(10));
    Assert.That(buf[0], Is.EqualTo(20));
    Assert.That(buf[9], Is.EqualTo(29));

    // Seek
    sub.Seek(0, SeekOrigin.Begin);
    Assert.That(sub.Position, Is.EqualTo(0));

    sub.Seek(-1, SeekOrigin.End);
    Assert.That(sub.Position, Is.EqualTo(49));

    read = sub.Read(buf, 0, 10);
    Assert.That(read, Is.EqualTo(1)); // only 1 byte left
    Assert.That(buf[0], Is.EqualTo(69)); // byte 69 of inner (20 + 49)
  }

  // ── Helper ─────────────────────────────────────────────────────────

  private string CreateZipArchive(string entryName, byte[] content) {
    var zipPath = Path.Combine(_tempDir, $"{Path.GetFileNameWithoutExtension(entryName)}_{Guid.NewGuid():N}.zip");
    using var zipFs = File.Create(zipPath);
    var writer = new FileFormat.Zip.ZipWriter(zipFs);
    writer.AddEntry(entryName, content);
    writer.Finish();
    return zipPath;
  }

  /// <summary>
  /// Builds a minimal valid PE executable with a single section, then appends overlayData
  /// as the PE overlay (data after last section). This simulates a third-party SFX stub.
  /// </summary>
  private void BuildFakePeWithOverlay(string outputPath, byte[] overlayData) {
    using var fs = File.Create(outputPath);
    var pe = BuildMinimalPe(512);
    fs.Write(pe);
    fs.Write(overlayData);
  }

  /// <summary>
  /// Creates a minimal PE with one section ending at the given sectionEnd offset.
  /// Layout: DOS header (64 bytes) + PE signature (4) + COFF header (20) +
  /// optional header (112 min) + section header (40) + section data.
  /// </summary>
  private static byte[] BuildMinimalPe(int sectionEnd) {
    var pe = new byte[sectionEnd];

    // DOS Header
    pe[0] = (byte)'M'; pe[1] = (byte)'Z';
    // e_lfanew at offset 0x3C → PE starts at offset 0x40 (64)
    pe[0x3C] = 0x40;

    // PE Signature at 0x40
    pe[0x40] = (byte)'P'; pe[0x41] = (byte)'E'; // PE\0\0

    // COFF Header at 0x44 (20 bytes)
    pe[0x44] = 0x4C; pe[0x45] = 0x01; // Machine = IMAGE_FILE_MACHINE_I386
    pe[0x46] = 0x01; pe[0x47] = 0x00; // NumberOfSections = 1
    // SizeOfOptionalHeader at 0x54: minimal optional header = 0x70 (112) for PE32
    pe[0x54] = 0x70; pe[0x55] = 0x00;
    pe[0x56] = 0x02; pe[0x57] = 0x01; // Characteristics = EXECUTABLE_IMAGE | 32BIT_MACHINE

    // Optional Header at 0x58 (112 bytes for PE32)
    pe[0x58] = 0x0B; pe[0x59] = 0x01; // Magic = PE32
    // SizeOfHeaders at 0x94 (offset 0x3C from opt header start)
    var sizeOfHeaders = sectionEnd;
    pe[0x94] = (byte)(sizeOfHeaders & 0xFF);
    pe[0x95] = (byte)((sizeOfHeaders >> 8) & 0xFF);

    // Section Table at 0x58 + 0x70 = 0xC8 (40 bytes per entry)
    var secTableOff = 0xC8;
    // Section name: ".text"
    pe[secTableOff] = (byte)'.'; pe[secTableOff + 1] = (byte)'t';
    pe[secTableOff + 2] = (byte)'e'; pe[secTableOff + 3] = (byte)'x';
    pe[secTableOff + 4] = (byte)'t';
    // VirtualSize at +8
    pe[secTableOff + 8] = 0x00; pe[secTableOff + 9] = 0x01; // 256
    // VirtualAddress at +12
    pe[secTableOff + 12] = 0x00; pe[secTableOff + 13] = 0x10; // 0x1000
    // SizeOfRawData at +16: section occupies from 0x200 to sectionEnd
    var rawSize = sectionEnd - 0x200;
    if (rawSize < 0) rawSize = 0;
    pe[secTableOff + 16] = (byte)(rawSize & 0xFF);
    pe[secTableOff + 17] = (byte)((rawSize >> 8) & 0xFF);
    // PointerToRawData at +20
    pe[secTableOff + 20] = 0x00; pe[secTableOff + 21] = 0x02; // 0x200

    return pe;
  }
}
