#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Med;

namespace Compression.Tests.Med;

[TestFixture]
public class MedTests {

  // Builds a minimal MMD0 module: header + song struct (1 block, 1 sample) +
  // a block pointer table + 1 block + a sample pointer table + 1 sample.
  private static byte[] MakeSyntheticMed() {
    // Layout offsets we control:
    //   0x00  header (52 bytes is plenty for our pointers)
    //   0x40  song struct (768 bytes; numblocks@+504, numsamples@+767)
    //   blockArr / smplArr / block / sample placed after the song struct.
    const int songPtr = 0x40;
    const int songLen = 768;
    var blockArrPtr = songPtr + songLen;     // 1 u32
    var smplArrPtr = blockArrPtr + 4;        // 1 u32
    var blockPtr = smplArrPtr + 4;           // MMD0Block
    // MMD0Block: lines byte, tracks byte, then lines*tracks*3 note bytes.
    const int lines = 1, tracks = 1;
    var blockLen = 2 + lines * tracks * 3;
    var smplPtr = blockPtr + blockLen;       // InstrHdr (6) + sample data
    const int sampleData = 4;
    var total = smplPtr + 6 + sampleData;

    var buf = new byte[total];
    buf[0] = (byte)'M'; buf[1] = (byte)'M'; buf[2] = (byte)'D'; buf[3] = (byte)'0';
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), songPtr);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16, 4), (uint)blockArrPtr);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), (uint)smplArrPtr);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(32, 4), 0); // no expdata

    // Song struct counts.
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(songPtr + 504, 2), 1); // numblocks
    buf[songPtr + 767] = 1; // numsamples

    // Block pointer table.
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(blockArrPtr, 4), (uint)blockPtr);
    // Sample pointer table.
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(smplArrPtr, 4), (uint)smplPtr);

    // Block.
    buf[blockPtr] = lines - 1; // MMD0 stores lines-1
    buf[blockPtr + 1] = tracks;

    // Sample (InstrHdr.length = sampleData).
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(smplPtr, 4), sampleData);
    for (var i = 0; i < sampleData; ++i) buf[smplPtr + 6 + i] = (byte)(i + 1);

    return buf;
  }

  [Test]
  public void List_ExposesFullMetadataBlockAndSample() {
    using var ms = new MemoryStream(MakeSyntheticMed());
    var entries = new MedFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.med"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("patterns/block_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
  }

  [Test]
  public void Extract_WritesFullByteIdentical() {
    var blob = MakeSyntheticMed();
    var tmp = Path.Combine(Path.GetTempPath(), "med_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new MedFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.med")), Is.EqualTo(blob));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("magic = MMD0"));
      Assert.That(meta, Does.Contain("num_blocks = 1"));
      Assert.That(meta, Does.Contain("num_samples = 1"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    using var ms = new MemoryStream([(byte)'M', (byte)'M', (byte)'D', (byte)'0', 0, 0, 0, 0]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new MedFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "FULL.med"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_MagicVariants_DoNotCollideWithMod() {
    var d = new MedFormatDescriptor();
    Assert.That(d.MagicSignatures.Any(s => s.Bytes.SequenceEqual("MMD0"u8.ToArray())), Is.True);
    Assert.That(d.MagicSignatures.Any(s => s.Bytes.SequenceEqual("MMD3"u8.ToArray())), Is.True);
    Assert.That(d.Extensions, Does.Contain(".med"));
  }
}
