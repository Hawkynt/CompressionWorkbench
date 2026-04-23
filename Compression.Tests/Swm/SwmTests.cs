using System.Buffers.Binary;
using System.Text;
using FileFormat.Swm;
using FileFormat.Wim;

namespace Compression.Tests.Swm;

[TestFixture]
public class SwmTests {

  /// <summary>
  /// Builds a WIM byte stream and patches the part_number / total_parts fields in the
  /// header so it looks like one volume of an N-part SWM set. The resulting bytes
  /// still parse as a regular WIM (the data layout is unchanged), which matches what
  /// the SWM reader expects on disk.
  /// </summary>
  private static byte[] BuildSwmVolume(IReadOnlyList<byte[]> resources, ushort partNumber, ushort totalParts) {
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms, WimConstants.CompressionNone);
    writer.Write(resources);
    var bytes = ms.ToArray();
    // Header layout: part_number @ 40, total_parts @ 42 (uint16 LE).
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(40), partNumber);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(42), totalParts);
    return bytes;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new SwmFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Swm"));
    Assert.That(d.Extensions, Contains.Item(".swm"));
    Assert.That(d.Extensions, Contains.Item(".swm2"));
    Assert.That(d.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMetadataIniWithPartInfo() {
    var data = BuildSwmVolume([Encoding.UTF8.GetBytes("vol1 payload")], partNumber: 1, totalParts: 3);
    using var ms = new MemoryStream(data);
    var entries = new SwmFormatDescriptor().List(ms, null);
    var meta = entries.First(e => e.Name == "metadata.ini");
    Assert.That(meta.OriginalSize, Is.GreaterThan(0));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_WritesMetadataReferencingTotalParts() {
    var data = BuildSwmVolume([Encoding.UTF8.GetBytes("vol1 payload")], partNumber: 1, totalParts: 3);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new SwmFormatDescriptor().Extract(ms, tmp, null, null);
      var metaPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True);
      var metaText = File.ReadAllText(metaPath);
      Assert.That(metaText, Does.Contain("total_parts = 3"));
      Assert.That(metaText, Does.Contain("part_number = 1"));
      Assert.That(metaText, Does.Contain("note = full extraction requires all 3 sibling files"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void List_NonWimInput_Throws() {
    var data = new byte[64];
    data[0] = (byte)'N'; data[1] = (byte)'O'; data[2] = (byte)'P'; data[3] = (byte)'E';
    using var ms = new MemoryStream(data);
    Assert.That(() => new SwmFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }
}
