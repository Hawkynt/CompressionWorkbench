#pragma warning disable CS1591
using System.Text.RegularExpressions;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Documentation;

/// <summary>
/// Keeps the audio package's support matrix honest. The matrix is the only support table for the
/// audio domain, so nothing else can be cross-checked against it — instead every capability cell
/// it states is re-derived here from the built registry, and the row set is re-derived from the
/// projects the package actually bundles.
/// <para>
/// The container table is fully derivable: state, PCM decode/encode and demux/mux all come from
/// <see cref="FormatRegistry"/> and <see cref="AudioConversionInventory"/>. The codec table is
/// not — codecs are plain classes rather than registered descriptors — so for those rows this
/// fixture checks that the row set matches the bundled `Codec.*` assemblies exactly, which is
/// what actually rots when a codec is added or removed.
/// </para>
/// </summary>
[TestFixture]
public class AudioReadmeStateTests {

  private const string PackageDirectory = "Hawkynt.FileFormats.Audio";
  private const string MatrixHeading = "## 🧩 Support matrix";
  private const string MatrixEndHeading = "## 🚀 Quick start";

  private static string FindRepositoryFile(params string[] relativeParts) {
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent) {
      var path = relativeParts.Aggregate(current.FullName, Path.Combine);
      if (File.Exists(path))
        return path;
    }
    throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(relativeParts)}' from '{AppContext.BaseDirectory}'.");
  }

  private static string Slice(string text, string startHeading, string endHeading) {
    var start = text.IndexOf(startHeading, StringComparison.Ordinal);
    Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing heading '{startHeading}'.");
    var end = text.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);
    Assert.That(end, Is.GreaterThan(start), $"Missing heading '{endHeading}' after '{startHeading}'.");
    return text[start..end];
  }

  private static IEnumerable<(List<string> Columns, List<string> Cells)> Rows(string section) {
    List<string>? columns = null;
    var skipSeparator = false;
    foreach (var raw in section.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      var line = raw.Trim();
      if (!line.StartsWith('|')) {
        columns = null;
        continue;
      }

      var cells = line.Trim('|').Split('|').Select(static c => c.Trim()).ToList();
      if (columns == null) {
        columns = cells;
        skipSeparator = true;
        continue;
      }

      if (skipSeparator) {
        skipSeparator = false;
        continue;
      }

      if (cells.Count != columns.Count)
        continue;

      yield return (columns, cells);
    }
  }

  private static bool Mark(string cell) => cell switch {
    "✅" => true,
    "—" => false,
    _ => throw new InvalidDataException($"Unknown capability marker '{cell}'."),
  };

  /// <summary>
  /// Every project the package bundles, from both places they are declared: the package csproj and
  /// <c>Directory.Build.props</c>, which is where the AMR projects are added so they take part in
  /// static-graph restore. Reading only the csproj would silently miss those four.
  /// </summary>
  private static HashSet<string> BundledProjects(string prefix) {
    var text = File.ReadAllText(FindRepositoryFile(PackageDirectory, PackageDirectory + ".csproj"))
               + File.ReadAllText(FindRepositoryFile("Directory.Build.props"));
    return Regex
      .Matches(text, @"(?:Codecs|FileFormats)[\\/]((?:Codec|FileFormat)\.[A-Za-z0-9]+)[\\/]")
      .Select(static m => m.Groups[1].Value)
      .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
      .ToHashSet(StringComparer.Ordinal);
  }

  private static string ReadMatrix() {
    var readme = File.ReadAllText(FindRepositoryFile(PackageDirectory, "README.md"));
    return Slice(readme, MatrixHeading, MatrixEndHeading);
  }

  private static string ExpectedState(FormatCapabilities capabilities)
    => capabilities.HasFlag(FormatCapabilities.CanModify) ? "R/W"
      : capabilities.HasFlag(FormatCapabilities.CanCreate) ? "WORM"
      : "R";

  [Test]
  public void EveryContainerCellMatchesTheLiveRegistry() {
    FormatRegistration.EnsureInitialized();
    var inventory = AudioConversionInventory.Enumerate().ToDictionary(static c => c.FormatId, StringComparer.Ordinal);

    var problems = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (columns, cells) in Rows(ReadMatrix())) {
      var idIndex = columns.IndexOf("Id");
      if (idIndex < 0 || columns.IndexOf("Decode") < 0)
        continue;

      var id = cells[idIndex].Trim('`');
      if (!seen.Add(id)) {
        problems.Add($"{id}: appears in the table more than once");
        continue;
      }

      var descriptor = FormatRegistry.GetById(id);
      if (descriptor == null) {
        problems.Add($"{id}: row has no registered descriptor");
        continue;
      }

      void Check(string column, bool expected) {
        var i = columns.IndexOf(column);
        if (i < 0)
          return;
        if (Mark(cells[i]) != expected)
          problems.Add($"{id}.{column}: documented={cells[i]}, live={(expected ? "✅" : "—")}");
      }

      var stateIndex = columns.IndexOf("State");
      var expectedState = ExpectedState(descriptor.Capabilities);
      if (stateIndex >= 0 && cells[stateIndex] != expectedState)
        problems.Add($"{id}.State: documented={cells[stateIndex]}, live={expectedState}");

      inventory.TryGetValue(id, out var capability);
      Check("Decode", capability?.CanDecodePcm == true);
      Check("Encode", capability?.CanEncodePcm == true);
      Check("Demux", capability?.CanDemuxEncoded == true);
      Check("Mux", capability?.CanMuxEncoded == true);
    }

    Assert.Multiple(() => {
      Assert.That(seen, Is.Not.Empty, "No Id-keyed container rows found in the support matrix.");
      Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    });
  }

  [Test]
  public void EveryBundledFormatHasExactlyOneRow() {
    FormatRegistration.EnsureInitialized();
    var bundled = BundledProjects("FileFormat.");
    var expected = FormatRegistry.All
      .Where(d => bundled.Contains(d.GetType().Assembly.GetName().Name ?? string.Empty))
      .Select(static d => d.Id)
      .ToHashSet(StringComparer.Ordinal);

    var documented = Rows(ReadMatrix())
      .Where(static r => r.Columns.IndexOf("Id") >= 0 && r.Columns.IndexOf("Decode") >= 0)
      .Select(r => r.Cells[r.Columns.IndexOf("Id")].Trim('`'))
      .ToHashSet(StringComparer.Ordinal);

    Assert.Multiple(() => {
      Assert.That(expected, Is.Not.Empty, "No bundled audio formats were discovered.");
      Assert.That(expected.Except(documented), Is.Empty, "Bundled formats with no row in the support matrix.");
      Assert.That(documented.Except(expected), Is.Empty, "Support-matrix rows naming a format the package does not bundle.");
    });
  }

  [Test]
  public void EveryBundledCodecHasExactlyOneRow() {
    var expected = BundledProjects("Codec.");
    var documented = Rows(ReadMatrix())
      .Where(static r => r.Columns.IndexOf("Id") >= 0 && r.Columns.IndexOf("Family") >= 0)
      .Select(r => r.Cells[r.Columns.IndexOf("Id")].Trim('`'))
      .ToList();

    Assert.Multiple(() => {
      Assert.That(expected, Is.Not.Empty, "No bundled audio codecs were discovered.");
      Assert.That(documented, Is.Unique, "The codec table lists the same codec twice.");
      Assert.That(expected.Except(documented), Is.Empty, "Bundled codecs with no row in the support matrix.");
      Assert.That(documented.Except(expected), Is.Empty, "Support-matrix rows naming a codec the package does not bundle.");
    });
  }

  /// <summary>
  /// The support table is meant to be the only one. A second copy under <c>docs/</c> is exactly how
  /// the two drifted apart before, so its return is a test failure rather than a review question.
  /// </summary>
  [Test]
  public void NoSecondAudioSupportTableExists() {
    var docs = new DirectoryInfo(Path.GetDirectoryName(FindRepositoryFile("docs", "MEDIA-CODEC-COVERAGE.md"))!);
    var offenders = docs
      .GetFiles("*AUDIO*.md")
      .Where(static f => File.ReadAllLines(f.FullName).Count(static l => l.TrimStart().StartsWith('|')) > 4)
      .Select(static f => f.Name)
      .ToList();

    Assert.That(offenders, Is.Empty,
      "Audio support tables belong in the package README; these docs pages grew one back.");
  }
}
