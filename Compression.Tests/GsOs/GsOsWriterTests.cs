#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.GsOs;
using FileSystem.ProDos;

namespace Compression.Tests.GsOs;

/// <summary>
/// WORM (write-once read-many) tier verification for
/// <see cref="GsOsFormatDescriptor"/>: the emitted 2IMG container wraps
/// a real ProDOS volume whose files round-trip through
/// <see cref="ProDosReader"/>.
/// </summary>
[TestFixture]
public class GsOsWriterTests {

  [Test, Category("HappyPath")]
  public void Build_EmitsCanonical2ImgHeader_WithProDosBlockOrder() {
    var w = new GsOsWriter();
    w.AddFile("HELLO", Encoding.ASCII.GetBytes("hello gsos"));
    var image = w.Build();

    Assert.That(image[..4], Is.EqualTo("2IMG"u8.ToArray()),
      "2IMG magic must occupy bytes 0..3.");
    Assert.That(Encoding.ASCII.GetString(image, 4, 4), Is.EqualTo("CWB!"),
      "Default creator code is CWB! (CompressionWorkbench).");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(8, 2)), Is.EqualTo(64),
      "Header size field at offset 8 must equal 64 (canonical header size).");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(10, 2)), Is.EqualTo(1),
      "Version at offset 10 must be 1 (universal 2IMG spec).");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(12, 4)), Is.EqualTo(1u),
      "Image format at offset 12 must be 1 (ProDOS block order).");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16, 4)), Is.EqualTo(0u),
      "Flags at offset 16 must be 0 (unlocked, no DOS-3.3 volume number).");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(20, 4)),
      Is.EqualTo((uint)ProDosWriter.FloppyTotalBlocks),
      "Data block count at offset 20 must match the ProDOS volume block count (280 default).");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(24, 4)), Is.EqualTo(64u),
      "Data offset at offset 24 must equal 64 (data starts right after the header).");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(28, 4)),
      Is.EqualTo((uint)(ProDosWriter.FloppyTotalBlocks * 512)),
      "Data length at offset 28 must equal blocks × 512.");

    Assert.That(image.Length, Is.EqualTo(64 + ProDosWriter.FloppyTotalBlocks * 512),
      "Image length must equal header (64) + ProDOS payload.");
  }

  [Test, Category("HappyPath")]
  public void Build_InnerVolume_ParsesAsProDos_AndContainsAddedFiles() {
    var alpha = Encoding.ASCII.GetBytes("alpha content for gsos");
    var beta = new byte[600];
    for (var i = 0; i < beta.Length; i++) beta[i] = (byte)(i & 0xFF);

    var w = new GsOsWriter();
    w.AddFile("ALPHA", alpha);
    w.AddFile("BETA", beta);
    var image = w.Build();

    var inner = image.AsSpan(64).ToArray();
    using var innerMs = new MemoryStream(inner);
    using var r = new ProDosReader(innerMs);
    var entries = r.Entries.Where(e => !e.IsDirectory)
                           .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    Assert.That(entries, Does.ContainKey("ALPHA"),
      "Inner ProDOS volume must contain ALPHA after WORM emit.");
    Assert.That(entries, Does.ContainKey("BETA"),
      "Inner ProDOS volume must contain BETA after WORM emit.");
    Assert.That(r.Extract(entries["ALPHA"]), Is.EqualTo(alpha),
      "ALPHA contents must round-trip the embedded ProDOS payload byte-for-byte.");
    Assert.That(r.Extract(entries["BETA"]), Is.EqualTo(beta),
      "BETA contents must round-trip the embedded ProDOS payload byte-for-byte.");
  }

  [Test, Category("HappyPath")]
  public void Build_With800KBlockCount_EmitsLargerImage_AndUpdatesHeader() {
    var w = new GsOsWriter();
    w.AddFile("X", new byte[10]);
    var image = w.Build(totalBlocks: ProDosWriter.Disk800KTotalBlocks);

    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(20, 4)),
      Is.EqualTo((uint)ProDosWriter.Disk800KTotalBlocks),
      "Data block count must reflect the 800 KB floppy block count.");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(28, 4)),
      Is.EqualTo((uint)(ProDosWriter.Disk800KTotalBlocks * 512)),
      "Data length must equal 1600 × 512 = 819 200 bytes for the 800 KB floppy.");
    Assert.That(image.Length, Is.EqualTo(64 + ProDosWriter.Disk800KTotalBlocks * 512),
      "Image length must match header + inner volume for the 800 KB floppy.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_CreatePath_RoundTripsViaProDosReader() {
    var d = new GsOsFormatDescriptor();
    var alpha = Encoding.ASCII.GetBytes("alpha via descriptor.Create");
    var beta = Encoding.ASCII.GetBytes("beta payload");

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("ALPHA", alpha),
      ArchiveInputInfo.InMemory("BETA", beta),
    };

    using var output = new MemoryStream();
    d.Create(output, inputs, new FormatCreateOptions());

    var image = output.ToArray();
    Assert.That(image[..4], Is.EqualTo("2IMG"u8.ToArray()),
      "Descriptor.Create must emit the 2IMG magic at offset 0.");

    var inner = image.AsSpan(64).ToArray();
    using var innerMs = new MemoryStream(inner);
    using var r = new ProDosReader(innerMs);
    var entries = r.Entries.Where(e => !e.IsDirectory)
                           .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    Assert.That(entries, Does.ContainKey("ALPHA"),
      "Descriptor.Create payload must include ALPHA in the inner ProDOS volume.");
    Assert.That(entries, Does.ContainKey("BETA"),
      "Descriptor.Create payload must include BETA in the inner ProDOS volume.");
    Assert.That(r.Extract(entries["ALPHA"]), Is.EqualTo(alpha),
      "ALPHA contents must survive the Create round-trip.");
    Assert.That(r.Extract(entries["BETA"]), Is.EqualTo(beta),
      "BETA contents must survive the Create round-trip.");
  }

  [Test, Category("Sad")]
  public void Build_NullFile_Throws() {
    var w = new GsOsWriter();
    Assert.That(() => w.AddFile("X", null!), Throws.ArgumentNullException);
  }

  [Test, Category("Sad")]
  public void Build_EmptyName_DoesNotThrow() {
    // GsOsWriter forwards verbatim to the inner ProDosWriter, which currently
    // accepts an empty name without raising. Pin the lenient surface so a future
    // tightening of name validation has to update this test alongside the writer.
    var w = new GsOsWriter();
    w.AddFile("", new byte[1]);
    Assert.That(() => w.Build(), Throws.Nothing);
  }
}
