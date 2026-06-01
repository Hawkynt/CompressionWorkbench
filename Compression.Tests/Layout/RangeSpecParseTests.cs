#pragma warning disable CS1591
using Compression.Registry.Layout;

namespace Compression.Tests.Layout;

[TestFixture]
public class RangeSpecParseTests {

  [Test]
  public void Percent_Closed_Parses() {
    var r = RangeSpec.Parse("0%-5%");
    var (start, end) = r.Resolve(imageSize: 1000);
    Assert.That(start, Is.EqualTo(0));
    Assert.That(end, Is.EqualTo(50));
  }

  [Test]
  public void Percent_MidRange_Parses() {
    var r = RangeSpec.Parse("25%-75%");
    var (start, end) = r.Resolve(imageSize: 1000);
    Assert.That(start, Is.EqualTo(250));
    Assert.That(end, Is.EqualTo(750));
  }

  [TestCase("10MB-50MB", 10L * 1024 * 1024, 50L * 1024 * 1024)]
  [TestCase("1KB-4KB", 1024L, 4096L)]
  [TestCase("1024-2048", 1024L, 2048L)]
  [TestCase("1G-2G", 1L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024)]
  public void AbsoluteByteSizes_Parse(string input, long expectedStart, long expectedEnd) {
    var r = RangeSpec.Parse(input);
    var (start, end) = r.Resolve(imageSize: long.MaxValue / 2);
    Assert.That(start, Is.EqualTo(expectedStart));
    Assert.That(end, Is.EqualTo(expectedEnd));
  }

  [Test]
  public void Bracket_HalfOpen_Parses() {
    var r = RangeSpec.Parse("[1024, 2048)");
    var (start, end) = r.Resolve(imageSize: 100_000);
    Assert.That(start, Is.EqualTo(1024));
    Assert.That(end, Is.EqualTo(2048));
  }

  [Test]
  public void Bracket_Closed_TreatsEndExclusivePlusOne() {
    var r = RangeSpec.Parse("[1024, 2047]");
    var (start, end) = r.Resolve(imageSize: 100_000);
    Assert.That(start, Is.EqualTo(1024));
    Assert.That(end, Is.EqualTo(2048));
  }

  [Test]
  public void OpenStart_PercentForm_DefaultsToZero() {
    var r = RangeSpec.Parse("-50%");
    var (start, end) = r.Resolve(imageSize: 1000);
    Assert.That(start, Is.EqualTo(0));
    Assert.That(end, Is.EqualTo(500));
  }

  [Test]
  public void OpenEnd_PercentForm_DefaultsToImageSize() {
    var r = RangeSpec.Parse("50%-");
    var (start, end) = r.Resolve(imageSize: 1000);
    Assert.That(start, Is.EqualTo(500));
    Assert.That(end, Is.EqualTo(1000));
  }

  [Test]
  public void OpenEnd_PlusSyntax_SameAsDashOpen() {
    var r1 = RangeSpec.Parse("1024-");
    var r2 = RangeSpec.Parse("1024-+");
    Assert.That(r1.Resolve(10000), Is.EqualTo(r2.Resolve(10000)));
  }

  [Test]
  public void Resolve_ClampsEndToImageSize() {
    var r = RangeSpec.Parse("0-10000");
    var (_, end) = r.Resolve(imageSize: 500);
    Assert.That(end, Is.EqualTo(500));
  }

  [Test]
  public void Resolve_NegativeStartClampsToZero() {
    var r = new RangeSpec(StartFraction: null, EndFraction: null, StartBytes: -100, EndBytes: 50);
    var (start, end) = r.Resolve(imageSize: 1000);
    Assert.That(start, Is.EqualTo(0));
    Assert.That(end, Is.EqualTo(50));
  }

  [Test]
  public void Resolve_EndBeforeStart_ClampsToStart() {
    var r = new RangeSpec(StartFraction: null, EndFraction: null, StartBytes: 100, EndBytes: 50);
    var (start, end) = r.Resolve(imageSize: 1000);
    Assert.That(start, Is.EqualTo(100));
    Assert.That(end, Is.EqualTo(100));
  }

  [Test]
  public void ToString_RoundTripsPercent() {
    var r = RangeSpec.Parse("25%-75%");
    var rt = RangeSpec.Parse(r.ToString());
    Assert.That(rt.Resolve(1000), Is.EqualTo((250L, 750L)));
  }

  [Test]
  public void ToString_RoundTripsBytes() {
    var r = RangeSpec.Parse("4KB-16KB");
    var rt = RangeSpec.Parse(r.ToString());
    Assert.That(rt.Resolve(long.MaxValue / 2), Is.EqualTo((4096L, 16384L)));
  }

  [Test]
  public void EmptyString_Throws() {
    Assert.Throws<FormatException>(() => RangeSpec.Parse("   "));
  }

  [Test]
  public void NullString_Throws() {
    Assert.Throws<ArgumentNullException>(() => RangeSpec.Parse(null!));
  }

  [Test]
  public void PercentOutOfRange_Throws() {
    Assert.Throws<FormatException>(() => RangeSpec.Parse("0%-150%"));
  }

  [Test]
  public void UnknownUnit_Throws() {
    Assert.Throws<FormatException>(() => RangeSpec.Parse("1FOO-2FOO"));
  }

  [Test]
  public void BracketWithoutComma_Throws() {
    Assert.Throws<FormatException>(() => RangeSpec.Parse("[1024 2048)"));
  }
}
