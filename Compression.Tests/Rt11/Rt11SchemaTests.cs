namespace Compression.Tests.Rt11;

[TestFixture]
public class Rt11SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesRt11Schema() {
    var d = new FileSystem.Rt11.Rt11FormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "DirectorySegments"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_EncodesIntoHomeBlock() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "data"u8.ToArray());
      var d = new FileSystem.Rt11.Rt11FormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "TEST.DAT", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "PDP-11VOL" },
        });
      // Home block at block 1 (= offset 512), volume id at +0x1D8, 12 ASCII bytes.
      var bytes = ms.ToArray();
      var off = 512 + 0x1D8;
      var label = System.Text.Encoding.ASCII.GetString(bytes, off, 9);
      Assert.That(label, Is.EqualTo("PDP-11VOL"));
    } finally {
      File.Delete(tmp);
    }
  }
}
