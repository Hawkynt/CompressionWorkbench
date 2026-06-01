namespace Compression.Tests.Atari8;

[TestFixture]
public class Atari8SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesWriteProtectSchema() {
    var d = new FileSystem.Atari8.Atari8FormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "WriteProtect"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_WriteProtect_SetsAtrHeaderFlag() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Atari8.Atari8FormatDescriptor();
      using var on = new MemoryStream();
      using var off = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        on, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["WriteProtect"] = "true" },
        });
      ((Compression.Registry.IArchiveCreatable)d).Create(
        off, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["WriteProtect"] = "false" },
        });
      Assert.That(on.ToArray()[15], Is.EqualTo((byte)0x01), "WP on => flag byte 15 = 0x01");
      Assert.That(off.ToArray()[15], Is.EqualTo((byte)0x00), "WP off => flag byte 15 = 0x00");
    } finally {
      File.Delete(tmp);
    }
  }
}
