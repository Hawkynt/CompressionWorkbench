using System.Text;
using Compression.Registry;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

/// <summary>
/// Verifies the descriptor's <c>Compatibility</c> create-option routes between
/// the genuine (driver-compatible) and extended (feature-rich) layouts, and that
/// the descriptor reads either back through format auto-detection.
/// </summary>
[TestFixture]
public class DriveSpace3GenuineOptionTests {

  private static readonly (string Name, byte[] Data)[] Inputs = [
    ("HELLO.TXT", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("genuine DVR3\r\n", 40)))),
    ("DATA.BIN", MakeData()),
  ];

  private static byte[] MakeData() { var b = new byte[5000]; new Random(7).NextBytes(b); return b; }

  private static byte[] Create(string? compatibility) {
    var d = new DriveSpace3FormatDescriptor();
    var opts = compatibility is null
      ? new FormatCreateOptions()
      : new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Compatibility"] = compatibility } };
    using var ms = new MemoryStream();
    d.Create(ms, Inputs.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data)).ToList(), opts);
    return ms.ToArray();
  }

  [Test]
  public void Genuine_Option_ProducesGenuineSignature_And_RoundTrips() {
    var img = Create("Genuine");
    Assert.That(Encoding.ASCII.GetString(img, 3, 8), Is.EqualTo("MSDBL6.0"), "genuine MSDBL6.0 signature");
    Assert.That(img[0x0D], Is.EqualTo(64), "64 sectors/cluster (DriveSpace 3)");
    Assert.That(img[0x33], Is.EqualTo(3), "version flag 3");

    var d = new DriveSpace3FormatDescriptor();
    var listed = d.List(new MemoryStream(img), null).Select(e => e.Name).ToList();
    Assert.That(listed, Is.EquivalentTo(Inputs.Select(i => i.Name)));

    var dir = Path.Combine(Path.GetTempPath(), $"cwb-dvr3opt-{Guid.NewGuid():N}");
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
    Assert.That(Encoding.ASCII.GetString(img, 3, 7), Is.EqualTo("MS_DSP3"),
      "default (Extended) keeps the feature-rich MS_DSP3 layout");
  }
}
