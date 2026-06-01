namespace Compression.Tests.D64;

[TestFixture]
public class D64SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelAndDiskIdSchema() {
    var d = new FileSystem.D64.D64FormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "DiskId"), Is.True);
  }

  private static int BamOffset() {
    // Track 18 sector 0: skip tracks 1..17. Tracks 1-17 each have 21 sectors of 256 bytes.
    return 17 * 21 * 256;
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_EncodesIntoBam() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.D64.D64FormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYDISK" },
        });
      var bytes = ms.ToArray();
      var off = BamOffset() + 0x90;
      Assert.That(System.Text.Encoding.ASCII.GetString(bytes, off, 6), Is.EqualTo("MYDISK"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_DiskId_EncodesIntoBam() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.D64.D64FormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["DiskId"] = "XY" },
        });
      var bytes = ms.ToArray();
      var off = BamOffset();
      Assert.That(bytes[off + 0xA2], Is.EqualTo((byte)'X'));
      Assert.That(bytes[off + 0xA3], Is.EqualTo((byte)'Y'));
    } finally {
      File.Delete(tmp);
    }
  }
}
