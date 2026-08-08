using Compression.Registry;
using FileSystem.AdvFs;

namespace Compression.Tests.AdvFs;

/// <summary>
/// Schema-knob contract tests for <see cref="AdvFsFormatDescriptor"/>: proves the
/// published <c>VolumeLabel</c> option is a real knob the writer stamps into the
/// BSR_VD_ATTR volume tag and the reader reads back.
/// </summary>
[TestFixture]
public class AdvFsSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new AdvFsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabel_TakesEffectAndFilesRoundTrip() {
    var d = new AdvFsFormatDescriptor();
    var payload = "advfs label knob"u8.ToArray();
    using var ms = new MemoryStream();
    d.Create(ms,
      [ArchiveInputInfo.InMemory("note.txt", payload)],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYDOMAIN" } });

    ms.Position = 0;
    var r = new AdvFsReader(ms);
    Assert.That(r.Valid, Is.True);
    Assert.That(r.VolumeTag, Is.EqualTo("MYDOMAIN"), "VolumeLabel must land in the BSR_VD_ATTR volume tag.");
    var entry = r.FileTableEntries.Single(e => e.Name == "note.txt");
    Assert.That(r.ExtractFile(entry), Is.EqualTo(payload), "File must round-trip.");
  }

  [Test, Category("Equivalence")]
  public void Create_DefaultLabel_MatchesWriterDefault() {
    var d = new AdvFsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    ms.Position = 0;
    Assert.That(new AdvFsReader(ms).VolumeTag, Is.Empty,
      "An unasked-for domain carries no tag, the way mkfdmn leaves one it was given no name for.");
  }
}
