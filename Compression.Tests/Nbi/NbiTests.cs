using System.Buffers.Binary;
using FileFormat.Nbi;

namespace Compression.Tests.Nbi;

[TestFixture]
public class NbiTests {

  private static readonly byte[] SegmentData = BuildPayload();

  private static byte[] BuildPayload() {
    var p = new byte[300];
    for (var i = 0; i < p.Length; ++i)
      p[i] = (byte)(0x80 + (i & 0x3F));
    return p;
  }

  // Minimal NBI: 512-byte loader header (image header + one last segment),
  // then the segment payload bytes starting at offset 512.
  private static byte[] BuildSyntheticNbi() {
    using var ms = new MemoryStream();
    var header = new byte[NbiReader.HeaderSectorSize];

    // Image header (16 bytes).
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), NbiReader.Magic);
    // Flags word: low byte = header length in 16-byte blocks (image + 1 segment = 2).
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x00000002u);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0x00007C00u);   // load location
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0x00100000u);  // exec address

    // Segment descriptor at offset 16 (16 bytes).
    header[16] = 1;         // length in 16-byte blocks
    header[17] = 0;         // vendor tag
    header[18] = 0;         // reserved
    header[19] = 0x04;      // flags: last segment
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0x00100000u);          // load addr
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)SegmentData.Length); // img len
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)SegmentData.Length); // mem len

    ms.Write(header);
    ms.Write(SegmentData);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new NbiFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Id, Is.EqualTo("Nbi"));
      Assert.That(d.Extensions, Contains.Item(".nbi"));
      Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x36, 0x13, 0x03, 0x1B }));
    });
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataPayloadAndSegment() {
    var nbi = BuildSyntheticNbi();
    var d = new NbiFormatDescriptor();
    using var ms = new MemoryStream(nbi);
    var names = d.List(ms, null).Select(e => e.Name).ToList();
    Assert.Multiple(() => {
      Assert.That(names, Contains.Item("FULL.nbi"));
      Assert.That(names, Contains.Item("metadata.ini"));
      Assert.That(names, Contains.Item("payload.bin"));
      Assert.That(names, Contains.Item("segment_00.bin"));
    });
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesSegmentAndParsesHeader() {
    var nbi = BuildSyntheticNbi();
    var d = new NbiFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "nbi_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(nbi);
      d.Extract(ms, dir, null, null);

      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.nbi")), Is.EqualTo(nbi));

      var segment = File.ReadAllBytes(Path.Combine(dir, "segment_00.bin"));
      Assert.That(segment, Is.EqualTo(SegmentData));

      var payload = File.ReadAllBytes(Path.Combine(dir, "payload.bin"));
      Assert.That(payload, Is.EqualTo(SegmentData));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("valid=1"));
      Assert.That(meta, Does.Contain("segment_count=1"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("Boundary")]
  public void Reader_ParsesSegmentGeometry() {
    var r = new NbiReader(BuildSyntheticNbi());
    Assert.Multiple(() => {
      Assert.That(r.IsValid, Is.True);
      Assert.That(r.Segments, Has.Count.EqualTo(1));
      Assert.That(r.Segments[0].ImageLength, Is.EqualTo((uint)SegmentData.Length));
      Assert.That(r.Segments[0].DataOffset, Is.EqualTo(NbiReader.HeaderSectorSize));
      Assert.That(r.SegmentsComplete, Is.True);
    });
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[32];
    Array.Fill(garbage, (byte)0x55);
    var d = new NbiFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "nbi_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
