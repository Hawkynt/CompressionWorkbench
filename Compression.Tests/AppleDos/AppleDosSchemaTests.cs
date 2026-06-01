namespace Compression.Tests.AppleDos;

[TestFixture]
public class AppleDosSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeNumberSchema() {
    var d = new FileSystem.AppleDos.AppleDosFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeNumber"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeNumber_EncodesIntoVtoc() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.AppleDos.AppleDosFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeNumber"] = "77" },
        });
      // VTOC at track 17 sector 0 = offset 17 * 16 * 256; volume number at +0x06.
      var vtocOff = 17 * 16 * 256;
      Assert.That(ms.ToArray()[vtocOff + 0x06], Is.EqualTo((byte)77));
    } finally {
      File.Delete(tmp);
    }
  }
}
