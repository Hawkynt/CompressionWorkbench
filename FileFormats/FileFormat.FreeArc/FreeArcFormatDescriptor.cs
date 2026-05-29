#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.FreeArc;

/// <summary>Format descriptor for FreeArc compressed archives (.arc).</summary>
public sealed class FreeArcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the FreeArc archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the FreeArc archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new FreeArcReader(stream, leaveOpen: true);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new FreeArcWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new FreeArcReader(archive);
    foreach (var e in r.Entries) {
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  /// <inheritdoc/>
  public string Id => "FreeArc";

  /// <inheritdoc/>
  public string DisplayName => "FreeArc";

  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <inheritdoc/>
  public string DefaultExtension => ".arc";

  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".arc"];

  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc/>
  // "ArC\x01" — the four-byte signature that opens every FreeArc archive.
  // Confidence is set high (0.95) because the combination of 'r', 'C' and 0x01
  // makes accidental collisions with the legacy ARC format (which uses 0x1A) extremely unlikely.
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([(byte)'A', (byte)'r', (byte)'C', 0x01], Confidence: 0.95)];

  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("freearc", "FreeArc")];

  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc/>
  public string Description => "FreeArc compressed archive";

  /// <inheritdoc/>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new FreeArcReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) =>
      new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize, e.Method, false, false, null))
      .ToList();
  }

  /// <inheritdoc/>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new FreeArcReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <inheritdoc/>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new FreeArcWriter();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }
}
