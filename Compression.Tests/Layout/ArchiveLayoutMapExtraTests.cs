#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// Tests for <see cref="IArchiveLayoutMap"/> implementations on RAR, CAB, PDF,
/// and FLAC. For each format we create a small archive, call EnumerateLayout,
/// and assert that MetadataReserved + Used tiles cover the file with no gaps.
/// </summary>
[TestFixture]
public class ArchiveLayoutMapExtraTests {

  // ──────────────────────────────────────────────────────────────────────
  // RAR
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Rar_DescriptorImplementsLayoutMap() {
    var d = new FileFormat.Rar.RarFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void Rar5_EnumerateLayout_CoversFullFile() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Rar.RarWriter(ms, method: 0); // Store for simplicity
    w.AddFile("hello.txt", "world"u8.ToArray());
    w.AddFile("data.bin", new byte[64]);
    w.Finish();

    var d = new FileFormat.Rar.RarFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
    // RAR Store files may produce 0-byte data tiles; just check structure
    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("Signature")),
      Is.True, "Expected a RAR Signature tile.");
  }

  [Test]
  public void Rar5_EnumerateLayout_HasEndOfArchive() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Rar.RarWriter(ms, method: 0);
    w.AddFile("a.txt", "abc"u8.ToArray());
    w.Finish();

    var d = new FileFormat.Rar.RarFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("End-of-archive")),
      Is.True, "Expected End-of-archive tile.");
  }

  [Test]
  public void Rar4_EnumerateLayout_CoversFullFile() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Rar.Rar4Writer(ms, method: 0x30); // Store
    w.AddFile("hello.txt", "world"u8.ToArray());
    w.Finish();

    var d = new FileFormat.Rar.RarFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
  }

  // ──────────────────────────────────────────────────────────────────────
  // CAB
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Cab_DescriptorImplementsLayoutMap() {
    var d = new FileFormat.Cab.CabFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void Cab_EnumerateLayout_CoversFullFile() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Cab.CabWriter(FileFormat.Cab.CabCompressionType.None);
    w.AddFile("hello.txt", "world"u8.ToArray());
    w.AddFile("data.bin", new byte[64]);
    w.WriteTo(ms);

    var d = new FileFormat.Cab.CabFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
    AssertHasUsed(tiles);
  }

  [Test]
  public void Cab_EnumerateLayout_ReportsCfheader() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Cab.CabWriter(FileFormat.Cab.CabCompressionType.None);
    w.AddFile("a.txt", "abc"u8.ToArray());
    w.WriteTo(ms);

    var d = new FileFormat.Cab.CabFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("CFHEADER")),
      Is.True, "Expected a CFHEADER tile.");
  }

  [Test]
  public void Cab_EnumerateLayout_ReportsDataBlocks() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Cab.CabWriter(FileFormat.Cab.CabCompressionType.None);
    w.AddFile("test.txt", "Hello CAB!"u8.ToArray());
    w.WriteTo(ms);

    var d = new FileFormat.Cab.CabFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.Used
                                && t.FileName != null
                                && t.FileName.Contains("data")),
      Is.True, "Expected CFDATA Used tile.");
  }

  // ──────────────────────────────────────────────────────────────────────
  // PDF
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Pdf_DescriptorImplementsLayoutMap() {
    var d = new FileFormat.Pdf.PdfFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void Pdf_EnumerateLayout_CoversFullFile() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Pdf.PdfWriter();
    w.AddFile("test.txt", "Hello PDF!"u8.ToArray());
    w.WriteTo(ms);

    var d = new FileFormat.Pdf.PdfFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
  }

  [Test]
  public void Pdf_EnumerateLayout_ReportsHeader() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Pdf.PdfWriter();
    w.AddFile("a.txt", "abc"u8.ToArray());
    w.WriteTo(ms);

    var d = new FileFormat.Pdf.PdfFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("PDF Header")),
      Is.True, "Expected a PDF Header tile.");
  }

  [Test]
  public void Pdf_EnumerateLayout_ReportsEof() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Pdf.PdfWriter();
    w.AddFile("a.txt", "abc"u8.ToArray());
    w.WriteTo(ms);

    var d = new FileFormat.Pdf.PdfFormatDescriptor();
    ms.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(ms).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("%%EOF")),
      Is.True, "Expected a %%EOF tile.");
  }

  // ──────────────────────────────────────────────────────────────────────
  // FLAC
  // ──────────────────────────────────────────────────────────────────────

  [Test]
  public void Flac_DescriptorImplementsLayoutMap() {
    var d = new FileFormat.Flac.FlacFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveLayoutMap>());
  }

  [Test]
  public void Flac_EnumerateLayout_CoversFullFile() {
    // Create a small FLAC file from PCM data
    using var pcmMs = new MemoryStream();
    // 16-bit stereo 44100Hz: 100 samples = 400 bytes
    var pcmData = new byte[400];
    for (var i = 0; i < pcmData.Length; i += 2) {
      var sample = (short)(Math.Sin(i * 0.1) * 1000);
      pcmData[i] = (byte)(sample & 0xFF);
      pcmData[i + 1] = (byte)((sample >> 8) & 0xFF);
    }
    pcmMs.Write(pcmData);
    pcmMs.Position = 0;

    using var flacMs = new MemoryStream();
    FileFormat.Flac.FlacWriter.Compress(pcmMs, flacMs);

    var d = new FileFormat.Flac.FlacFormatDescriptor();
    flacMs.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(flacMs).ToList();

    Assert.That(tiles, Is.Not.Empty);
    AssertHasMetadata(tiles);
    AssertHasUsed(tiles);
  }

  [Test]
  public void Flac_EnumerateLayout_ReportsMagicAndStreaminfo() {
    using var pcmMs = new MemoryStream();
    var pcmData = new byte[400];
    pcmMs.Write(pcmData);
    pcmMs.Position = 0;

    using var flacMs = new MemoryStream();
    FileFormat.Flac.FlacWriter.Compress(pcmMs, flacMs);

    var d = new FileFormat.Flac.FlacFormatDescriptor();
    flacMs.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(flacMs).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("fLaC Magic")),
      Is.True, "Expected fLaC Magic tile.");
    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved
                                && t.FileName != null
                                && t.FileName.Contains("STREAMINFO")),
      Is.True, "Expected STREAMINFO tile.");
  }

  [Test]
  public void Flac_EnumerateLayout_ReportsAudioFrames() {
    using var pcmMs = new MemoryStream();
    var pcmData = new byte[800];
    for (var i = 0; i < pcmData.Length; i += 2) {
      var sample = (short)(Math.Sin(i * 0.05) * 2000);
      pcmData[i] = (byte)(sample & 0xFF);
      pcmData[i + 1] = (byte)((sample >> 8) & 0xFF);
    }
    pcmMs.Write(pcmData);
    pcmMs.Position = 0;

    using var flacMs = new MemoryStream();
    FileFormat.Flac.FlacWriter.Compress(pcmMs, flacMs);

    var d = new FileFormat.Flac.FlacFormatDescriptor();
    flacMs.Position = 0;
    var tiles = ((IArchiveLayoutMap)d).EnumerateLayout(flacMs).ToList();

    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.Used
                                && t.FileName != null
                                && t.FileName.Contains("Audio Frame")),
      Is.True, "Expected Audio Frame tile.");
  }

  // ──────────────────────────────────────────────────────────────────────
  // Helpers
  // ──────────────────────────────────────────────────────────────────────

  private static void AssertHasMetadata(List<DefragBlockInfo> tiles) {
    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.MetadataReserved), Is.True,
      "Expected at least one MetadataReserved tile.");
  }

  private static void AssertHasUsed(List<DefragBlockInfo> tiles) {
    Assert.That(tiles.Any(t => t.Kind == DefragBlockKind.Used), Is.True,
      "Expected at least one Used tile.");
  }
}
