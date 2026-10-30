using System.Reflection;
using System.Security.Cryptography;
using FileFormat.C64Sfx;

namespace Compression.Tests.C64Sfx;

/// <summary>
/// Real C64/C128 SFX self-extracting LHA archives from zimmers.net; every member's SHA-256 is what
/// unar 1.10.1 extracted (ReferenceVectors/unar-listing.txt).
/// </summary>
[TestFixture]
public class C64SfxTests {
  private static MemoryStream Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"C64SfxVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded SFX vector '{name}' is missing.");
    var ms = new MemoryStream();
    stream.CopyTo(ms);
    ms.Position = 0;
    return ms;
  }

  [TestCase("Draughts.sfx", "draughts|lh1/P|4174;draughts.doc|lh1/S|2758;draughts2|lh1/P|9634")]
  [TestCase("Agricola.sfx", "agricola        |lh1|163;a|lh1|8194;b|lh1|2002;c|lh1|32700;d|lh1|1714;e|lh1|6018;f|lh1|2050;g|lh1/S|450")]
  [Category("ThirdPartyVector")]
  public void Given_RealSfx_When_Listed_Then_MatchesLsar(string vector, string expected) {
    var list = new C64SfxFormatDescriptor().List(Vector(vector), null);
    Assert.That(string.Join(";", list.Select(e => $"{e.Name}|{e.Method}|{e.OriginalSize}")), Is.EqualTo(expected));
  }

  [TestCase("Draughts.sfx", "draughts", "4060b5f1ebc37676292caa77bab145a3125b74019fe0f0da46f1b64bff3d5525")]
  [TestCase("Draughts.sfx", "draughts.doc", "894662d644dbd55ef3580f0fb1958e552e214afd5d6f6841961ca894744b09c8")]
  [TestCase("Draughts.sfx", "draughts2", "6927b9ee55b577a1851d89734f7f65e684e5933f5742365deacf52274e0d729a")]
  [TestCase("Agricola.sfx", "c", "ab6a574aafa89331cd58e0882e7908121434a9a33eaa4cd1fdbbae46dfdade28")]
  [TestCase("Agricola.sfx", "g", "f99740c29b17b5192d5ced06eb2412ad930c4a30cff7d9efb4cbd4a378c798a8")]
  [Category("ThirdPartyVector")]
  public void Given_RealSfx_When_Extracted_Then_BytesMatchUnar(string vector, string member, string sha256) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_c64sfx_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      new C64SfxFormatDescriptor().Extract(Vector(vector), dir, null, [member]);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, member)))), Is.EqualTo(sha256));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test, Category("EdgeCase")]
  public void Given_StubWithoutLha_When_Opened_Then_InvalidData() {
    var data = Vector("Draughts.sfx").ToArray()[..3000];
    Assert.Throws<InvalidDataException>(() => C64SfxFormatDescriptor.Payload(new MemoryStream(data)));
  }

  [Test, Category("HappyPath")]
  public void Given_Descriptor_When_Inspected_Then_ReadOnlyWithBannerSignature() {
    var d = new C64SfxFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.False);
    var data = Vector("Draughts.sfx").ToArray();
    var sig = d.MagicSignatures[0];
    Assert.That(data.AsSpan(sig.Offset, sig.Bytes.Length).SequenceEqual(sig.Bytes), Is.True);
  }
}
