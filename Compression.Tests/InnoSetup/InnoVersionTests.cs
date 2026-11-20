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

  [Test]
  public void GivenAFutureTag_WhenParsed_ThenItIsUnknownButCarriesItsNumber() {
    var v = Parse("Inno Setup Setup Data (7.2.0)");
    Assert.That(v.IsKnown, Is.False);
    Assert.That(v.Value, Is.EqualTo(InnoVersion.V(7, 2, 0)));
  }
}
