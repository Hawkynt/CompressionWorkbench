namespace Compression.Tests.CpcDsk;

[TestFixture]
public class CpcDskSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesTrackSchema() {
    var d = new FileSystem.CpcDsk.CpcDskFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "Tracks"), Is.True);
    Assert.That(schema.Any(o => o.Key == "Sides"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_DoubleSided_EncodesIntoDiskInfoHeader() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.CpcDsk.CpcDskFormatDescriptor();
      using var ss = new MemoryStream();
      using var ds = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ss, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["Sides"] = "1" },
        });
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ds, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["Sides"] = "2" },
        });
      // Sides byte at disk-info offset 49.
      Assert.That(ss.ToArray()[49], Is.EqualTo((byte)1));
      Assert.That(ds.ToArray()[49], Is.EqualTo((byte)2));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_EightyTracks_EncodesIntoDiskInfoHeader() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.CpcDsk.CpcDskFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["Tracks"] = "80" },
        });
      // Tracks byte at disk-info offset 48.
      Assert.That(ms.ToArray()[48], Is.EqualTo((byte)80));
    } finally {
      File.Delete(tmp);
    }
  }
}
