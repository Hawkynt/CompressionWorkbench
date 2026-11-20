using System.Text;
using FileFormat.InnoSetup;

namespace Compression.Tests.InnoSetup;

/// <summary>The setup-data tags around the 7.1.1 layout change (Shared.Struct.pas SetupID).</summary>
[TestFixture]
public class InnoVersionTests {

  private static InnoVersion Parse(string tag) {
    var bytes = new byte[64];
    Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 0);
    return InnoVersion.Parse(bytes);
  }

  [TestCase("Inno Setup Setup Data (7.0.0.3)", 7, 0, 0, 3)] // 7.0.0 to 7.1.0 all write this tag
  [TestCase("Inno Setup Setup Data (7.1.1)", 7, 1, 1, 0)]
  public void GivenAKnownTag_WhenParsed_ThenItIsTheUnicodeVersionItNames(string tag, int a, int b, int c, int d) {
    var v = Parse(tag);
    Assert.That(v.IsKnown, Is.True);
    Assert.That(v.IsUnicode, Is.True);
    Assert.That(v.Value, Is.EqualTo(InnoVersion.V(a, b, c, d)));
  }

  [Test]
  public void GivenTheTagBefore711_WhenAskedForTheNextLayout_ThenItIs711() {
    Assert.That(Parse("Inno Setup Setup Data (7.0.0.3)").Next()?.Value, Is.EqualTo(InnoVersion.V(7, 1, 1)));
  }

  [TestCase("i1.2.10--16\x1a", true, true)]
  [TestCase("i1.2.10--32\x1a", false, true)]
  [TestCase("i1.2.09--16\x1a", true, false)] // an unlisted 1.2.x tag is read the same way
  public void GivenALegacyTag_WhenParsed_ThenTwelveBytesAndTheBitnessItNames(string tag, bool bits16, bool known) {
    var v = InnoVersion.Parse(Encoding.ASCII.GetBytes(tag)); // only the 12 tag bytes exist
    Assert.That((v.IsLegacyTag, v.TagLength, v.Is16Bit, v.IntegerSize, v.IsKnown), Is.EqualTo((true, 12, bits16, bits16 ? 2 : 4, known)));
    Assert.That(v.Major, Is.EqualTo(1));
  }

  [TestCase("i1.2.10--64\x1a")]
  [TestCase("i1.2.10-16-\x1a")]
  [TestCase("i1.2.1x--16\x1a")]
  public void GivenAMalformedLegacyTag_WhenParsed_ThenItIsNotAccepted(string tag) {
    var bytes = new byte[64];
    Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 0);
    Assert.Throws<InvalidDataException>(() => InnoVersion.Parse(bytes));
  }

  [Test]
  public void GivenAFutureTag_WhenParsed_ThenItIsUnknownButCarriesItsNumber() {
    var v = Parse("Inno Setup Setup Data (7.2.0)");
    Assert.That(v.IsKnown, Is.False);
    Assert.That(v.Value, Is.EqualTo(InnoVersion.V(7, 2, 0)));
  }
}
