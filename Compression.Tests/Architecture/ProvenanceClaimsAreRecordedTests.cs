using System.Text.RegularExpressions;
using Compression.Tests.Documentation;

namespace Compression.Tests.Architecture;

/// <summary>
/// Every product source file that says it was ported, converted or transcribed from somebody
/// else's code has a row in <c>docs/LICENCE-PROVENANCE.md</c>, which records the upstream, its
/// licence and whether the notice it needs is in place.
/// </summary>
/// <remarks>
/// <para>A "faithful port of …" comment is a licence statement whether its author meant one or
/// not: a port of GPL code cannot ship under this repository's LGPL, and a port of permissive
/// code still owes its attribution. The scan is deliberately textual and simple. Negated
/// phrasings ("not a port of", "nothing here is ported") are ignored, also when the negation
/// sits on the preceding line. A file is covered when its repository-relative path, or any
/// directory above it written with a trailing slash, appears in back-quotes in the document.</para>
/// <para>Tests and <c>Vendored/**</c> are exempt: vendored copies are recorded in their own
/// vendoring notes.</para>
/// </remarks>
[TestFixture]
public class ProvenanceClaimsAreRecordedTests {

  private const string LedgerPath = "docs/LICENCE-PROVENANCE.md";

  private static readonly Regex Claim = new(
    @"(?i:\bfaithful(?:ly)?[\s,]+(?:[\w-]+[\s,]+){0,3}?port(?:ed)?\b|\bported\b|\bport\s+of\b|\bconverted\s+from\b|\btransliterat(?:ed|ion)\b|\btranscribed\s+from\b|\badaptation\s+of\b)|(?://|\*)\s*Ports\b",
    RegexOptions.Compiled);

  private static readonly Regex Negation = new(
    @"\bnot\b[^.]{0,40}\bport|\bno\b[^.]{0,60}\bported|\bnothing\b[^.]{0,40}\b(?:ported|taken)|hand-ported|without\s+porting|rather\s+than\s+(?:a\s+)?port|never\s+ported",
    RegexOptions.Compiled | RegexOptions.IgnoreCase);

  private static readonly Regex BackQuoted = new("`([^`\r\n]+)`", RegexOptions.Compiled);

  private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) {
    "bin", "obj", "Vendored", ".claude", ".git", ".vs", "node_modules", "test-corpus", "Compression.Tests",
  };

  [Test, Category("Spec")]
  public void EveryPortClaimInProductCode_HasALedgerRow() {
    var root = FilesystemReadmeIsCurrentTests.RepositoryRoot();
    var covered = LedgerPaths(root);

    var missing = new List<string>();
    foreach (var file in ProductSources(root)) {
      var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
      var claim = FirstClaim(File.ReadAllLines(file));
      if (claim is null || IsCovered(relative, covered))
        continue;
      missing.Add($"{relative}:{claim.Value.Line}: {claim.Value.Text.Trim()}");
    }

    Assert.That(missing, Is.Empty,
      $"source files claim third-party provenance without a row in {LedgerPath}; add the upstream, its licence and the class (A/B/C/R)");
  }

  [Test, Category("Spec")]
  public void EveryPathTheLedgerNames_Exists() {
    var root = FilesystemReadmeIsCurrentTests.RepositoryRoot();
    var stale = LedgerPaths(root)
      .Where(p => p.StartsWith("Compression.", StringComparison.Ordinal) || p.StartsWith("Hawkynt.", StringComparison.Ordinal))
      .Where(p => !File.Exists(Path.Combine(root, p)) && !Directory.Exists(Path.Combine(root, p)))
      .ToList();

    Assert.That(stale, Is.Empty, $"{LedgerPath} names paths that no longer exist");
  }

  [TestCase("/// A faithful port of FFmpeg's <c>libavcodec/foo.c</c>.", ExpectedResult = true)]
  [TestCase("/// a faithful, decode-only port of FFmpeg's", ExpectedResult = true)]
  [TestCase("// Ported from libbsc's range coder (Apache-2.0).", ExpectedResult = true)]
  [TestCase("/// Converted from libzbitmap (MIT).", ExpectedResult = true)]
  [TestCase("/// Tables ported verbatim from qoa.h.", ExpectedResult = true)]
  [TestCase("/// Ports little-endian bcachefs get_inc_field() semantics:", ExpectedResult = true)]
  [TestCase("/// This is a managed adaptation of FFmpeg's LGPL dcaenc.c core path", ExpectedResult = true)]
  [TestCase("/// This is a clean-room implementation, not a port of the reference source.", ExpectedResult = false)]
  [TestCase("/// Nothing here is ported from Upack.", ExpectedResult = false)]
  [TestCase("/// No third-party source code was consulted or ported.", ExpectedResult = false)]
  [TestCase("/// preferred over a hand-ported split-radix FFT.", ExpectedResult = false)]
  [TestCase("  private static int Transliterate(char raw) {", ExpectedResult = false)]
  [TestCase("/// port $0800 selects the active channel", ExpectedResult = false)]
  [TestCase("", ExpectedResult = false)]
  public bool TheClaimScanner_TellsClaimsFromDisclaimers(string line) => FirstClaim([line]) is not null;

  [Test]
  public void ANegationOnThePreviousLine_CancelsTheClaim() {
    string[] split = ["/// This is a clean-room implementation written from the format description, not", "/// a port of Muravyov's reference `balz.cpp`; only this building block's own"];
    Assert.That(FirstClaim(split), Is.Null);
  }

  [Test]
  public void ADirectoryRow_CoversEveryFileBelowIt() {
    HashSet<string> rows = ["Hawkynt.FileFormats.Audio/Codecs/Codec.Sipr/"];
    Assert.That(IsCovered("Hawkynt.FileFormats.Audio/Codecs/Codec.Sipr/SiprCodec.cs", rows), Is.True);
    Assert.That(IsCovered("Hawkynt.FileFormats.Audio/Codecs/Codec.Siren/SirenCodec.cs", rows), Is.False);
    Assert.That(IsCovered("Hawkynt.FileFormats.Audio/Codecs/Codec.SiprX.cs", rows), Is.False);
  }

  private static (int Line, string Text)? FirstClaim(IReadOnlyList<string> lines) {
    for (var i = 0; i < lines.Count; ++i) {
      if (!Claim.IsMatch(lines[i]))
        continue;
      var context = i > 0 ? lines[i - 1] + " " + lines[i] : lines[i];
      if (!Negation.IsMatch(context))
        return (i + 1, lines[i]);
    }
    return null;
  }

  private static bool IsCovered(string relativePath, IReadOnlySet<string> ledgerPaths) {
    if (ledgerPaths.Contains(relativePath))
      return true;
    for (var slash = relativePath.LastIndexOf('/'); slash > 0; slash = relativePath.LastIndexOf('/', slash - 1))
      if (ledgerPaths.Contains(relativePath[..(slash + 1)]))
        return true;
    return false;
  }

  private static HashSet<string> LedgerPaths(string root) {
    var text = File.ReadAllText(Path.Combine(root, LedgerPath));
    return BackQuoted.Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(v => v.Contains('/')).ToHashSet(StringComparer.Ordinal);
  }

  private static IEnumerable<string> ProductSources(string root) {
    var pending = new Stack<string>([root]);
    while (pending.Count > 0) {
      var directory = pending.Pop();
      foreach (var sub in Directory.EnumerateDirectories(directory)) {
        var name = Path.GetFileName(sub);
        if (SkippedDirectories.Contains(name) || name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
          continue;
        pending.Push(sub);
      }
      foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        yield return file;
    }
  }
}
