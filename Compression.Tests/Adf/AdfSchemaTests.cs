namespace Compression.Tests.Adf;

[TestFixture]
public class AdfSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelAndFileSystemTypeSchema() {
    var d = new FileSystem.Adf.AdfFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "FileSystemType"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_EncodesIntoRootBlock() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Adf.AdfFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYVOL" },
        });
      // Root block at sector 880; volume name BCPL string at offset 432 inside it.
      var bytes = ms.ToArray();
      var rootOff = 880 * 512;
      var nameLen = bytes[rootOff + 432];
      var name = System.Text.Encoding.ASCII.GetString(bytes, rootOff + 433, nameLen);
      Assert.That(name, Is.EqualTo("MYVOL"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_FileSystemType_TogglesBootByte() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Adf.AdfFormatDescriptor();
      using var ffs = new MemoryStream();
      using var ofs = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ffs, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["FileSystemType"] = "FFS" },
        });
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ofs, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["FileSystemType"] = "OFS" },
        });
      Assert.That(ffs.ToArray()[3], Is.EqualTo((byte)1), "FFS boot byte = 0x01");
      Assert.That(ofs.ToArray()[3], Is.EqualTo((byte)0), "OFS boot byte = 0x00");
    } finally {
      File.Delete(tmp);
    }
  }
}
