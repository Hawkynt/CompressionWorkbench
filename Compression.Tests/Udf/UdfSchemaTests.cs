namespace Compression.Tests.Udf;

[TestFixture]
public class UdfSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new FileSystem.Udf.UdfFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_RoundTripsThroughReader() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "udfdata"u8.ToArray());
      var d = new FileSystem.Udf.UdfFormatDescriptor();
      using var custom = new MemoryStream();
      using var def    = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        custom, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "TESTVOL" },
        });
      ((Compression.Registry.IArchiveCreatable)d).Create(
        def, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions());
      // Different volume labels => different image bytes
      Assert.That(custom.ToArray(), Is.Not.EqualTo(def.ToArray()),
        "Setting a non-default VolumeLabel must produce a different image.");
    } finally {
      File.Delete(tmp);
    }
  }
}
