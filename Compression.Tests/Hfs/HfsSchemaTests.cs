namespace Compression.Tests.Hfs;

[TestFixture]
public class HfsSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new FileSystem.Hfs.HfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_ChangesImageBytes() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "hfs"u8.ToArray());
      var d = new FileSystem.Hfs.HfsFormatDescriptor();
      using var def = new MemoryStream();
      using var custom = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        def, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions());
      ((Compression.Registry.IArchiveCreatable)d).Create(
        custom, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MyDisk" },
        });
      Assert.That(custom.ToArray(), Is.Not.EqualTo(def.ToArray()),
        "Setting a non-default VolumeLabel must produce a different image.");
    } finally {
      File.Delete(tmp);
    }
  }
}
