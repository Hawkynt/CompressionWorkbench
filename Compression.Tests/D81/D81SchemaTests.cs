namespace Compression.Tests.D81;

[TestFixture]
public class D81SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelAndDiskIdSchema() {
    var d = new FileSystem.D81.D81FormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "DiskId"), Is.True);
  }

  private static int HeaderOffset() {
    // Track 40 sector 0: 39 tracks × 40 sectors × 256 = 399 360.
    return 39 * 40 * 256;
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_EncodesIntoHeader() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.D81.D81FormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "BIGDISK" },
        });
      var bytes = ms.ToArray();
      var off = HeaderOffset() + 4;
      Assert.That(System.Text.Encoding.ASCII.GetString(bytes, off, 7), Is.EqualTo("BIGDISK"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_DiskId_EncodesIntoHeader() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.D81.D81FormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["DiskId"] = "5K" },
        });
      var bytes = ms.ToArray();
      var off = HeaderOffset();
      Assert.That(bytes[off + 0x16], Is.EqualTo((byte)'5'));
      Assert.That(bytes[off + 0x17], Is.EqualTo((byte)'K'));
    } finally {
      File.Delete(tmp);
    }
  }
}
