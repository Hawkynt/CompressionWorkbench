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
      WriteEntry(zip, "Firmware/sep-firmware.bin", [0xA, 0xB, 0xC]);
      WriteEntry(zip, "Firmware/dfu/iBSS.d22.RELEASE.im4p", [0xD]);
      WriteEntry(zip, "LLB.iphone15,3.RELEASE.im4p", [0xE]);
      WriteEntry(zip, "iBoot.iphone15,3.RELEASE.im4p", [0xF]);
      WriteEntry(zip, "058-90000-000.dmg", Enumerable.Repeat((byte)0x55, 32).ToArray());
      WriteEntry(zip, "Restore.plist", "some plist"u8.ToArray());
    }
    return ms.ToArray();
  }

  private static void WriteEntry(ZipArchive zip, string name, byte[] data) {
    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
    using var stream = entry.Open();
    stream.Write(data);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var descriptor = new FileFormat.Ipsw.IpswFormatDescriptor();
    Assert.That(descriptor.Id, Is.EqualTo("Ipsw"));
    Assert.That(descriptor.CompoundExtensions, Contains.Item(".ipsw"));
    Assert.That(descriptor.Extensions, Is.Empty);
    Assert.That(descriptor.MagicSignatures, Is.Empty);
    Assert.That(descriptor.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void List_PreservesExactZipPaths() {
    var descriptor = new FileFormat.Ipsw.IpswFormatDescriptor();
    using var image = new MemoryStream(BuildSyntheticIpsw());

    var names = descriptor.List(image, null).Select(entry => entry.Name).ToList();

    Assert.Multiple(() => {
      Assert.That(names, Contains.Item("FULL.ipsw"));
      Assert.That(names, Contains.Item("metadata.ini"));
      Assert.That(names, Contains.Item("BuildManifest.plist"));
      Assert.That(names, Contains.Item("Firmware/sep-firmware.bin"));
      Assert.That(names, Contains.Item("Firmware/dfu/iBSS.d22.RELEASE.im4p"));
      Assert.That(names, Contains.Item("LLB.iphone15,3.RELEASE.im4p"));
      Assert.That(names, Contains.Item("iBoot.iphone15,3.RELEASE.im4p"));
      Assert.That(names, Contains.Item("058-90000-000.dmg"));
      Assert.That(names, Contains.Item("Restore.plist"));
      Assert.That(names, Does.Not.Contain("Firmware/iBSS.d22.RELEASE.im4p"));
      Assert.That(names, Does.Not.Contain("other/Restore.plist"));
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_PreservesZipPathsAndRendersMetadata() {
    var descriptor = new FileFormat.Ipsw.IpswFormatDescriptor();
    var temp = Path.Combine(Path.GetTempPath(), "ipsw_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try {
      using var image = new MemoryStream(BuildSyntheticIpsw());
      descriptor.Extract(image, temp, null, null);

      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(temp, "FULL.ipsw")), Is.True);
        Assert.That(File.Exists(Path.Combine(temp, "BuildManifest.plist")), Is.True);
        Assert.That(File.Exists(Path.Combine(temp, "Firmware", "sep-firmware.bin")), Is.True);
        Assert.That(File.Exists(Path.Combine(temp, "Firmware", "dfu", "iBSS.d22.RELEASE.im4p")), Is.True);
        Assert.That(File.Exists(Path.Combine(temp, "LLB.iphone15,3.RELEASE.im4p")), Is.True);
        Assert.That(File.Exists(Path.Combine(temp, "iBoot.iphone15,3.RELEASE.im4p")), Is.True);
        Assert.That(File.Exists(Path.Combine(temp, "058-90000-000.dmg")), Is.True);
        Assert.That(File.Exists(Path.Combine(temp, "Restore.plist")), Is.True);
      });

      var metadata = File.ReadAllText(Path.Combine(temp, "metadata.ini"));
      Assert.Multiple(() => {
        Assert.That(metadata, Does.Contain("product_version=17.4.1"));
        Assert.That(metadata, Does.Contain("build_version=21E236"));
        Assert.That(metadata, Does.Contain("identifier=iPhone15,3"));
        Assert.That(metadata, Does.Contain("total_zip_entries=7"));
      });
    } finally {
      Directory.Delete(temp, recursive: true);
    }
  }
}
