using System.Text;
using Compression.Registry;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// Verifies the DoubleSpace descriptor's <c>Compatibility</c> create-option
/// routes between the genuine v2 (driver-compatible) and extended layouts, and
/// that the descriptor reads either back via auto-detection.
/// </summary>
[TestFixture]
public class DoubleSpaceGenuineOptionTests {

  private static readonly (string Name, byte[] Data)[] Inputs = [
    ("HELLO.TXT", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("genuine v2\r\n", 30)))),
    ("DATA.BIN", MakeData()),
  ];

  private static byte[] MakeData() { var b = new byte[3000]; new Random(11).NextBytes(b); return b; }

  private static byte[] Create(string? compat) {
    var d = new DoubleSpaceFormatDescriptor();
    var opts = compat is null
      ? new FormatCreateOptions()
      : new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Compatibility"] = compat } };
    using var ms = new MemoryStream();
    d.Create(ms, Inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data)).ToList(), opts);
    return ms.ToArray();
  }

  [Test]
  public void Genuine_Option_ProducesMsdblSignature_And_RoundTrips() {
    var img = Create("Genuine");
    Assert.That(Encoding.ASCII.GetString(img, 3, 8), Is.EqualTo("MSDBL6.0"));
    Assert.That(img[0x0D], Is.EqualTo(16), "16 sectors/cluster (v2)");

    var d = new DoubleSpaceFormatDescriptor();
    Assert.That(d.List(new MemoryStream(img), null).Select(e => e.Name),
      Is.EquivalentTo(Inputs.Select(i => i.Name)));

    var dir = Path.Combine(Path.GetTempPath(), $"cwb-dbl-{Guid.NewGuid():N}");
    try {
      d.Extract(new MemoryStream(img), dir, null, null);
      foreach (var (name, data) in Inputs)
        Assert.That(File.ReadAllBytes(Path.Combine(dir, name)), Is.EqualTo(data), $"{name} round-trip");
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Default_Option_ProducesExtendedLayout() {
    var img = Create(null);
    Assert.That(Encoding.ASCII.GetString(img, 3, 8), Is.EqualTo("MSDSP6.0"),
      "default (Extended) keeps the feature-rich MSDSP6.0 layout");
  }
}
