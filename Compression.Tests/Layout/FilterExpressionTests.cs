#pragma warning disable CS1591
using Compression.Registry.Layout;

namespace Compression.Tests.Layout;

[TestFixture]
public class FilterExpressionTests {

  private static FilterFileContext MakeFile(
      string name = "test.bin",
      long size = 1024,
      DateTime? lastMod = null,
      uint attrs = 0,
      IReadOnlyList<long>? allSizes = null,
      IReadOnlyList<DateTime>? allMtimes = null) {
    var ext = name.Contains('.') ? name[name.LastIndexOf('.')..].ToLowerInvariant() : string.Empty;
    return new FilterFileContext {
      Name = name,
      Path = name,
      Extension = ext,
      Size = size,
      LastModified = lastMod,
      Attributes = attrs,
      AllSizes = allSizes,
      AllLastModifiedTimes = allMtimes,
    };
  }

  [Test]
  public void EqualOnName_Matches() {
    var f = FilterExpression.Parse("name = 'hello.txt'");
    Assert.That(f.Matches(MakeFile(name: "hello.txt")), Is.True);
    Assert.That(f.Matches(MakeFile(name: "other.txt")), Is.False);
  }

  [Test]
  public void DoubleEqualsOnName_Matches() {
    var f = FilterExpression.Parse("name == 'hello.txt'");
    Assert.That(f.Matches(MakeFile(name: "hello.txt")), Is.True);
  }

  [Test]
  public void NotEqual_Matches() {
    var f = FilterExpression.Parse("name != 'hello.txt'");
    Assert.That(f.Matches(MakeFile(name: "other.txt")), Is.True);
    Assert.That(f.Matches(MakeFile(name: "hello.txt")), Is.False);
  }

  [Test]
  public void SizeComparisons_Work() {
    var lt = FilterExpression.Parse("size < 1000");
    var le = FilterExpression.Parse("size <= 1024");
    var gt = FilterExpression.Parse("size > 1000");
    var ge = FilterExpression.Parse("size >= 1024");
    var file = MakeFile(size: 1024);
    Assert.That(lt.Matches(file), Is.False);
    Assert.That(le.Matches(file), Is.True);
    Assert.That(gt.Matches(file), Is.True);
    Assert.That(ge.Matches(file), Is.True);
  }

  [Test]
  public void SizeLiteral_KB_Parses() {
    var f = FilterExpression.Parse("size >= 1KB");
    Assert.That(f.Matches(MakeFile(size: 1024)), Is.True);
    Assert.That(f.Matches(MakeFile(size: 1023)), Is.False);
  }

  [Test]
  public void SizeLiteral_MB_Parses() {
    var f = FilterExpression.Parse("size > 5MB");
    Assert.That(f.Matches(MakeFile(size: 6L * 1024 * 1024)), Is.True);
    Assert.That(f.Matches(MakeFile(size: 1024)), Is.False);
  }

  [Test]
  public void And_BothMustMatch() {
    var f = FilterExpression.Parse("size > 100 and name = 'a.txt'");
    Assert.That(f.Matches(MakeFile(name: "a.txt", size: 200)), Is.True);
    Assert.That(f.Matches(MakeFile(name: "a.txt", size: 50)), Is.False);
    Assert.That(f.Matches(MakeFile(name: "b.txt", size: 200)), Is.False);
  }

  [Test]
  public void Or_EitherMatches() {
    var f = FilterExpression.Parse("name = 'a.txt' or name = 'b.txt'");
    Assert.That(f.Matches(MakeFile(name: "a.txt")), Is.True);
    Assert.That(f.Matches(MakeFile(name: "b.txt")), Is.True);
    Assert.That(f.Matches(MakeFile(name: "c.txt")), Is.False);
  }

  [Test]
  public void Not_Negates() {
    var f = FilterExpression.Parse("not (name = 'a.txt')");
    Assert.That(f.Matches(MakeFile(name: "a.txt")), Is.False);
    Assert.That(f.Matches(MakeFile(name: "b.txt")), Is.True);
  }

  [Test]
  public void Parens_GroupCorrectly() {
    // Without parens, "and" binds tighter than "or", so the second clause is
    // (name=c and size>50). With parens, we force OR before AND.
    var f = FilterExpression.Parse("(name = 'a.txt' or name = 'b.txt') and size > 50");
    Assert.That(f.Matches(MakeFile(name: "a.txt", size: 100)), Is.True);
    Assert.That(f.Matches(MakeFile(name: "c.txt", size: 100)), Is.False);
    Assert.That(f.Matches(MakeFile(name: "a.txt", size: 10)), Is.False);
  }

  [Test]
  public void Contains_MatchesSubstring() {
    var f = FilterExpression.Parse("name contains 'foo'");
    Assert.That(f.Matches(MakeFile(name: "foobar.txt")), Is.True);
    Assert.That(f.Matches(MakeFile(name: "barbaz.txt")), Is.False);
  }

  [Test]
  public void Matches_RegexWorks() {
    var f = FilterExpression.Parse(@"name matches '\\.txt$'");
    Assert.That(f.Matches(MakeFile(name: "foo.txt")), Is.True);
    Assert.That(f.Matches(MakeFile(name: "foo.bin")), Is.False);
  }

