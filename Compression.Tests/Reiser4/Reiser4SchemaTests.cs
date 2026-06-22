using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// Schema-knob contract tests for <see cref="Reiser4FormatDescriptor"/>: proves
/// the published <c>VolumeLabel</c> and <c>ImageSize</c> options are real knobs
/// the empty-filesystem writer honours and the master superblock reads back.
/// </summary>
[TestFixture]
public class Reiser4SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelAndImageSizeSchema() {
    var d = new Reiser4FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "ImageSize"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabel_LandsInMasterSuperblock() {
    var d = new Reiser4FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "R4VOL" } });

    var sb = Reiser4MasterSb.TryParse(ms.ToArray());
    Assert.That(sb.Valid, Is.True);
    Assert.That(sb.Label, Is.EqualTo("R4VOL"), "VolumeLabel must land in the master superblock.");
  }

  [Test, Category("HappyPath")]
  public void Create_ImageSize_DrivesBlockCount() {
    var d = new Reiser4FormatDescriptor();
    using var ms = new MemoryStream();
    // 32 MB / 4 KB = 8192 blocks (above the 4096-block minimum).
    d.Create(ms, [],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["ImageSize"] = "32 MB" } });

    var bytes = ms.ToArray();
    Assert.That(bytes.Length, Is.EqualTo(32 * 1024 * 1024), "Image size must match the requested 32 MB.");
    var sb = Reiser4MasterSb.TryParse(bytes);
    Assert.That(sb.Valid, Is.True);
    Assert.That(sb.Format40Present, Is.True);
    Assert.That(sb.BlockCount, Is.EqualTo(8192UL), "ImageSize must drive the format40 block count.");
  }
}
