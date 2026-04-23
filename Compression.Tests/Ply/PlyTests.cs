using System.Text;
using FileFormat.Ply;

namespace Compression.Tests.Ply;

[TestFixture]
public class PlyTests {

  private static byte[] BuildAsciiPly() {
    var sb = new StringBuilder();
    sb.Append("ply\n");
    sb.Append("format ascii 1.0\n");
    sb.Append("comment crafted fixture\n");
    sb.Append("element vertex 3\n");
    sb.Append("property float x\n");
    sb.Append("property float y\n");
    sb.Append("property float z\n");
    sb.Append("element face 1\n");
    sb.Append("property list uchar int vertex_indices\n");
    sb.Append("end_header\n");
    sb.Append("0 0 0\n");
    sb.Append("1 0 0\n");
    sb.Append("0 1 0\n");
    sb.Append("3 0 1 2\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_IncludesHeaderBodyMetadata() {
    var data = BuildAsciiPly();
    using var ms = new MemoryStream(data);
    var entries = new PlyFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "header.txt"), Is.True);
    Assert.That(entries.Any(e => e.Name == "body.bin"), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesHeaderBodyAndMetadata() {
    var data = BuildAsciiPly();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new PlyFormatDescriptor().Extract(ms, tmp, null, null);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Contains.Substring("format=ascii 1.0"));
      Assert.That(meta, Contains.Substring("element_count=2"));
      Assert.That(meta, Contains.Substring("element.vertex.count=3"));
      Assert.That(meta, Contains.Substring("element.face.count=1"));

      var header = File.ReadAllText(Path.Combine(tmp, "header.txt"));
      Assert.That(header, Does.StartWith("ply"));
      Assert.That(header, Does.Contain("end_header"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_NoHeader_StillProducesMetadata() {
    var data = Encoding.ASCII.GetBytes("garbage\n");
    using var ms = new MemoryStream(data);
    var entries = new PlyFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }
}
