#pragma warning disable CS1591
using System.Text.RegularExpressions;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// The support matrix in <c>Hawkynt.FileFormats.Archives/README.md</c> is the one ledger for the
/// archive domain, and every capability cell in it is derived from the descriptor the package ships.
/// This fixture re-derives those cells from the built registry and fails on any disagreement, so the
/// README cannot drift from the code: a State that is WORM in the table while the descriptor says
/// CanModify, a Compress tick without CanCreate, a Maintenance verb without its interface.
/// </summary>
[TestFixture]
public sealed class ArchivesReadmeStateTests {

  private static readonly string[] MaintenanceTokens = ["defrag", "shrink", "wipe", "optimize", "reorder"];

  [Test, Category("HappyPath")]
  public void EverySupportMatrixCellMatchesTheLiveDescriptor() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var readme = File.ReadAllText(FindRepositoryFile("Hawkynt.FileFormats.Archives", "README.md"));
    var section = Slice(readme, "## 🧩 Support matrix", "## 🚀 Quick start");
    var problems = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);

    foreach (var (columns, cells) in Rows(section)) {
      var idColumn = columns.IndexOf("Id");
      if (idColumn < 0) continue;
      var id = cells[idColumn].Trim('`');
      if (!seen.Add(id)) { problems.Add($"{id}: listed twice"); continue; }
      var descriptor = FormatRegistry.GetById(id);
      if (descriptor == null) { problems.Add($"{id}: row has no registered descriptor"); continue; }
      var caps = descriptor.Capabilities;
      var ops = (object?)FormatRegistry.GetArchiveOps(id) ?? FormatRegistry.GetStreamOps(id);

      void Check(string column, bool expected) {
        var i = columns.IndexOf(column);
        if (i < 0) return;
        var actual = Mark(cells[i]);
        if (actual != expected)
          problems.Add($"{id}.{column}: documented={cells[i]}, live={(expected ? "✅" : "—")}");
      }

      var state = columns.IndexOf("State");
      if (state >= 0) {
        var expected = caps.HasFlag(FormatCapabilities.CanModify) ? "R/W" : caps.HasFlag(FormatCapabilities.CanCreate) ? "WORM" : "R";
        if (cells[state] != expected) problems.Add($"{id}.State: documented={cells[state]}, live={expected}");
      }
      Check("Test", caps.HasFlag(FormatCapabilities.CanTest));
      Check("Compress", caps.HasFlag(FormatCapabilities.CanCreate));
      Check("Decompress", caps.HasFlag(FormatCapabilities.CanExtract));
      Check("Optimize", caps.HasFlag(FormatCapabilities.SupportsOptimize));
      Check("Demux", caps.HasFlag(FormatCapabilities.CanExtract));
      Check("Mux", caps.HasFlag(FormatCapabilities.CanCreate));
      Check("Remux / edit", caps.HasFlag(FormatCapabilities.CanModify) || ops is IFileInternalChunkMover || ops is IAudioMuxTarget);

      var maintenance = columns.IndexOf("Maintenance");
      if (maintenance >= 0) {
        var expected = new List<string>();
        if (ops is IArchiveDefragmentable) expected.Add("defrag");
        if (ops is IArchiveShrinkable) expected.Add("shrink");
        if (ops is IWipeEmpty or IArchiveLayoutMap) expected.Add("wipe");
        if (ops is ILayoutOptimizable || caps.HasFlag(FormatCapabilities.SupportsOptimize)) expected.Add("optimize");
        if (ops is IFileInternalChunkMover) expected.Add("reorder");
        var documented = cells[maintenance] == "—" ? [] : cells[maintenance].Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in documented)
          if (!MaintenanceTokens.Contains(token)) problems.Add($"{id}.Maintenance: unknown verb '{token}'");
        if (!documented.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)))
          problems.Add($"{id}.Maintenance: documented=[{string.Join(" · ", documented)}], live=[{string.Join(" · ", expected)}]");
      }
    }

    Assert.That(seen, Is.Not.Empty, "No Id-keyed rows found in the support matrix.");
    Assert.That(problems, Is.Empty,
      "Hawkynt.FileFormats.Archives/README.md support matrix disagrees with the live registry:\n" + string.Join("\n", problems));
  }

  [Test, Category("HappyPath")]
  public void EveryBundledDescriptorHasARow() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var root = Path.GetDirectoryName(FindRepositoryFile("Hawkynt.FileFormats.Archives", "README.md"))!;
    var projectFiles = File.ReadAllText(Path.Combine(root, "Hawkynt.FileFormats.Archives.csproj"))
      + File.ReadAllText(Path.Combine(root, "Directory.Build.targets"));
    var bundled = Regex.Matches(projectFiles, @"FileFormats\\(FileFormat\.[A-Za-z0-9]+)\\")
      .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    var readme = File.ReadAllText(Path.Combine(root, "README.md"));
    var section = Slice(readme, "## 🧩 Support matrix", "## 🚀 Quick start");
    var documented = Rows(section)
      .Where(r => r.Columns.IndexOf("Id") >= 0)
      .Select(r => r.Cells[r.Columns.IndexOf("Id")].Trim('`'))
      .ToHashSet(StringComparer.Ordinal);

    var missing = FormatRegistry.All
      .Where(d => bundled.Contains(d.GetType().Assembly.GetName().Name!.Replace("CompressionWorkbench.", "")))
      .Select(d => d.Id)
      .Where(id => !documented.Contains(id))
      .OrderBy(x => x, StringComparer.Ordinal)
      .ToArray();

    Assert.That(missing, Is.Empty,
      "Descriptors bundled into Hawkynt.FileFormats.Archives without a support-matrix row: " + string.Join(", ", missing));
  }

  private static bool Mark(string cell) => cell switch {
    "✅" => true,
    "—" => false,
    _ => throw new InvalidDataException($"Unknown capability marker '{cell}'."),
  };

  private static IEnumerable<(List<string> Columns, List<string> Cells)> Rows(string section) {
    List<string>? columns = null;
    var skipSeparator = false;
    foreach (var raw in section.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      var line = raw.Trim();
      if (!line.StartsWith('|')) { columns = null; continue; }
      var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToList();
      if (columns == null) { columns = cells; skipSeparator = true; continue; }
      if (skipSeparator) { skipSeparator = false; continue; }
      if (cells.Count != columns.Count) continue;
      yield return (columns, cells);
    }
  }

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
