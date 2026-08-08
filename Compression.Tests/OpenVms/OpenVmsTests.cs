using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

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

  /// <summary>
  /// Locks the OpenVMS Files-11 capability surface at the workbench-layout R/W
  /// scope: <see cref="IArchiveCreatable"/> + <see cref="IArchiveModifiable"/>
  /// on the descriptor, <see cref="FormatCapabilities.CanCreate"/> +
  /// <see cref="FormatCapabilities.CanModify"/> on the capability flag.
  /// VMS-mountability stays deferred per the Description — the in-place
  /// modifier round-trips through our own writer/reader pair, not real
  /// OpenVMS, and the Description documents which surfaces (FILECHAR,
  /// RECATTR, home-block checksums, variable-length dir records) remain
  /// out of scope.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Descriptor_WormPlusInPlace_NotOpenVmsMountable() {
    var d = new FileSystem.OpenVms.OpenVmsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "OpenVMS descriptor must advertise IArchiveCreatable — OpenVmsWriter emits a real ODS-2 home block + INDEXF.SYS + BITMAP.SYS + 000000.DIR layout.");
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
      "OpenVMS descriptor must advertise IArchiveModifiable — OpenVmsInPlaceModifier handles Add/Remove/Replace by mutating only the touched bitmap sector, INDEXF.SYS slot, directory block, and data LBNs.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d.Description, Does.Contain("not OpenVMS-mountable"),
      "Description must keep the honest scope notice — emitted volumes don't satisfy a real OpenVMS validator.");
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_HomeBlock_ReturnsBoundedStream_ReadPastSizeReturnsZero() {
    var img = BuildMinimal(ods5: false);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.OpenVms.OpenVmsFormatDescriptor();

    using var s = d.OpenEntry(ms, "home_block.bin", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>(), "OpenEntry must return BoundedEntryStream");
    Assert.That(s.Length, Is.EqualTo(512));

    var buf = new byte[1024];
    var n = s.Read(buf, 0, buf.Length);
    Assert.That(n, Is.EqualTo(512));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0), "read past LogicalSize returns 0 (EOF)");
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_FullDisk_ReturnsBoundedStreamOfWholeImage() {
    var img = BuildMinimal(ods5: false);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.OpenVms.OpenVmsFormatDescriptor();

    using var s = d.OpenEntry(ms, "FULL.disk", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(img.Length));

    var buf = new byte[img.Length + 16];
    var n = s.Read(buf, 0, buf.Length);
    Assert.That(n, Is.EqualTo(img.Length));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0), "read past LogicalSize returns 0 (EOF)");
  }

  [Test, Category("Sad")]
  public void OpenEntry_UnknownName_ReturnsEmptyBoundedStream() {
    var img = BuildMinimal(ods5: false);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.OpenVms.OpenVmsFormatDescriptor();
    using var s = d.OpenEntry(ms, "INDEXF.SYS", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(0),
      "Real ODS-2 user-file extraction is deferred — INDEXF.SYS must surface as empty until the walker lands.");
  }
}
