using System.Text;
using FileFormat.Collada;

namespace Compression.Tests.Collada;

[TestFixture]
public class ColladaTests {

  private static byte[] BuildMinimalCollada() {
    var sb = new StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    sb.Append("<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">");
    sb.Append("<asset><created>2026-04-22T00:00:00Z</created></asset>");
    sb.Append("<library_geometries><geometry id=\"g1\"/></library_geometries>");
    sb.Append("<library_materials><material id=\"m1\"/></library_materials>");
    sb.Append("<library_images><image id=\"i1\"/></library_images>");
    sb.Append("<library_visual_scenes><visual_scene id=\"s1\"/></library_visual_scenes>");
    sb.Append("<scene><instance_visual_scene url=\"#s1\"/></scene>");
    sb.Append("</COLLADA>");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_EmitsOneEntryPerLibrary() {
    var data = BuildMinimalCollada();
    using var ms = new MemoryStream(data);
    var entries = new ColladaFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "document.xml"), Is.True);
    Assert.That(entries.Count(e => e.Name.StartsWith("library_", StringComparison.Ordinal)), Is.EqualTo(4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_MetadataHasVersionAndLibraryCounts() {
    var data = BuildMinimalCollada();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new ColladaFormatDescriptor().Extract(ms, tmp, null, null);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Contains.Substring("version=1.4.1"));
      Assert.That(meta, Contains.Substring("library_count=4"));
      Assert.That(meta, Contains.Substring("scene_instances=#s1"));
      Assert.That(File.Exists(Path.Combine(tmp, "library_geometries.xml")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_MalformedXml_StillProducesMetadataAndFull() {
    var data = Encoding.UTF8.GetBytes("not-xml-at-all");
    using var ms = new MemoryStream(data);
    var entries = new ColladaFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "FULL.dae"), Is.True);
  }
}
