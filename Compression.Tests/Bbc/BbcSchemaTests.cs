namespace Compression.Tests.Bbc;

[TestFixture]
public class BbcSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelAndBootOptionSchema() {
    var d = new FileSystem.Bbc.BbcFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "BootOption"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_EncodesIntoCatalog() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Bbc.BbcFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "TESTLABEL" },
        });
      var bytes = ms.ToArray();
      // First 8 chars of title at offset 0; uppercase
      var titlePart = System.Text.Encoding.ASCII.GetString(bytes, 0, 8);
      Assert.That(titlePart, Is.EqualTo("TESTLABE"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_BootOption_TogglesOptByte() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Bbc.BbcFormatDescriptor();
      using var none = new MemoryStream();
      using var run = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        none, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["BootOption"] = "None" },
        });
      ((Compression.Registry.IArchiveCreatable)d).Create(
        run, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["BootOption"] = "RUN" },
        });
      // boot bits at sector 1 byte 6, bits 4-5. Sector size = 256.
      Assert.That((none.ToArray()[256 + 6] >> 4) & 0x03, Is.EqualTo(0));
      Assert.That((run.ToArray()[256 + 6] >> 4) & 0x03, Is.EqualTo(2));
    } finally {
      File.Delete(tmp);
    }
  }
}
