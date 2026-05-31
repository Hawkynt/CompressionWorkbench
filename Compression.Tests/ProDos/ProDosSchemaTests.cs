namespace Compression.Tests.ProDos;

[TestFixture]
public class ProDosSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesImageSizeAndVolumeLabelSchema() {
    var d = new FileSystem.ProDos.ProDosFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IFormatOptionsSchema>());
    var schema = ((Compression.Registry.IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "ImageSize"), Is.True);
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Create_AutoSize_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "prodos data"u8.ToArray());
      var d = new FileSystem.ProDos.ProDosFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "FILE1", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_Explicit800K_ProducesEightHundredKVolume() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, new byte[100]);
      var d = new FileSystem.ProDos.ProDosFormatDescriptor();
      using var big = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        big, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["ImageSize"] = "800 KB (3.5\")" },
        });
      Assert.That(big.Length, Is.EqualTo(1600L * 512), "800 KB = 1600 × 512-byte blocks");
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_Explicit140K_ProducesFloppyVolume() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, new byte[100]);
      var d = new FileSystem.ProDos.ProDosFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "F", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["ImageSize"] = "140 KB (5.25\")" },
        });
      Assert.That(ms.Length, Is.EqualTo(280L * 512), "140 KB = 280 × 512-byte blocks");
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("RoundTrip")]
  public void Create_VolumeLabel_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "x"u8.ToArray());
      var d = new FileSystem.ProDos.ProDosFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms, [new Compression.Registry.ArchiveInputInfo(tmp, "FILE1", false)],
        new Compression.Registry.FormatCreateOptions {
          FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYVOL" },
        });
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1), "labelled volume must still round-trip its file");
    } finally {
      File.Delete(tmp);
    }
  }
}
