using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Cab;

namespace Compression.Tests.Cab;

/// <summary>
/// LZX folders whose stream header announces x86 call translation (makecab and Microsoft's own
/// installers write them that way). The fixture was written by makecab from known data; see
/// LzxVectors/README.md.
/// </summary>
[TestFixture]
public class LzxCallTranslationTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CabLzxVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded CAB vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_MakecabLzxCabinetWithCallTranslation_When_Extracted_Then_TheOriginalBytes() {
    var cab = Vector("makecab-lzx-e8.cab");
    var r = new CabReader(new MemoryStream(cab));
    var entry = r.Entries.Single();
    var data = r.ExtractEntry(entry);
    Assert.That(data, Has.Length.EqualTo(150001));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo("282afab0f7615e397d33a15198980f8e24bdc1b2e50af295787ebf79053234a3"));
  }

  [Test, Category("RoundTrip")]
  public void Given_OurLzxCabinet_When_RoundTripped_Then_Identical() {
    var data = Vector("makecab-lzx-e8.cab");
    var w = new CabWriter(CabCompressionType.Lzx);
    w.AddFile("x.bin", data);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    var r = new CabReader(ms);
    Assert.That(r.ExtractEntry(r.Entries.Single()), Is.EqualTo(data));
  }
}