  [Test]
  public void In_MultipleValues() {
    var f = FilterExpression.Parse("extension in ('.txt', '.md', '.csv')");
    Assert.That(f.Matches(MakeFile(name: "a.txt")), Is.True);
    Assert.That(f.Matches(MakeFile(name: "a.md")), Is.True);
    Assert.That(f.Matches(MakeFile(name: "a.bin")), Is.False);
  }

  [Test]
  public void Quartile_OnSize_ResolvesPercentile() {
    var allSizes = new long[] { 10, 20, 30, 40, 100, 200, 500, 1000, 5000, 10000 };
    // 75th percentile of 10 sorted values -> index 6 (0-based int truncation of 0.75*9 = 6.75 -> 6).
    var f = FilterExpression.Parse("size >= quartile(0.75)");
    // Files at or above the 75th percentile (value: 500) should match.
    Assert.That(f.Matches(MakeFile(size: 500, allSizes: allSizes)), Is.True);
    Assert.That(f.Matches(MakeFile(size: 10000, allSizes: allSizes)), Is.True);
    Assert.That(f.Matches(MakeFile(size: 100, allSizes: allSizes)), Is.False);
  }

  [Test]
  public void Quartile_OnLastModified_ResolvesPercentile() {
    var now = DateTime.UtcNow;
    var times = new[] {
      now.AddDays(-100), now.AddDays(-80), now.AddDays(-60),
      now.AddDays(-40), now.AddDays(-20), now.AddDays(-5)
    };
    var f = FilterExpression.Parse("lastModified >= quartile(0.75)");
    // 0.75 * 5 = 3.75 -> idx 3 -> times[3] = -40 days.
    // Files modified within the last 40 days should match.
    Assert.That(f.Matches(MakeFile(lastMod: now.AddDays(-30), allMtimes: times)), Is.True);
    Assert.That(f.Matches(MakeFile(lastMod: now.AddDays(-90), allMtimes: times)), Is.False);
  }

  [Test]
  public void Quartile_OutOfRange_Throws() {
    Assert.Throws<FormatException>(() => FilterExpression.Parse("size >= quartile(1.5)"));
    Assert.Throws<FormatException>(() => FilterExpression.Parse("size >= quartile(-0.1)"));
  }

  [Test]
  public void Now_ReturnsRecentValue() {
    var f = FilterExpression.Parse("lastModified < now()");
    Assert.That(f.Matches(MakeFile(lastMod: DateTime.UtcNow.AddMinutes(-5))), Is.True);
    Assert.That(f.Matches(MakeFile(lastMod: DateTime.UtcNow.AddHours(5))), Is.False);
  }

  [Test]
  public void TodayMinusDays_Works() {
    var f = FilterExpression.Parse("lastModified > today() - days(30)");
    Assert.That(f.Matches(MakeFile(lastMod: DateTime.UtcNow.AddDays(-10))), Is.True);
    Assert.That(f.Matches(MakeFile(lastMod: DateTime.UtcNow.AddDays(-90))), Is.False);
  }

  [Test]
  public void DateLiteral_OnDateField_ParsesAsDate() {
    var f = FilterExpression.Parse("lastModified < '2024-01-01'");
    Assert.That(f.Matches(MakeFile(lastMod: new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc))), Is.True);
    Assert.That(f.Matches(MakeFile(lastMod: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc))), Is.False);
  }

  [Test]
  public void MissingField_AlwaysFalseForEquals() {
    var f = FilterExpression.Parse("lastModified > now() - days(30)");
    Assert.That(f.Matches(MakeFile(lastMod: null)), Is.False);
  }

  [Test]
  public void UnknownField_ErrorIncludesPosition() {
    var ex = Assert.Throws<FormatException>(() => FilterExpression.Parse("foobar = 1"));
    Assert.That(ex!.Message, Does.Contain("foobar"));
    Assert.That(ex.Message, Does.Contain("position 0"));
  }

  [Test]
  public void UnknownFunction_ErrorIncludesName() {
    var ex = Assert.Throws<FormatException>(() => FilterExpression.Parse("size = bogus()"));
    Assert.That(ex!.Message, Does.Contain("bogus"));
  }

  [Test]
  public void UnterminatedString_Throws() {
    Assert.Throws<FormatException>(() => FilterExpression.Parse("name = 'hello"));
  }

  [Test]
  public void MissingOperator_Throws() {
    Assert.Throws<FormatException>(() => FilterExpression.Parse("name 'value'"));
  }

  [Test]
  public void Cache_ReturnsSameInstanceForSameString() {
    var a = FilterExpression.Parse("size > 100");
    var b = FilterExpression.Parse("size > 100");
    Assert.That(a, Is.SameAs(b),
      "FilterExpression caches by string identity — same input should yield same compiled filter.");
  }

  [Test]
  public void AttributesComparison_Works() {
    var f = FilterExpression.Parse("attributes > 0");
    Assert.That(f.Matches(MakeFile(attrs: 0x10)), Is.True);
    Assert.That(f.Matches(MakeFile(attrs: 0)), Is.False);
  }

  [Test]
  public void ExtensionFilter_Works() {
    var f = FilterExpression.Parse("extension = '.txt'");
    Assert.That(f.Matches(MakeFile(name: "a.TXT")), Is.True);
    Assert.That(f.Matches(MakeFile(name: "a.bin")), Is.False);
  }
}
