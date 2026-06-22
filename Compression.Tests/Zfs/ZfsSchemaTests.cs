using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Schema-knob contract tests for <see cref="ZfsFormatDescriptor"/>: proves the
/// published <c>VolumeLabel</c> (pool name) and <c>ImageSize</c> options are real
/// knobs the WORM pool writer honours and the reader reads back, with files still
/// round-tripping.
/// </summary>
[TestFixture]
public class ZfsSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelAndImageSizeSchema() {
    var d = new ZfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
    Assert.That(schema.Any(o => o.Key == "ImageSize"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_PoolNameAndImageSize_TakeEffectAndFilesRoundTrip() {
    var d = new ZfsFormatDescriptor();
    var payload = "zfs pool knob"u8.ToArray();
    using var ms = new MemoryStream();
    d.Create(ms,
      [ArchiveInputInfo.InMemory("note.txt", payload)],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["VolumeLabel"] = "mypool",
          ["ImageSize"] = "128 MB",
        },
      });

    Assert.That(ms.Length, Is.EqualTo(128L * 1024 * 1024), "ImageSize must size the pool image.");

    ms.Position = 0;
    var r = new ZfsReader(ms);
    Assert.That(r.PoolName, Is.EqualTo("mypool"), "VolumeLabel must land in the vdev-label NVList name.");
    var entry = r.Entries.Single(e => !e.IsDirectory && e.Name == "note.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(payload), "File must round-trip.");
  }

  [Test, Category("Equivalence")]
  public void Create_DefaultPoolName_MatchesWriterDefault() {
    var d = new ZfsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    ms.Position = 0;
    Assert.That(new ZfsReader(ms).PoolName, Is.EqualTo("compworkbench"));
  }
}
