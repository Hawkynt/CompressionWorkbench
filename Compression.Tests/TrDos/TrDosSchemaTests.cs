namespace Compression.Tests.TrDos;

[TestFixture]
public class TrDosSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new FileSystem.TrDos.TrDosFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_EncodesIntoDiskInfoSector() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.TrDos.TrDosFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "SPECTRUM" },
        });
      // Disk-info sector at offset 0x800; label at offset 0xF5, 8 bytes ASCII.
      var label = System.Text.Encoding.ASCII.GetString(ms.ToArray(), 0x800 + 0xF5, 8);
      Assert.That(label, Is.EqualTo("SPECTRUM"));
    } finally {
      File.Delete(tmp);
    }
  }
}
