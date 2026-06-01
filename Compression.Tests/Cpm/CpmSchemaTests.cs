namespace Compression.Tests.Cpm;

[TestFixture]
public class CpmSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesUserCodeSchema() {
    var d = new FileSystem.Cpm.CpmFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "UserCode"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_UserCode_EncodesIntoDirectoryEntry() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Cpm.CpmFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "TEST.TXT", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["UserCode"] = "5" },
        });
      var bytes = ms.ToArray();
      // CP/M reserved area = 2 tracks × 26 × 128 = 6656; first directory entry user code at +0.
      var entryOff = 2 * 26 * 128;
      Assert.That(bytes[entryOff], Is.EqualTo((byte)5));
    } finally {
      File.Delete(tmp);
    }
  }
}
