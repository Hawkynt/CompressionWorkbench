using Compression.Registry;
using FileSystem.Tux3;

namespace Compression.Tests.Tux3;

/// <summary>
/// Schema-knob contract tests for <see cref="Tux3FormatDescriptor"/>: proves the
/// published <c>Birthday</c> option is a real knob the writer stamps into the
/// superblock and the reader reads back.
/// </summary>
[TestFixture]
public class Tux3SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesBirthdaySchema() {
    var d = new Tux3FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "Birthday"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_Birthday_TakesEffectAndFilesRoundTrip() {
    var d = new Tux3FormatDescriptor();
    var payload = "tux3 birthday knob"u8.ToArray();
    using var ms = new MemoryStream();
    d.Create(ms,
      [ArchiveInputInfo.InMemory("note.txt", payload)],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Birthday"] = "0xCAFEF00DBAADBEEF" } });

    ms.Position = 0;
    var r = new Tux3Reader(ms);
    Assert.That(r.Birthday, Is.EqualTo(0xCAFEF00DBAADBEEFUL), "Birthday knob must land in the superblock.");
    var entry = r.Entries.Single(e => e.Name == "note.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(payload), "File must round-trip.");
  }

  [Test, Category("Equivalence")]
  public void Create_DefaultBirthday_MatchesWriterPlaceholder() {
    var d = new Tux3FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    ms.Position = 0;
    Assert.That(new Tux3Reader(ms).Birthday, Is.EqualTo(0x_5455_5833_4253_4831UL));
  }
}
