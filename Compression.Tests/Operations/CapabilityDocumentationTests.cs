#pragma warning disable CS1591
using System.Text.RegularExpressions;
using Compression.Registry;

namespace Compression.Tests.Operations;

[TestFixture]
public sealed class CapabilityDocumentationTests {
  [Test, Category("HappyPath")]
  public void OperationCoverageFilesystemMatrixMatchesLiveRegistry() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var document = File.ReadAllText(FindRepositoryFile("docs", "OPERATION_COVERAGE.md"));
    var section = Slice(document, "## Filesystem descriptors", "## N/A notes");
    var documented = ParseFilesystemMatrix(section);
    var problems = new List<string>();

    foreach (var id in FormatRegistry.FilesystemFormatIds) {
      var descriptor = FormatRegistry.GetById(id)!;
      var ops = FormatRegistry.GetArchiveOps(id);
      var expected = ExpectedRow(descriptor, ops);
      if (!documented.Remove(id, out var actual)) {
        problems.Add($"missing row: {RenderRow(id, expected)}");
        continue;
      }

      foreach (var column in expected.Keys)
        if (!actual.TryGetValue(column, out var value) || value != expected[column])
          problems.Add($"{id}.{column}: documented={Render(actual.GetValueOrDefault(column))}, live={Render(expected[column])}; expected row: {RenderRow(id, expected)}");
    }

    foreach (var unexpected in documented.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
      problems.Add($"unexpected filesystem row not present in live registry: {unexpected}");

    Assert.That(problems, Is.Empty,
      "docs/OPERATION_COVERAGE.md filesystem matrix is stale. Regenerate/update it from the live registry:\n" +
      string.Join("\n", problems));
  }

  [Test, Category("HappyPath")]
  public void WormProseCannotNameDescriptorsThatAdvertiseCanModify() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var document = File.ReadAllText(FindRepositoryFile("docs", "OPERATION_COVERAGE.md"));
    var section = Slice(document, "### Stays WORM", "## Filesystem descriptors");
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

  private static Dictionary<string, bool> ExpectedRow(IFormatDescriptor descriptor, IArchiveFormatOperations? ops) {
    var defrag = ops is IArchiveDefragmentable;
    var shrink = ops is IArchiveShrinkable;
    var purge = ops is IArchivePurgeable;
    var wipe = ops is IWipeEmpty or IFilesystemExtentMap or IArchiveLayoutMap;
    var optimize = ops is ILayoutOptimizable || descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize);
    return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) {
      ["Compact"] = defrag || shrink || optimize,
      ["Defrag"] = defrag,
      ["Shrink"] = shrink,
      ["Purge"] = purge,
      ["Wipe"] = wipe,
      ["Optimize"] = optimize,
    };
  }

  private static Dictionary<string, Dictionary<string, bool>> ParseFilesystemMatrix(string section) {
    var lines = section.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var headerIndex = Array.FindIndex(lines, line => line.TrimStart().StartsWith("| Format |", StringComparison.Ordinal));
    Assert.That(headerIndex, Is.GreaterThanOrEqualTo(0), "Filesystem capability table header is missing.");
    var columns = SplitRow(lines[headerIndex]);
    var result = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
    for (var i = headerIndex + 2; i < lines.Length; ++i) {
      if (!lines[i].TrimStart().StartsWith('|')) break;
      var cells = SplitRow(lines[i]);
      if (cells.Count != columns.Count || cells.Count == 0) continue;
      var id = cells[0];
      if (string.IsNullOrWhiteSpace(id)) continue;
      var row = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
      for (var c = 1; c < cells.Count; ++c)
        row[columns[c]] = ParseMarker(cells[c]);
      result[id] = row;
    }
    return result;
  }

  private static List<string> SplitRow(string line)
    => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList();

  private static bool ParseMarker(string marker)
    => marker switch {
      "Y" or "Yes" or "✅" => true,
      "·" or "—" or "-" or "" => false,
      _ => throw new InvalidDataException($"Unknown capability marker '{marker}'."),
    };

  private static string Render(bool value) => value ? "Y" : "·";

  private static string RenderRow(string id, IReadOnlyDictionary<string, bool> row)
    => $"| {id} | {Render(row["Compact"])} | {Render(row["Defrag"])} | {Render(row["Shrink"])} | {Render(row["Purge"])} | {Render(row["Wipe"])} | {Render(row["Optimize"])} |";

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
