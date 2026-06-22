using Compression.Registry;
using FileSystem.ReiserFs;

namespace Compression.Tests.ReiserFs;

/// <summary>
/// Schema-knob contract tests for <see cref="ReiserFsFormatDescriptor"/>: proves
/// the published <c>VolumeLabel</c> option is a real knob the writer stamps into
/// <c>s_label</c> and the reader reads back, with files still round-tripping.
/// </summary>
[TestFixture]
public class ReiserFsSchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new ReiserFsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabel_TakesEffectAndFilesRoundTrip() {
    var d = new ReiserFsFormatDescriptor();
    var payload = "reiserfs label knob"u8.ToArray();
    using var ms = new MemoryStream();
    d.Create(ms,
      [ArchiveInputInfo.InMemory("note.txt", payload)],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYREISER" } });

    ms.Position = 0;
    var r = new ReiserFsReader(ms);
    Assert.That(r.Label, Is.EqualTo("MYREISER"), "VolumeLabel must land in s_label.");
    var entry = r.Entries.Single(e => !e.IsDirectory && e.Name == "note.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(payload), "File must round-trip.");
  }

  [Test, Category("Equivalence")]
  public void Create_DefaultLabel_MatchesWriterDefault() {
    var d = new ReiserFsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    ms.Position = 0;
    Assert.That(new ReiserFsReader(ms).Label, Is.EqualTo("worm"));
  }
}
