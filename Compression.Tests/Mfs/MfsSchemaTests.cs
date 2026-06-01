namespace Compression.Tests.Mfs;

[TestFixture]
public class MfsSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new FileSystem.Mfs.MfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_EncodesIntoMdb() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "mac"u8.ToArray());
      var d = new FileSystem.Mfs.MfsFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "Macintosh" },
        });
      var bytes = ms.ToArray();
      // MDB at offset 1024, volume name Pascal string at +36 (length byte + ASCII).
      var len = bytes[1024 + 36];
      Assert.That(len, Is.EqualTo((byte)9));
      var name = System.Text.Encoding.ASCII.GetString(bytes, 1024 + 37, 9);
      Assert.That(name, Is.EqualTo("Macintosh"));
    } finally {
      File.Delete(tmp);
    }
  }
}
