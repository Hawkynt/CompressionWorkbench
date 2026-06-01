namespace Compression.Tests.Lif;

[TestFixture]
public class LifSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesLifSchema() {
    var d = new FileSystem.Lif.LifFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "DirectorySectors"), Is.True);
    Assert.That(schema.Any(o => o.Key == "DefaultFileType"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_EncodesIntoVolumeHeader() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Lif.LifFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "HELLO" },
        });
      // Label at byte offset 2, 6 ASCII chars.
      var label = System.Text.Encoding.ASCII.GetString(ms.ToArray(), 2, 5);
      Assert.That(label, Is.EqualTo("HELLO"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_DefaultFileType_EncodesIntoDirectoryEntry() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Lif.LifFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["DefaultFileType"] = "TEXT (0xE0F0)" },
        });
      // Directory starts at sector 2 (offset 512); first dir entry file-type BE at +10.
      var dirOff = 512;
      var ft = (ms.ToArray()[dirOff + 10] << 8) | ms.ToArray()[dirOff + 11];
      Assert.That(ft, Is.EqualTo(0xE0F0));
    } finally {
      File.Delete(tmp);
    }
  }
}
