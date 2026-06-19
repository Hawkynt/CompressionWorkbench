using System.Text;
using Compression.Registry;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Verifies the Stacker descriptor's <c>Compatibility</c> / <c>Version</c>
/// create-options route between the genuine (driver-compatible) and extended
/// layouts, and that the descriptor reads either back via auto-detection.
/// </summary>
[TestFixture]
public class StackerGenuineOptionTests {

  private static readonly (string Name, byte[] Data)[] Inputs = [
    ("HELLO.TXT", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("genuine stacvol\r\n", 40)))),
    ("DATA.BIN", MakeData()),
  ];

  private static byte[] MakeData() { var b = new byte[5000]; new Random(9).NextBytes(b); return b; }

  private static byte[] Create(Dictionary<string, string>? opt) {
    var d = new StackerFormatDescriptor();
    var opts = opt is null ? new FormatCreateOptions() : new FormatCreateOptions { FormatSpecific = opt };
    using var ms = new MemoryStream();
    d.Create(ms, Inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data)).ToList(), opts);
    return ms.ToArray();
  }

  [Test]
  public void Genuine_Option_RoundTripsThroughDescriptor() {
    var img = Create(new Dictionary<string, string> { ["Compatibility"] = "Genuine", ["Version"] = "3" });
    Assert.That(Encoding.ASCII.GetString(img, 0, 7), Is.EqualTo("STACKER"));

    var d = new StackerFormatDescriptor();
    var listed = d.List(new MemoryStream(img), null).Select(e => e.Name).ToList();
    Assert.That(listed, Is.EquivalentTo(Inputs.Select(i => i.Name)));

    var dir = Path.Combine(Path.GetTempPath(), $"cwb-stacopt-{Guid.NewGuid():N}");
    try {
      d.Extract(new MemoryStream(img), dir, null, null);
      foreach (var (name, data) in Inputs)
        Assert.That(File.ReadAllBytes(Path.Combine(dir, name)), Is.EqualTo(data), $"{name} round-trip");
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Genuine_Reader_DecodesGenuineButNotExtended() {
    var genuine = Create(new Dictionary<string, string> { ["Compatibility"] = "Genuine" });
    using var gr = new GenuineStackerReader(new MemoryStream(genuine));
    Assert.That(gr.Entries, Has.Count.EqualTo(Inputs.Length));

    // Extended (default) output is not the genuine obfuscated-SCB layout.
    var extended = Create(null);
    Assert.Throws<InvalidDataException>(() => { using var _ = new GenuineStackerReader(new MemoryStream(extended)); });
  }
}
