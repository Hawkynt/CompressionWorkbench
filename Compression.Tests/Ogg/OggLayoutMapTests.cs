#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Ogg;

namespace Compression.Tests.Ogg;

[TestFixture]
public class OggLayoutMapTests {

  /// <summary>
  /// Builds a minimal OGG file with one logical stream: one BOS page and one
  /// data page. Each page has the 27-byte header + 1-byte segment table +
  /// a small payload.
  /// </summary>
  private static MemoryStream BuildTestOgg(int dataPages = 2) {
    var ms = new MemoryStream();
    uint serial = 0x12345678;

    // BOS page (beginning of stream)
    WritePage(ms, serial, flags: 0x02, granule: 0, seqNo: 0, payload: new byte[16]);

    // Data pages
    for (var i = 0; i < dataPages; i++)
      WritePage(ms, serial, flags: 0x00, granule: (ulong)(i + 1) * 1024, seqNo: (uint)(i + 1), payload: new byte[64]);

    ms.Position = 0;
    return ms;
  }

  private static void WritePage(MemoryStream ms, uint serial, byte flags, ulong granule,
                                 uint seqNo, byte[] payload) {
    // OGG page header: 27 bytes + segment table + payload
    // We use a single segment for the payload (length < 255)
    ms.Write("OggS"u8); // capture pattern
    ms.WriteByte(0);     // stream_structure_version
    ms.WriteByte(flags); // header_type_flag
    Span<byte> buf = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(buf, granule);
    ms.Write(buf);       // granule_position
    BinaryPrimitives.WriteUInt32LittleEndian(buf, serial);
    ms.Write(buf[..4]);  // bitstream_serial_number
    BinaryPrimitives.WriteUInt32LittleEndian(buf, seqNo);
    ms.Write(buf[..4]);  // page_sequence_number
    ms.Write(new byte[4]); // checksum (zero for test)
    ms.WriteByte(1);       // number_page_segments
    ms.WriteByte((byte)payload.Length); // segment table (1 entry)
    ms.Write(payload);
  }

  [Test]
  public void EnumerateChunks_FirstPageIsHeader() {
    using var ms = BuildTestOgg();
    var chunks = OggLayoutMap.Enumerate(ms).ToList();

    var first = chunks[0];
    Assert.That(first.Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    Assert.That(first.FileName, Does.Contain("header"));
    Assert.That(first.Offset, Is.EqualTo(0));
  }

  [Test]
  public void EnumerateChunks_DataPagesAreUsed() {
    using var ms = BuildTestOgg(dataPages: 3);
    var chunks = OggLayoutMap.Enumerate(ms).ToList();

    var dataChunks = chunks.Where(c => c.Kind == DefragBlockKind.Used).ToList();
    Assert.That(dataChunks, Has.Count.EqualTo(3));
    foreach (var c in dataChunks)
      Assert.That(c.FileName, Does.Contain("data"));
  }

  [Test]
  public void EnumerateChunks_CoversFullFile() {
    using var ms = BuildTestOgg();
    var chunks = OggLayoutMap.Enumerate(ms).ToList();
    var total = chunks.Sum(c => c.Length);
    Assert.That(total, Is.EqualTo(ms.Length));
  }

  [Test]
  public void EnumerateChunks_Contiguous() {
    using var ms = BuildTestOgg(dataPages: 4);
    var chunks = OggLayoutMap.Enumerate(ms).OrderBy(c => c.Offset).ToList();
    for (var i = 1; i < chunks.Count; i++)
      Assert.That(chunks[i].Offset, Is.EqualTo(chunks[i - 1].Offset + chunks[i - 1].Length));
  }

  [Test]
  public void Descriptor_ImplementsIFileInternalLayoutMap() {
    var d = new OggFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFileInternalLayoutMap>());
  }

  [Test]
  public void EnumerateChunks_EmptyStream_ReturnsNothing() {
    using var ms = new MemoryStream();
    var chunks = OggLayoutMap.Enumerate(ms).ToList();
    Assert.That(chunks, Is.Empty);
  }
}
