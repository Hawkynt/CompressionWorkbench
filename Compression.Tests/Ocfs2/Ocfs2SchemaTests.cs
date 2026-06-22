using Compression.Registry;
using FileSystem.Ocfs2;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// Schema-knob contract tests for <see cref="Ocfs2FormatDescriptor"/>: proves the
/// published <c>VolumeLabel</c> option is a real knob the writer stamps into
/// <c>s_label</c> and the superblock reads back, with files still round-tripping.
/// </summary>
[TestFixture]
public class Ocfs2SchemaTests {

  [Test, Category("Spec")]
  public void Descriptor_ExposesVolumeLabelSchema() {
    var d = new Ocfs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    var schema = ((IFormatOptionsSchema)d).OptionsSchema;
    Assert.That(schema.Any(o => o.Key == "VolumeLabel"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Create_VolumeLabel_TakesEffectAndFilesRoundTrip() {
    var d = new Ocfs2FormatDescriptor();
    var payload = "ocfs2 label knob"u8.ToArray();
    using var ms = new MemoryStream();
    d.Create(ms,
      [ArchiveInputInfo.InMemory("note.txt", payload)],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["VolumeLabel"] = "MYCLUSTERFS" } });

    var sb = Ocfs2Superblock.TryParse(ms.ToArray());
    Assert.That(sb.Valid, Is.True);
    Assert.That(sb.Label, Is.EqualTo("MYCLUSTERFS"), "VolumeLabel must land in s_label.");

    ms.Position = 0;
    var names = d.List(ms, null).Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("note.txt"));

    var outDir = Path.Combine(Path.GetTempPath(), "ocfs2_schema_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      d.Extract(ms, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "note.txt")), Is.EqualTo(payload), "File must round-trip.");
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("Equivalence")]
  public void Create_DefaultLabel_MatchesWriterDefault() {
    var d = new Ocfs2FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    Assert.That(Ocfs2Superblock.TryParse(ms.ToArray()).Label, Is.EqualTo("OCFS2VOL"));
  }
}
