using Compression.Registry;
using FileSystem.Gfs2;

namespace Compression.Tests.Gfs2;

/// <summary>
/// Schema-knob contract tests for <see cref="Gfs2FormatDescriptor"/>: proves the
/// published <c>ImageSize</c> and <c>LockTable</c> options are real knobs the
/// empty-volume writer honours and the superblock reads back.
/// </summary>
[TestFixture]
public class Gfs2SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesImageSizeAndLockTableSchema() {
    var d = new Gfs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "ImageSize"), Is.True);
    Assert.That(schema.Any(o => o.Key == "LockTable"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_LockTable_LandsInSuperblock() {
    var d = new Gfs2FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["LockTable"] = "mycluster:myvol" } });

    ms.Position = 0;
    var r = new Gfs2Reader(ms);
    Assert.That(r.SuperblockValid, Is.True);
    Assert.That(r.LockTable, Is.EqualTo("mycluster:myvol"), "LockTable must land in sb_locktable.");
  }

  [Test, Category("HappyPath")]
  public void Create_ImageSize_SizesTheVolume() {
    var d = new Gfs2FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["ImageSize"] = "64 MB" } });

    Assert.That(ms.ToArray().LongLength, Is.EqualTo(64L * 1024 * 1024), "ImageSize must size the volume.");
    ms.Position = 0;
    Assert.That(new Gfs2Reader(ms).SuperblockValid, Is.True);
  }

  [Test, Category("Equivalence")]
  public void Create_DefaultLockTable_IsEmpty() {
    var d = new Gfs2FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [], new FormatCreateOptions());
    ms.Position = 0;
    Assert.That(new Gfs2Reader(ms).LockTable, Is.EqualTo(""));
  }
}
