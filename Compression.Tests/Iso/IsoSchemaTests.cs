namespace Compression.Tests.Iso;

[TestFixture]
public class IsoSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesIsoIdentifierSchema() {
    var d = new FileSystem.Iso.IsoFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "SystemId"), Is.True);
    Assert.That(schema.Any(o => o.Key == "Publisher"), Is.True);
    Assert.That(schema.Any(o => o.Key == "Application"), Is.True);
    Assert.That(schema.Any(o => o.Key == "Joliet"), Is.True);
  }

  [Test, Category("Spec")]
  public void Create_VolumeLabel_EncodesIntoPvd() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Iso.IsoFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYISO" },
        });
      // PVD at sector 16 (offset 16 * 2048 = 32768). Volume ID at PVD+40, 32 bytes ASCII.
      var bytes = ms.ToArray();
      var pvdOff = 16 * 2048;
      var volId = System.Text.Encoding.ASCII.GetString(bytes, pvdOff + 40, 5);
      Assert.That(volId, Is.EqualTo("MYISO"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_Publisher_EncodesIntoPvd() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Iso.IsoFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["Publisher"] = "ACME" },
        });
      var bytes = ms.ToArray();
      var pvdOff = 16 * 2048;
      var pub = System.Text.Encoding.ASCII.GetString(bytes, pvdOff + 318, 4);
      Assert.That(pub, Is.EqualTo("ACME"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_Joliet_TogglesSecondVolumeDescriptor() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.Iso.IsoFormatDescriptor();
      using var on = new MemoryStream();
      using var off = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        on, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["Joliet"] = "true" },
        });
      ((Compression.Registry.IArchiveCreatable)d).Create(
        off, [new Compression.Registry.ArchiveInputInfo(tmp, "A", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["Joliet"] = "false" },
        });
      // With Joliet on: sector 17 = SVD (type 2). With Joliet off: sector 17 = terminator (type 0xFF).
      Assert.That(on.ToArray()[17 * 2048], Is.EqualTo((byte)2), "SVD type with Joliet on");
      Assert.That(off.ToArray()[17 * 2048], Is.EqualTo((byte)0xFF), "Terminator at sector 17 with Joliet off");
    } finally {
      File.Delete(tmp);
    }
  }
}
