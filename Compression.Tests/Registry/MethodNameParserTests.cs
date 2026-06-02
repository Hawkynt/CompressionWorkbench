using Compression.Registry;

namespace Compression.Tests.Registry;

/// <summary>
/// Unit tests for <see cref="MethodNameParser.Parse"/>. Covers null/empty
/// inputs (equivalence class: "no method specified"), a non-plus base method
/// (equivalence class: "default tier"), a single plus, a multi-plus
/// (boundary: highest documented tier), an all-plus pathological input,
/// and a leading/trailing whitespace case.
/// </summary>
[TestFixture]
public class MethodNameParserTests {

  [Test, Category("EdgeCase")]
  public void Parse_Null_ReturnsEmptyZero() {
    var (b, n) = MethodNameParser.Parse(null);
    Assert.That(b, Is.EqualTo(""));
    Assert.That(n, Is.EqualTo(0));
  }

  [Test, Category("EdgeCase")]
  public void Parse_Empty_ReturnsEmptyZero() {
    var (b, n) = MethodNameParser.Parse("");
    Assert.That(b, Is.EqualTo(""));
    Assert.That(n, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Parse_PlainMethod_ReturnsBaseZero() {
    var (b, n) = MethodNameParser.Parse("deflate");
    Assert.That(b, Is.EqualTo("deflate"));
    Assert.That(n, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Parse_SinglePlus_ReturnsBaseOne() {
    var (b, n) = MethodNameParser.Parse("deflate+");
    Assert.That(b, Is.EqualTo("deflate"));
    Assert.That(n, Is.EqualTo(1));
  }

  [Test, Category("Boundary")]
  public void Parse_FourPluses_ReturnsBaseFour() {
    var (b, n) = MethodNameParser.Parse("deflate++++");
    Assert.That(b, Is.EqualTo("deflate"));
    Assert.That(n, Is.EqualTo(4));
  }

  [Test, Category("EdgeCase")]
  public void Parse_OnlyPlus_ReturnsEmptyOne() {
    var (b, n) = MethodNameParser.Parse("+");
    Assert.That(b, Is.EqualTo(""));
    Assert.That(n, Is.EqualTo(1));
  }

  [Test, Category("EdgeCase")]
  public void Parse_WhitespaceAndPlus_TrimsAndCounts() {
    var (b, n) = MethodNameParser.Parse("  deflate++  ");
    Assert.That(b, Is.EqualTo("deflate"));
    Assert.That(n, Is.EqualTo(2));
  }
}
