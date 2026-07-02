#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Shar;

/// <summary>
/// Shell archive (shar) — self-extracting Unix shell script carrying files as here-documents.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.gnu.org/software/sharutils/</c> — GNU sharutils — shar/unshar reference implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Shar</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class SharFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable {

  /// <summary>Rebuild-based defrag: extracts then re-creates the SHAR archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the SHAR archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new SharReader(stream);
        return r.Entries.Select(e => (e.FileName, e.Data));
      },
      buildImage: files => {
        var w = new SharWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }

  public string Id => "Shar";
  public string DisplayName => "SHAR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Appends file entries to an existing shell archive. Shar's trailing
  /// <c>exit 0</c> sentinel is overwritten with the new entry's
  /// <c>echo x - name</c> block (heredoc for text, uudecode for binary) and
  /// a fresh <c>exit 0</c> sentinel — bytes before the old sentinel are
  /// byte-identical after the operation. See <see cref="SharInPlaceModifier"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      SharInPlaceModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// In-place Remove is not implemented for Shar — the heredoc/uudecode
  /// block boundaries depend on arbitrary user content and cannot be
  /// scanned safely without re-parsing the whole script. Callers should
  /// rebuild via the rebuild-based <c>Defragment</c> path instead.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) =>
    throw new NotSupportedException(
      "Shar Remove is not implemented in-place — rebuild via the defragmenter " +
      "to drop entries (heredoc bodies can contain arbitrary delimiter-lookalike text).");
  public string DefaultExtension => ".shar";
  public IReadOnlyList<string> Extensions => [".shar", ".sh"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'#', (byte)'!', (byte)' ', (byte)'/', (byte)'b', (byte)'i', (byte)'n', (byte)'/', (byte)'s', (byte)'h'], Confidence: 0.50)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("shar", "SHAR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Shell archive, self-extracting Unix script";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SharReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.Data.Length, e.Data.Length,
      "shar", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SharReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, e.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new SharWriter();
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
