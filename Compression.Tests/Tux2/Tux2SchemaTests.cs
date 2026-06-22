using Compression.Registry;
using FileSystem.Tux2;

namespace Compression.Tests.Tux2;

/// <summary>
/// Schema-knob contract tests for <see cref="Tux2FormatDescriptor"/>: proves the
/// published <c>Version</c> option is a real knob the writer honours and the
/// reader reads back.
/// </summary>
[TestFixture]
public class Tux2SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVersionSchema() {
    var d = new Tux2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "Version"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_Version_TakesEffectAndFilesRoundTrip() {
    var d = new Tux2FormatDescriptor();
    var payload = "tux2 version knob"u8.ToArray();
    using var ms = new MemoryStream();
    d.Create(ms,
      [ArchiveInputInfo.InMemory("note.txt", payload)],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Version"] = "7" } });

    ms.Position = 0;
    var r = new Tux2Reader(ms);
    Assert.That(r.Version, Is.EqualTo(7u), "Version knob must land in the header.");
    var entry = r.Entries.Single(e => e.Name == "note.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(payload), "File must round-trip.");
  }

  [Test, Category("Equivalence")]
  public void Create_DefaultVersion_IsOne() {
    var d = new Tux2FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    ms.Position = 0;
    Assert.That(new Tux2Reader(ms).Version, Is.EqualTo(1u));
  }
}
