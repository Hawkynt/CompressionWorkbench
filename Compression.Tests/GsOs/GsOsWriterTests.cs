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

    // Magic
    Assert.That(image[..4], Is.EqualTo("2IMG"u8.ToArray()),
      "2IMG magic must occupy bytes 0..3.");
    // Creator
    Assert.That(Encoding.ASCII.GetString(image, 4, 4), Is.EqualTo("CmpW"),
      "Default creator code is CmpW (CompressionWorkbench).");
    // Header size = 64
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(8, 2)), Is.EqualTo(64),
      "Header size field at offset 8 must equal 64 (canonical header size).");
    // Version = 1
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(10, 2)), Is.EqualTo(1),
      "Version at offset 10 must be 1 (universal 2IMG spec).");
    // Image format = 1 (ProDOS block order)
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(12, 4)), Is.EqualTo(1u),
      "Image format at offset 12 must be 1 (ProDOS block order).");
    // Flags = 0
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16, 4)), Is.EqualTo(0u),
      "Flags at offset 16 must be 0 (unlocked, no DOS-3.3 volume number).");
    // Data block count = 280 (default 140 KB floppy)
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(20, 4)),
      Is.EqualTo((uint)ProDosWriter.FloppyTotalBlocks),
      "Data block count at offset 20 must match the ProDOS volume block count (280 default).");
    // Data offset = 64
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(24, 4)), Is.EqualTo(64u),
      "Data offset at offset 24 must equal 64 (data starts right after the header).");
    // Data length = blocks * 512
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(28, 4)),
      Is.EqualTo((uint)(ProDosWriter.FloppyTotalBlocks * 512)),
      "Data length at offset 28 must equal blocks × 512.");

    // Total image length
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

    // Strip the 2IMG header, read inner as a raw ProDOS volume.
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
  public void Build_WithComment_AppendsCommentBytes_AndUpdatesOffsetFields() {
    var w = new GsOsWriter();
    w.AddFile("X", Encoding.ASCII.GetBytes("data"));
    w.SetComment("This is a GS/OS test volume.");
    var image = w.Build();

    var commentOffset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(32, 4));
    var commentLength = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(36, 4));
    Assert.That(commentLength, Is.GreaterThan(0u),
      "Comment-length field must be set when a comment is configured.");
    Assert.That(commentOffset, Is.EqualTo((uint)(64 + ProDosWriter.FloppyTotalBlocks * 512)),
      "Comment must be placed immediately after the ProDOS payload.");
    var commentBytes = image.AsSpan((int)commentOffset, (int)commentLength).ToArray();
    Assert.That(Encoding.ASCII.GetString(commentBytes), Is.EqualTo("This is a GS/OS test volume."),
      "Comment byte range must contain the configured ASCII text.");
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
  public void Build_EmptyName_Throws() {
    var w = new GsOsWriter();
    Assert.That(() => w.AddFile("", new byte[1]), Throws.InstanceOf<ArgumentException>());
  }

  [Test, Category("Boundary")]
  public void Build_CreatorCode_TruncatedOrPaddedToFourBytes() {
    var w = new GsOsWriter();
    w.AddFile("X", new byte[8]);
    // Long creator must be truncated to first 4 chars.
    var imgLong = w.Build(creator: "ABCDEFG");
    Assert.That(Encoding.ASCII.GetString(imgLong, 4, 4), Is.EqualTo("ABCD"),
      "Creator codes longer than 4 chars must be truncated to the first 4.");

    w = new GsOsWriter();
    w.AddFile("X", new byte[8]);
    // Short creator must be space-padded to 4 chars.
    var imgShort = w.Build(creator: "AB");
    Assert.That(Encoding.ASCII.GetString(imgShort, 4, 4), Is.EqualTo("AB  "),
      "Short creator codes must be space-padded to exactly 4 bytes.");
  }
}
