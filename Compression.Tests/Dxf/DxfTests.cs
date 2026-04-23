using System.Text;
using FileFormat.Dxf;

namespace Compression.Tests.Dxf;

[TestFixture]
public class DxfTests {

  private static byte[] BuildMinimalDxf() {
    // Standard DXF group-code pairs: (code)\n(value)\n
    var sb = new StringBuilder();
    sb.Append("  0\nSECTION\n  2\nHEADER\n  9\n$ACADVER\n  1\nAC1014\n  0\nENDSEC\n");
    sb.Append("  0\nSECTION\n  2\nENTITIES\n");
    sb.Append("  0\nLINE\n  8\n0\n 10\n0.0\n 20\n0.0\n 30\n0.0\n 11\n1.0\n 21\n1.0\n 31\n0.0\n");
    sb.Append("  0\nCIRCLE\n  8\n0\n 10\n0.0\n 20\n0.0\n 30\n0.0\n 40\n5.0\n");
    sb.Append("  0\nLINE\n  8\n0\n 10\n2.0\n 20\n2.0\n 30\n0.0\n 11\n3.0\n 21\n3.0\n 31\n0.0\n");
    sb.Append("  0\nENDSEC\n");
    sb.Append("  0\nEOF\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_EmitsSectionsAndMetadata() {
    var data = BuildMinimalDxf();
    using var ms = new MemoryStream(data);
    var entries = new DxfFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("HEADER", StringComparison.OrdinalIgnoreCase)), Is.True);
    Assert.That(entries.Any(e => e.Name.Contains("ENTITIES", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_HistogramHasLinesAndCircle() {
    var data = BuildMinimalDxf();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new DxfFormatDescriptor().Extract(ms, tmp, null, null);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Contains.Substring("entity_LINE=2"));
      Assert.That(meta, Contains.Substring("entity_CIRCLE=1"));
      Assert.That(meta, Contains.Substring("section_count=2"));
      Assert.That(Directory.GetFiles(tmp, "section_*.txt"), Has.Length.EqualTo(2));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_EmptyFile_StillEmitsMetadata() {
    var data = Array.Empty<byte>();
    using var ms = new MemoryStream(data);
    var entries = new DxfFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }
}
