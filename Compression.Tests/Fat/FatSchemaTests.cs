using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// Smoke coverage for <see cref="FatFormatDescriptor"/>'s
/// <see cref="IFormatOptionsSchema"/> wiring — verifies the published knobs
/// match the spec and that Create() honours a non-default FatType selection.
/// </summary>
[TestFixture]
public class FatSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsFormatOptionsSchema() {
    var desc = new FatFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IFormatOptionsSchema>());
  }

  [Test, Category("HappyPath")]
  public void Schema_ContainsExpectedKeys() {
    var desc = (IFormatOptionsSchema)new FatFormatDescriptor();
    var keys = desc.OptionsSchema.Select(o => o.Key).ToHashSet();
    Assert.That(keys, Does.Contain("FatType"));
    Assert.That(keys, Does.Contain("ClusterSize"));
    Assert.That(keys, Does.Contain("ImageSize"));
    Assert.That(keys, Does.Contain("VolumeLabel"));
    // FatType is an enum with the four allowed variants.
    var fatTypeOpt = desc.OptionsSchema.First(o => o.Key == "FatType");
    Assert.That(fatTypeOpt.Kind, Is.EqualTo(FormatOptionKind.Enum));
    Assert.That(fatTypeOpt.AllowedValues, Is.EqualTo(new[] { "Auto", "FAT12", "FAT16", "FAT32" }));
  }

  [Test, Category("HappyPath")]
  public void Create_WithFatType16_ProducesFat16Image() {
    // Build a tiny payload, feed it through Create() with FatType=FAT16 and a
    // large enough ImageSize to satisfy the FAT16 4085-cluster minimum, then
    // inspect the BPB to confirm BS_FilSysType == "FAT16   ". This proves the
    // option flowed all the way through to the writer (not just lived in the
    // schema).
    var desc = new FatFormatDescriptor();
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, "hi"u8.ToArray());
      var opts = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["FatType"] = "FAT16",
          ["ImageSize"] = "32 MB",
        },
      };
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmpFile, "TEST.TXT", false)], opts);
      var image = ms.ToArray();

      // FAT16 has its BS_FilSysType at offset 54 (short BPB layout).
      var fileSysType = System.Text.Encoding.ASCII.GetString(image, 54, 8);
      Assert.That(fileSysType, Is.EqualTo("FAT16   "), "BS_FilSysType must read FAT16 when FatType=FAT16 is requested.");
    } finally {
      File.Delete(tmpFile);
    }
  }
}
