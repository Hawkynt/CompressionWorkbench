using Compression.Registry;
using FileSystem.Erofs;

namespace Compression.Tests.Erofs;

/// <summary>
/// Schema-knob contract tests for <see cref="ErofsFormatDescriptor"/>: proves the
/// published <c>VolumeLabel</c> option is a real knob the writer stamps into the
/// superblock <c>volume_name</c> field and the reader reads back, with files
/// still round-tripping.
/// </summary>
[TestFixture]
public class ErofsSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new ErofsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabel_TakesEffectAndFilesRoundTrip() {
    var d = new ErofsFormatDescriptor();
    var payload = "erofs label knob"u8.ToArray();
    using var ms = new MemoryStream();
    d.Create(ms,
      [ArchiveInputInfo.InMemory("note.txt", payload)],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "EROFSVOL" } });

    var r = new ErofsReader(ms.ToArray());
    Assert.That(r.VolumeName, Is.EqualTo("EROFSVOL"), "VolumeLabel must land in the superblock volume_name.");
    var entry = r.Entries.Single(e => !e.IsDirectory && e.Path == "note.txt");
    Assert.That(r.ExtractFile(entry), Is.EqualTo(payload), "File must round-trip.");
  }

  [Test, Category("Equivalence")]
  public void Create_NoLabel_LeavesVolumeNameEmpty() {
    var d = new ErofsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    Assert.That(new ErofsReader(ms.ToArray()).VolumeName, Is.EqualTo(""));
  }
}
