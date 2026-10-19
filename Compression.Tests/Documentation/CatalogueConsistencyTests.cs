#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Documentation;

/// <summary>
/// Keeps <c>docs/catalogue/</c> honest against the live registries.
/// </summary>
/// <remarks>
/// <para><c>reference-formats.csv</c> maps every catalogued real-world format to the
/// descriptor or building-block ids that cover it. An id that is renamed or deleted
/// would leave the catalogue pointing at nothing, so a dangling id fails here — no
/// network, only the CSV and the registries.</para>
///
/// <para>The <c>ours_*</c> capability letters are a snapshot. When a descriptor is
/// promoted or demoted they drift; that is reported as a warning rather than a failure,
/// so an unrelated format change does not have to touch the catalogue in the same PR.
/// Set <c>CWB_WRITE_DOCS=1</c> to refresh the census (<c>our-registry.csv</c>) and the
/// capability letters.</para>
/// </remarks>
[TestFixture]
public class CatalogueConsistencyTests {

  private const string CataloguePath = "docs/catalogue/reference-formats.csv";
  private const string CensusPath = "docs/catalogue/our-registry.csv";
  private const string BlockPrefix = "bb:";

  private static readonly string[] GapTypes = ["MISSING", "READ-ONLY-BUT-SOURCES-WRITE", "PARTIAL", "OK", "OURS-ONLY"];

  [OneTimeSetUp]
  public void Setup() => Compression.Lib.FormatRegistration.EnsureInitialized();

  [Test]
  public void EveryReferencedId_NamesALiveDescriptorOrBuildingBlock() {
    var (header, rows) = ReadCatalogue();
    var ours = Column(header, "ours");
    var dangling = rows
      .SelectMany(r => SplitIds(r[ours]).Select(id => (Row: r[0], Id: id)))
      .Where(x => Resolve(x.Id) == null)
      .Select(x => $"{x.Row} -> {x.Id}")
      .ToList();
    Assert.That(dangling, Is.Empty,
      $"{CataloguePath} references ids that no longer exist (renamed or removed): " + string.Join(", ", dangling));
  }

  [Test]
  public void EveryRow_HasACoherentGapType() {
    var (header, rows) = ReadCatalogue();
    int ours = Column(header, "ours"), gap = Column(header, "gap");
    var sourceColumns = header.Select((h, i) => (h, i)).Where(x => x.h.StartsWith("src_", StringComparison.Ordinal)).Select(x => x.i).ToArray();
    Assert.That(sourceColumns, Is.Not.Empty, "The catalogue has no src_* columns.");

    var bad = new List<string>();
    foreach (var r in rows) {
      var hasOurs = SplitIds(r[ours]).Any();
      var listed = sourceColumns.Any(i => r[i].Length > 0);
      var g = r[gap];
      if (!GapTypes.Contains(g)) bad.Add($"{r[0]}: unknown gap '{g}'");
      else if (g == "MISSING" && hasOurs) bad.Add($"{r[0]}: MISSING but mapped to ours");
      else if (g != "MISSING" && !hasOurs) bad.Add($"{r[0]}: {g} but no id mapped");
      else if (g == "OURS-ONLY" && listed) bad.Add($"{r[0]}: OURS-ONLY but a source lists it");
      else if (g != "OURS-ONLY" && !listed) bad.Add($"{r[0]}: {g} but no source lists it");
    }
    Assert.That(bad, Is.Empty, string.Join(Environment.NewLine, bad));
  }

