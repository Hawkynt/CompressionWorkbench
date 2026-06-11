using System.Text;
using Compression.Registry;
using FileFormat.Ova;
using FileFormat.Tar;

namespace Compression.Tests.Ova;

[TestFixture]
public class OvaTests {

  [Test, Category("HappyPath")]
  public void SourceGenerator_RegistersAllNewDescriptors() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    foreach (var id in new[] { "Ova", "CloneCd", "Qed", "BochsDisk", "Afio" })
      Assert.That(FormatRegistry.GetById(id), Is.Not.Null, $"Descriptor '{id}' was not registered.");
  }

  private const string OvfXml =
    "<?xml version=\"1.0\"?>\n" +
    "<Envelope xmlns:ovf=\"http://schemas.dmtf.org/ovf/envelope/1\">\n" +
    "  <References><File ovf:href=\"disk1.vmdk\"/></References>\n" +
    "  <DiskSection><Disk ovf:diskId=\"vmdisk1\"/></DiskSection>\n" +
    "  <VirtualSystem ovf:id=\"TestAppliance\">\n" +
    "    <OperatingSystemSection ovf:id=\"94\">\n" +
    "      <Description>Linux 64-bit</Description>\n" +
    "    </OperatingSystemSection>\n" +
    "  </VirtualSystem>\n" +
    "</Envelope>\n";

  private static byte[] BuildSyntheticOva() {
    using var ms = new MemoryStream();
    using (var w = new TarWriter(ms, leaveOpen: true)) {
      w.AddEntry(new TarEntry { Name = "appliance.ovf" }, Encoding.UTF8.GetBytes(OvfXml));
      w.AddEntry(new TarEntry { Name = "disk1.vmdk" }, new byte[] { 1, 2, 3, 4, 5 });
      w.AddEntry(new TarEntry { Name = "appliance.mf" }, Encoding.UTF8.GetBytes("SHA1(disk1.vmdk)=deadbeef\n"));
      w.Finish();
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new OvaFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ova"));
    Assert.That(d.Extensions, Contains.Item(".ova"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndMembers() {
    var img = BuildSyntheticOva();
    var d = new OvaFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);

    Assert.That(entries[0].Name, Is.EqualTo("FULL.ova"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(img.Length));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "appliance.ovf"), Is.True);
    Assert.That(entries.Any(e => e.Name == "disk1.vmdk"), Is.True);
    Assert.That(entries.Any(e => e.Name == "appliance.mf"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndMetadataParsesOvf() {
    var img = BuildSyntheticOva();
    var d = new OvaFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "ova_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.ova"));
      Assert.That(full, Is.EqualTo(img));

      var disk = File.ReadAllBytes(Path.Combine(dir, "disk1.vmdk"));
      Assert.That(disk, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("disk_count=1"));
      Assert.That(meta, Does.Contain("ovf_member=appliance.ovf"));
      Assert.That(meta, Does.Contain("vm_name=TestAppliance"));
      Assert.That(meta, Does.Contain("os_description=Linux 64-bit"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow_FullAndPartialMetadata() {
    var garbage = new byte[600];
    Array.Fill(garbage, (byte)0x5A);
    var d = new OvaFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "ova_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      List<Compression.Registry.ArchiveEntryInfo>? entries = null;
      Assert.DoesNotThrow(() => entries = d.List(ms, null));
      Assert.That(entries![0].Name, Is.EqualTo("FULL.ova"));

      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.ova"));
      Assert.That(full, Is.EqualTo(garbage));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
