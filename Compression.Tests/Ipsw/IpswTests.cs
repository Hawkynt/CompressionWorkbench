using System.IO.Compression;
using System.Text;

namespace Compression.Tests.Ipsw;

[TestFixture]
public class IpswTests {

  private static byte[] BuildSyntheticIpsw() {
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) {
      WriteEntry(zip, "BuildManifest.plist", Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\"?><plist><dict>" +
        "<key>ProductVersion</key><string>17.4.1</string>" +
        "<key>ProductBuildVersion</key><string>21E236</string>" +
        "<key>ProductType</key><string>iPhone15,3</string>" +
        "</dict></plist>"));
      WriteEntry(zip, "Firmware/sep-firmware.bin", new byte[] { 0xA, 0xB, 0xC });
      WriteEntry(zip, "Firmware/dfu/iBSS.d22.RELEASE.im4p", new byte[] { 0xD });
      WriteEntry(zip, "LLB.iphone15,3.RELEASE.im4p", new byte[] { 0xE });
      WriteEntry(zip, "iBoot.iphone15,3.RELEASE.im4p", new byte[] { 0xF });
      WriteEntry(zip, "058-90000-000.dmg", Enumerable.Repeat((byte)0x55, 32).ToArray());
      WriteEntry(zip, "Restore.plist", "some plist"u8.ToArray());
    }
    return ms.ToArray();
  }

  private static void WriteEntry(ZipArchive zip, string name, byte[] data) {
    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
    using var es = entry.Open();
    es.Write(data);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Ipsw.IpswFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ipsw"));
    Assert.That(d.CompoundExtensions, Contains.Item(".ipsw"));
    Assert.That(d.Extensions, Is.Empty);
    Assert.That(d.MagicSignatures, Is.Empty);
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_SurfacesCanonicalAppleArtifacts() {
    var ipsw = BuildSyntheticIpsw();
    var desc = new FileFormat.Ipsw.IpswFormatDescriptor();
    using var ms = new MemoryStream(ipsw);
    var entries = desc.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Contains.Item("FULL.ipsw"));
    Assert.That(names, Contains.Item("metadata.ini"));
    Assert.That(names, Contains.Item("BuildManifest.plist"));
    Assert.That(names, Has.Some.EqualTo("Firmware/sep-firmware.bin"));
    Assert.That(names, Has.Some.EqualTo("Firmware/iBSS.d22.RELEASE.im4p"));
    Assert.That(names, Contains.Item("LLB.iphone15,3.RELEASE.im4p"));
    Assert.That(names, Contains.Item("iBoot.iphone15,3.RELEASE.im4p"));
    Assert.That(names, Contains.Item("058-90000-000.dmg"));
    Assert.That(names, Has.Some.EqualTo("other/Restore.plist"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesCanonicalArtifactsAndMetadata() {
    var ipsw = BuildSyntheticIpsw();
    var desc = new FileFormat.Ipsw.IpswFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), "ipsw_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(ipsw);
      desc.Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.ipsw")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "BuildManifest.plist")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "Firmware", "sep-firmware.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "LLB.iphone15,3.RELEASE.im4p")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "iBoot.iphone15,3.RELEASE.im4p")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "058-90000-000.dmg")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "other", "Restore.plist")), Is.True);

      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("product_version=17.4.1"));
      Assert.That(meta, Does.Contain("build_version=21E236"));
      Assert.That(meta, Does.Contain("identifier=iPhone15,3"));
      Assert.That(meta, Does.Contain("total_zip_entries=7"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }
}