  [Test]
  public void TheCanonicalNames_AreUnique() {
    var (_, rows) = ReadCatalogue();
    var duplicates = rows.GroupBy(r => r[0], StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    Assert.That(duplicates, Is.Empty, "Duplicate canonical names: " + string.Join(", ", duplicates));
  }

  [Test]
  public void TheCapabilitySnapshot_MatchesTheDescriptors() {
    var root = FilesystemReadmeIsCurrentTests.RepositoryRoot();
    var (header, rows) = ReadCatalogue();
    int ours = Column(header, "ours"), read = Column(header, "ours_read"), create = Column(header, "ours_create"), modify = Column(header, "ours_modify");

    var drift = new List<string>();
    foreach (var r in rows) {
      var (rd, cr, md) = LiveCapabilities(SplitIds(r[ours]));
      if (r[read] != rd || r[create] != cr || r[modify] != md) {
        drift.Add($"{r[0]}: catalogue {r[read]}/{r[create]}/{r[modify]} live {rd}/{cr}/{md}");
        r[read] = rd; r[create] = cr; r[modify] = md;
      }
    }

    if (Environment.GetEnvironmentVariable("CWB_WRITE_DOCS") == "1") {
      WriteCsv(Path.Combine(root, CataloguePath), header, rows);
      WriteCsv(Path.Combine(root, CensusPath), CensusHeader, Census());
      Assert.Pass($"Refreshed {CensusPath} and the capability columns of {CataloguePath}.");
      return;
    }

    if (drift.Count > 0)
      Assert.Warn("Capability snapshot drifted (re-run with CWB_WRITE_DOCS=1): " + string.Join("; ", drift));
  }

  // ── census ───────────────────────────────────────────────────────────

  private static readonly string[] CensusHeader =
    ["kind", "id", "display_name", "category", "filesystem", "extensions", "list", "extract", "create", "modify"];

  private static List<string[]> Census() {
    var rows = new List<string[]>();
    var filesystems = FormatRegistry.FilesystemFormatIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var d in FormatRegistry.All.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)) {
      var c = d.Capabilities;
      var extensions = d.Extensions.Concat(d.CompoundExtensions).Distinct(StringComparer.OrdinalIgnoreCase);
      rows.Add([
        "format", d.Id, d.DisplayName, d.Category.ToString(), filesystems.Contains(d.Id) ? "Y" : "", string.Join(';', extensions),
        Flag(c, FormatCapabilities.CanList), Flag(c, FormatCapabilities.CanExtract),
        Flag(c, FormatCapabilities.CanCreate), Flag(c, FormatCapabilities.CanModify),
      ]);
    }
    foreach (var b in BuildingBlockRegistry.All.OrderBy(b => b.Id, StringComparer.OrdinalIgnoreCase))
      rows.Add(["block", BlockPrefix + b.Id, b.DisplayName, b.Family.ToString(), "", "", "", "Y", "Y", ""]);
    return rows;
  }

  private static string Flag(FormatCapabilities c, FormatCapabilities f) => (c & f) != 0 ? "Y" : "";

  // ── helpers ──────────────────────────────────────────────────────────

  private static object? Resolve(string id)
    => id.StartsWith(BlockPrefix, StringComparison.Ordinal)
      ? BuildingBlockRegistry.GetById(id[BlockPrefix.Length..])
      : FormatRegistry.GetById(id);

  /// <summary>Union of the capabilities of every mapped id: building blocks count as read+create.</summary>
  private static (string Read, string Create, string Modify) LiveCapabilities(IEnumerable<string> ids) {
    bool rd = false, cr = false, md = false;
    foreach (var id in ids)
      switch (Resolve(id)) {
        case IBuildingBlock:
          rd = cr = true;
          break;
        case IFormatDescriptor d:
          var c = d.Capabilities;
          rd |= (c & (FormatCapabilities.CanList | FormatCapabilities.CanExtract)) != 0 || d.Category == FormatCategory.Stream;
          cr |= (c & FormatCapabilities.CanCreate) != 0;
          md |= (c & FormatCapabilities.CanModify) != 0;
          break;
      }
    return (rd ? "Y" : "", cr ? "Y" : "", md ? "Y" : "");
  }

  private static IEnumerable<string> SplitIds(string cell)
    => cell.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  private static int Column(string[] header, string name) {
    var i = Array.IndexOf(header, name);
    Assert.That(i, Is.GreaterThanOrEqualTo(0), $"{CataloguePath} has no '{name}' column.");
    return i;
  }

  private static (string[] Header, List<string[]> Rows) ReadCatalogue() {
    var path = Path.Combine(FilesystemReadmeIsCurrentTests.RepositoryRoot(), CataloguePath);
    var records = ParseCsv(File.ReadAllText(path));
    Assert.That(records, Is.Not.Empty, $"{CataloguePath} is empty.");
    var header = records[0];
    var rows = records.Skip(1).Where(r => r.Length > 1 || r[0].Length > 0).ToList();
    var ragged = rows.Where(r => r.Length != header.Length).Select(r => r[0]).ToList();
    Assert.That(ragged, Is.Empty, "Rows whose field count differs from the header: " + string.Join(", ", ragged));
    return (header, rows);
  }

  /// <summary>RFC 4180: comma-separated, double-quoted fields may hold commas, quotes ("") and newlines.</summary>
  internal static List<string[]> ParseCsv(string text) {
    var records = new List<string[]>();
    var fields = new List<string>();
    var field = new StringBuilder();
    var quoted = false;
    for (var i = 0; i < text.Length; ++i) {
      var ch = text[i];
      if (quoted) {
        if (ch != '"') field.Append(ch);
        else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); ++i; }
        else quoted = false;
        continue;
      }
      switch (ch) {
        case '"': quoted = true; break;
        case ',': fields.Add(field.ToString()); field.Clear(); break;
        case '\r': break;
        case '\n':
          fields.Add(field.ToString()); field.Clear();
          records.Add([.. fields]); fields.Clear();
          break;
        default: field.Append(ch); break;
      }
    }
    if (field.Length > 0 || fields.Count > 0) {
      fields.Add(field.ToString());
      records.Add([.. fields]);
    }
    return records;
  }

  private static void WriteCsv(string path, string[] header, IEnumerable<string[]> rows) {
    var sb = new StringBuilder();
    foreach (var r in rows.Prepend(header))
      sb.Append(string.Join(',', r.Select(Quote))).Append('\n');
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, sb.ToString());
  }

  private static string Quote(string s)
    => s.IndexOfAny([',', '"', '\n', '\r']) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
}
