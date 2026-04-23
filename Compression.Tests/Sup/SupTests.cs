using System.Buffers.Binary;
using FileFormat.Sup;

namespace Compression.Tests.Sup;

[TestFixture]
public class SupTests {

  /// <summary>Builds one PGS segment with the given type, PTS, and body bytes.</summary>
  private static byte[] BuildSegment(byte type, uint pts, byte[] body) {
    var seg = new byte[13 + body.Length];
    seg[0] = (byte)'P';
    seg[1] = (byte)'G';
    BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(2, 4), pts);
    BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(6, 4), 0);
    seg[10] = type;
    BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(11, 2), (ushort)body.Length);
    body.CopyTo(seg.AsSpan(13));
    return seg;
  }

  /// <summary>Builds a PGS file with <paramref name="epochCount"/> epochs (PCS+PDS+ODS+END each).</summary>
  private static byte[] BuildSup(int epochCount) {
    using var ms = new MemoryStream();
    for (var i = 0; i < epochCount; i++) {
      var pts = (uint)((i + 1) * 90_000);
      ms.Write(BuildSegment(SupReader.SegPresentationComposition, pts, [0xAA, 0xBB]));
      ms.Write(BuildSegment(SupReader.SegPaletteDefinition, pts, [0x01, 0x02, 0x03]));
      ms.Write(BuildSegment(SupReader.SegObjectDefinition, pts, [0xCC]));
      ms.Write(BuildSegment(SupReader.SegEnd, pts + 1000, []));
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Read_ParsesAllSegments() {
    var data = BuildSup(2);
    var stream = SupReader.Read(data);
    Assert.That(stream.Segments, Has.Count.EqualTo(8)); // 4 segments × 2 epochs
    Assert.That(stream.Epochs, Has.Count.EqualTo(2));
    Assert.That(stream.Epochs[0].SegmentCount, Is.EqualTo(4));
    Assert.That(stream.Epochs[0].StartPtsRaw, Is.EqualTo(90_000u));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsMetadataAndEpochs() {
    var data = BuildSup(3);
    using var ms = new MemoryStream(data);
    var entries = new SupFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("subtitle_")), Is.EqualTo(3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesEpochFiles() {
    var data = BuildSup(2);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new SupFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "subtitle_000.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "subtitle_001.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Read_TruncatedFile_Throws() {
    var data = new byte[5];
    Assert.That(() => SupReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_InvalidMagic_Throws() {
    var data = new byte[20];
    data[0] = (byte)'X';
    data[1] = (byte)'Y';
    Assert.That(() => SupReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_SegmentsBeforeFirstPcs_AreIgnoredForEpochGrouping() {
    using var ms = new MemoryStream();
    // Stray PDS+ODS before any PCS — should not produce an epoch.
    ms.Write(BuildSegment(SupReader.SegPaletteDefinition, 0, [0x10]));
    ms.Write(BuildSegment(SupReader.SegObjectDefinition, 0, [0x11]));
    ms.Write(BuildSegment(SupReader.SegEnd, 0, []));
    // Then one full epoch.
    ms.Write(BuildSegment(SupReader.SegPresentationComposition, 9000, [0xAA]));
    ms.Write(BuildSegment(SupReader.SegEnd, 10000, []));

    var parsed = SupReader.Read(ms.ToArray());
    Assert.That(parsed.Epochs, Has.Count.EqualTo(1));
    Assert.That(parsed.Segments, Has.Count.EqualTo(5));
  }
}
