#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// A method the caller asked for reaches the writer as the method the caller
/// asked for.
/// </summary>
/// <remarks>
/// <para>Two parsers read the same string. <see cref="MethodSpec"/> is what the
/// CLI and the UI parse a <c>-m</c> argument with; <see cref="MethodNameParser"/>
/// is what a writer on the far side of the registry boundary parses the name it is
/// handed with. They disagreed — one stripped a single trailing <c>+</c>, the other
/// stripped all of them and counted — so <c>ds-lz77++</c> arrived as
/// <c>ds-lz77+</c> and the CVF writers gave effort tier 1 to a caller who asked for
/// tier 2. Silently, because both spellings are methods those writers declare.</para>
///
/// <para>The same boundary carries the other half of it. <c>default</c> is this
/// side's spelling of "no method preference" and <c>null</c> is the far side's, so
/// the sentinel must not cross — and <c>default+</c> is still no preference about
/// the method, which is why only the name may settle it.</para>
/// </remarks>
[TestFixture]
[Category("HappyPath")]
public sealed class MethodNameCrossesTheBoundaryTests {

  [TestCase("deflate", "deflate", 0)]
  [TestCase("deflate+", "deflate", 1)]
  [TestCase("deflate++", "deflate", 2)]
  [TestCase("ds-lz77", "ds-lz77", 0)]
  [TestCase("ds-lz77+", "ds-lz77", 1)]
  [TestCase("ds-lz77++", "ds-lz77", 2)]
  [TestCase("lzma+++", "lzma", 3)]
  [TestCase("  stored  ", "stored", 0)]
  public void BothParsers_ReadTheSameStringTheSameWay(string input, string name, int level) {
    var spec = MethodSpec.Parse(input);
    var parsed = MethodNameParser.Parse(input);

    Assert.Multiple(() => {
      Assert.That(spec.Name, Is.EqualTo(name), "MethodSpec read a different base method");
      Assert.That(spec.PlusLevel, Is.EqualTo(level), "MethodSpec read a different effort level");
      Assert.That(parsed.BaseMethod, Is.EqualTo(name));
      Assert.That(parsed.PlusLevel, Is.EqualTo(level));
      Assert.That(spec.Optimize, Is.EqualTo(level > 0));
      // Round-tripping through the printed form is what a writer that re-parses
      // the name it was handed actually sees.
      Assert.That(MethodNameParser.Parse(spec.ToString()), Is.EqualTo(parsed));
    });
  }

  [TestCase("default", 0)]
  [TestCase("default+", 1)]
  [TestCase("default++", 2)]
  public void TheDefaultSentinel_DoesNotCrossTheBoundary(string input, int level) {
    var spec = MethodSpec.Parse(input);
    Assert.Multiple(() => {
      Assert.That(spec.NamesNoMethod, Is.True,
        $"'{input}' names no method, so the far side has to be told null rather than the literal 'default'.");
      Assert.That(spec.PlusLevel, Is.EqualTo(level), "the effort the caller asked for went with the name");
    });
  }

  /// <summary>The effort run is put back for a writer that reads it out of the name.</summary>
  [TestCase("ds-lz77", 0, "ds-lz77")]
  [TestCase("ds-lz77", 1, "ds-lz77+")]
  [TestCase("ds-lz77", 2, "ds-lz77++")]
  [TestCase("ds-lz77++", 0, "ds-lz77++")]
  [TestCase("ds-lz77+", 2, "ds-lz77++")]
  public void TheEffortRun_IsPutBackOnTheNameTheWriterParses(string method, int level, string expected)
    => Assert.That(FormatHelpers.MethodWithEffort(new FormatCreateOptions { MethodName = method, OptimizeLevel = level }),
                   Is.EqualTo(expected));

  [Test]
  public void AnUnsetLevel_FallsBackToTheFlag() {
    Assert.Multiple(() => {
      Assert.That(new FormatCreateOptions { Optimize = true }.OptimizeLevel, Is.EqualTo(1));
      Assert.That(new FormatCreateOptions { Optimize = false }.OptimizeLevel, Is.EqualTo(0));
      Assert.That(new FormatCreateOptions { Optimize = true, OptimizeLevel = 3 }.OptimizeLevel, Is.EqualTo(3));
    });
  }

  /// <summary>
  /// The three CVF filesystems each declare three effort tiers of one codec, and
  /// each tier has to reach the writer as itself.
  /// </summary>
  [TestCase("DoubleSpace")]
  [TestCase("DriveSpace")]
  [TestCase("DriveSpace3")]
  public void EveryDeclaredCvfTier_ReachesTheWriterIntact(string formatId) {
    FormatRegistration.EnsureInitialized();
    var descriptor = FormatRegistry.GetById(formatId);
    Assert.That(descriptor, Is.Not.Null, formatId);

    var tiered = descriptor!.Methods.Count(m => MethodNameParser.Parse(m.Name).PlusLevel > 1);
    Assert.That(tiered, Is.GreaterThan(0),
      $"{formatId} declares no '++' tier, so this guard would pass vacuously.");

    foreach (var method in descriptor.Methods) {
      var spec = MethodSpec.Parse(method.Name);
      var options = new FormatCreateOptions { MethodName = spec.Name, OptimizeLevel = spec.PlusLevel };
      Assert.That(MethodNameParser.Parse(FormatHelpers.MethodWithEffort(options)),
        Is.EqualTo(MethodNameParser.Parse(method.Name)),
        $"{formatId}: '{method.Name}' does not survive the trip to the writer intact.");
    }
  }
}
