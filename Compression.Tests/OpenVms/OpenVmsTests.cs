using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.OpenVms;

[TestFixture]
public class OpenVmsTests {

  /// <summary>
  /// Synthesize a Files-11 home block at LBN 1 (offset 512). Format string lives
  /// at offset 0x1E8 inside the home block, volume label at 0x1F4. The structure
  /// level (0x0202 = ODS-2) sits at offset 0x00C.
  /// </summary>
  private static byte[] BuildMinimal(bool ods5 = false) {
    var image = new byte[2048];
    var hb = 512;
    // structure level at +0x00C
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(hb + 0x00C, 2), (ushort)(ods5 ? 0x0205 : 0x0202));
    // cluster size at +0x00E
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(hb + 0x00E, 2), 4);
    // index bitmap LBN
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(hb + 0x028, 4), 100);
    // max files
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(hb + 0x02C, 4), 4096);
    // owner UIC
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(hb + 0x036, 4), 0x00010001);
    // format string at +0x1E8
    var fmt = ods5 ? "DECFILE11B " : "DECFILE11A ";
    Encoding.ASCII.GetBytes(fmt).CopyTo(image.AsSpan(hb + 0x1E8));
    // volume label at +0x1F4
    Encoding.ASCII.GetBytes("VMSVOL").CopyTo(image.AsSpan(hb + 0x1F4));
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.OpenVms.OpenVmsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("OpenVms"));
    Assert.That(d.DisplayName, Is.EqualTo("OpenVMS Files-11"));
    Assert.That(d.Extensions, Does.Contain(".ods2"));
    Assert.That(d.Extensions, Does.Contain(".ods5"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(2));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(1000));
  }

  [Test, Category("HappyPath")]
  public void List_Ods2_EmitsHomeBlock() {
    var img = BuildMinimal(ods5: false);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.OpenVms.OpenVmsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.disk"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("home_block.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_Ods5_WritesParsedHeader() {
    var img = BuildMinimal(ods5: true);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.OpenVms.OpenVmsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "ods_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.disk")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "home_block.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("structure_name=ODS-5"));
      Assert.That(meta, Does.Contain("volume_label=VMSVOL"));
      Assert.That(meta, Does.Contain("cluster_size=4"));
      Assert.That(meta, Does.Contain("max_files=4096"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_NoHomeBlock_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[2048]);
    var d = new FileSystem.OpenVms.OpenVmsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.disk"));
  }

  [Test, Category("ErrorHandling")]
  public void List_TinyImage_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[16]);
    var d = new FileSystem.OpenVms.OpenVmsFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
  }
}
