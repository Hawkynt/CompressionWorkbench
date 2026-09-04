#pragma warning disable CS1591
using System.Text.RegularExpressions;
using Compression.Registry;
using Compression.Tests.Documentation;

namespace Compression.Tests.Operations;

/// <summary>
/// The pages that describe what the maintenance verbs cover say what the code says.
/// </summary>
/// <remarks>
/// The per-format matrix itself lives in the filesystem package README, rendered
/// from the descriptors by <see cref="FilesystemSupportMatrix"/>. This fixture
/// reads it back from the other side: the render walks the descriptors the
/// package bundles, and the check below walks the ids the registry calls
/// filesystems, so a format that falls out of one set and not the other is
/// caught rather than quietly dropped from the table. Both use the same verb
/// predicates, so there is one derivation and two ways in.
/// </remarks>
[TestFixture]
public sealed class CapabilityDocumentationTests {
  [Test, Category("HappyPath")]
  public void FilesystemMatrixInThePackageReadmeMatchesLiveRegistry() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var root = FilesystemReadmeIsCurrentTests.RepositoryRoot();
    var readme = File.ReadAllText(Path.Combine(root, FilesystemSupportMatrix.ReadmePath));
    var (_, section, _) = FilesystemSupportMatrix.Split(readme);
    var documented = ParseFilesystemMatrix(section);
    var problems = new List<string>();

    foreach (var id in FormatRegistry.FilesystemFormatIds) {
      var ops = FormatRegistry.GetArchiveOps(id);
      var expected = ExpectedRow(ops);
      if (!documented.Remove(id, out var actual)) {
        problems.Add($"missing row: {RenderRow(id, expected)}");
        continue;
      }

      foreach (var column in expected.Keys)
        if (!actual.TryGetValue(column, out var value) || value != expected[column])
          problems.Add($"{id}.{column}: documented={Render(actual.GetValueOrDefault(column))}, live={Render(expected[column])}; expected row: {RenderRow(id, expected)}");
    }

    // What is left over is the disk-image containers: the package bundles them
    // and the matrix lists them, but the registry does not count them among the
    // filesystems. Every one of those still has to name a format the package
    // actually ships.
    var bundled = FilesystemSupportMatrix.Descriptors(root).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
    foreach (var unexpected in documented.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
      if (!bundled.Contains(unexpected))
        problems.Add($"row for a format the package does not bundle: {unexpected}");

    Assert.That(problems, Is.Empty,
      $"The support matrix in {FilesystemSupportMatrix.ReadmePath} is stale. Re-run FilesystemReadmeIsCurrentTests with CWB_WRITE_DOCS=1 to render it from the live registry:\n" +
      string.Join("\n", problems));
  }

  [Test, Category("HappyPath")]
  public void WormProseCannotNameDescriptorsThatAdvertiseCanModify() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var document = File.ReadAllText(FindRepositoryFile("docs", "MAINTENANCE-MECHANISMS.md"));
    var section = Slice(document, "### Stays WORM", "## Where the per-format coverage lives");
    var boldNames = Regex.Matches(section, @"\*\*([^*]+)\*\*")
      .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var contradictions = FormatRegistry.All
      .Where(d => d.Capabilities.HasFlag(FormatCapabilities.CanModify))
      .Where(d => boldNames.Contains(d.Id) || boldNames.Contains(d.DisplayName))
      .Select(d => $"{d.Id} ({d.DisplayName}) advertises CanModify but is listed under 'Stays WORM'.")
      .OrderBy(x => x, StringComparer.Ordinal)
      .ToArray();

    Assert.That(contradictions, Is.Empty,
      "Write-capability prose contradicts executable descriptor state:\n" + string.Join("\n", contradictions));
  }

  private static Dictionary<string, bool> ExpectedRow(IArchiveFormatOperations? ops)
    => new(StringComparer.OrdinalIgnoreCase) {
      ["Compact"] = FilesystemSupportMatrix.Compacts(ops),
      ["Defrag"] = FilesystemSupportMatrix.Defrags(ops),
      ["Wipe"] = FilesystemSupportMatrix.Wipes(ops),
      ["Shrink"] = FilesystemSupportMatrix.Shrinks(ops),
      ["Layout"] = FilesystemSupportMatrix.RelaysOut(ops),
      ["Purge"] = FilesystemSupportMatrix.Purges(ops),
    };

  /// <summary>
  /// Every row of every family table in the generated region, keyed by the id in
  /// the second cell. Only the verb columns are read back; the hand-written ones
  /// carry prose no descriptor can be asked about.
  /// </summary>
  private static Dictionary<string, Dictionary<string, bool>> ParseFilesystemMatrix(string section) {
    var lines = section.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var result = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
    var verbs = new[] { "Compact", "Defrag", "Wipe", "Shrink", "Layout", "Purge" };
    List<string>? columns = null;

    foreach (var line in lines) {
      var text = line.TrimStart();
      if (!text.StartsWith('|')) continue;
      var cells = SplitRow(line);
      if (cells.Count < 3) continue;
      if (cells[0] == "Format" && cells[1] == "Id") { columns = cells; continue; }
      if (columns == null || cells.Count != columns.Count) continue;
      var id = cells[1].Trim('`').Trim();
      if (id.Length == 0 || id.All(c => c is '-' or ':')) continue;

      var row = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
      foreach (var verb in verbs) {
        var index = columns.FindIndex(c => c.Equals(verb, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) row[verb] = ParseMarker(cells[index]);
      }
      result[id] = row;
    }

    Assert.That(columns, Is.Not.Null, "The generated support matrix carries no table header.");
    Assert.That(result, Is.Not.Empty, "The generated support matrix carries no rows.");
    return result;
  }

  private static List<string> SplitRow(string line)
    => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList();

  /// <summary>A verb cell is a tick, a dash, or a tick that says how — "✅ moving", "✅ rebuild".</summary>
  private static bool ParseMarker(string marker)
    => marker switch {
      _ when marker.StartsWith("✅", StringComparison.Ordinal) => true,
      "Y" or "Yes" => true,
      "·" or "—" or "-" or "" => false,
      _ => throw new InvalidDataException($"Unknown capability marker '{marker}'."),
    };

  private static string Render(bool value) => value ? "✅" : "—";

  private static string RenderRow(string id, IReadOnlyDictionary<string, bool> row)
    => $"| `{id}` | {Render(row["Compact"])} | {Render(row["Defrag"])} | {Render(row["Wipe"])} | {Render(row["Shrink"])} | {Render(row["Layout"])} | {Render(row["Purge"])} |";

  private static string Slice(string text, string startHeading, string endHeading) {
    var start = text.IndexOf(startHeading, StringComparison.Ordinal);
    Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing heading '{startHeading}'.");
    var end = text.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);
    Assert.That(end, Is.GreaterThan(start), $"Missing heading '{endHeading}' after '{startHeading}'.");
    return text[start..end];
  }

  private static string FindRepositoryFile(params string[] relativeParts) {
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent) {
      var path = relativeParts.Aggregate(current.FullName, Path.Combine);
      if (File.Exists(path)) return path;
    }
    throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(relativeParts)}' from '{AppContext.BaseDirectory}'.");
  }
}
