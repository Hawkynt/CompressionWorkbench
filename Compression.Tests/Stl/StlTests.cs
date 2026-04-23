using System.Buffers.Binary;
using System.Text;
using FileFormat.Stl;

namespace Compression.Tests.Stl;

[TestFixture]
public class StlTests {

  /// <summary>
  /// Builds a minimal binary STL: 80-byte header + uint32 triangle count +
  /// <paramref name="triCount"/> * 50 bytes per triangle (12-byte normal + 3×12-byte vertices + 2-byte attribute).
  /// </summary>
  private static byte[] BuildBinaryStl(int triCount) {
    var size = 84 + 50 * triCount;
    var buf = new byte[size];
    // Header: "fixture" padded with zeros.
    var h = Encoding.ASCII.GetBytes("fixture");
    h.CopyTo(buf.AsSpan(0));
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), (uint)triCount);

    for (var i = 0; i < triCount; i++) {
      var pos = 84 + i * 50;
      // normal (0,0,1)
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 8, 4), 1.0f);
      // three vertices forming a triangle offset by i
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 12, 4), i);       // v0.x
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 16, 4), 0);       // v0.y
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 20, 4), 0);       // v0.z
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 24, 4), i + 1);   // v1.x
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 28, 4), 0);       // v1.y
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 32, 4), 0);       // v1.z
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 36, 4), i);       // v2.x
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 40, 4), 1);       // v2.y
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos + 44, 4), 0);       // v2.z
      // attribute 0
    }
    return buf;
  }

  private static byte[] BuildAsciiStl() {
    var sb = new StringBuilder();
    sb.Append("solid demo\n");
    sb.Append("facet normal 0 0 1\n");
    sb.Append("outer loop\n");
    sb.Append("vertex 0 0 0\n");
    sb.Append("vertex 1 0 0\n");
    sb.Append("vertex 0 1 0\n");
    sb.Append("endloop\n");
    sb.Append("endfacet\n");
    sb.Append("endsolid demo\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Binary_ListsMetadataAndTriangles() {
    var data = BuildBinaryStl(3);
    using var ms = new MemoryStream(data);
    var entries = new StlFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "triangles.bin"), Is.True);
    var tri = entries.First(e => e.Name == "triangles.bin");
    Assert.That(tri.OriginalSize, Is.EqualTo(3 * 50));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Ascii_ListsMetadataAndTriangles() {
    var data = BuildAsciiStl();
    using var ms = new MemoryStream(data);
    var entries = new StlFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    var tri = entries.First(e => e.Name == "triangles.bin");
    Assert.That(tri.OriginalSize, Is.EqualTo(50), "One triangle → 50 bytes binary payload");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Binary_ExtractWritesFiles() {
    var data = BuildBinaryStl(2);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new StlFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "triangles.bin")), Is.True);
      Assert.That(new FileInfo(Path.Combine(tmp, "triangles.bin")).Length, Is.EqualTo(100));

      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Contains.Substring("variant=binary"));
      Assert.That(meta, Contains.Substring("triangle_count=2"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_UnknownBytes_ReportsVariantUnknown() {
    // Random short buffer that neither satisfies binary size math nor ASCII pattern.
    var data = new byte[32];
    using var ms = new MemoryStream(data);
    var entries = new StlFormatDescriptor().List(ms, null);
    var metaEntry = entries.First(e => e.Name == "metadata.ini");
    using var metaStream = new MemoryStream();
    new StlFormatDescriptor().ExtractEntry(new MemoryStream(data), "metadata.ini", metaStream, null);
    Assert.That(Encoding.UTF8.GetString(metaStream.ToArray()), Contains.Substring("variant=unknown"));
  }
}
