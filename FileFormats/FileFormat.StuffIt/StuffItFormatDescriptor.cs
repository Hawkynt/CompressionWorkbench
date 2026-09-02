#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.StuffIt;

/// <summary>
/// Macintosh StuffIt (SIT) archive — classic Mac compression with resource/data fork entries.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — XADMaster (The Unarchiver) — open StuffIt decoder, the de-facto format reference</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/StuffIt</c> — Wikipedia overview</description></item>
///   <item><description>Aladdin Systems StuffIt — proprietary; no official spec published</description></item>
/// </list>
/// </summary>
public sealed class StuffItFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the StuffIt archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the StuffIt archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new StuffItReader(stream);
        return r.Entries.Select(e => (e.FileName, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new StuffItWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddFile(n, d);
        }
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new StuffItReader(archive);
    foreach (var e in r.Entries) {
      if (e.CompressedDataSize > 0)
        yield return new DefragBlockInfo(e.DataForkOffset, e.CompressedDataSize, DefragBlockKind.Used, FileName: e.FileName);
      if (e.CompressedResourceSize > 0)
        yield return new DefragBlockInfo(e.ResourceDataOffset, e.CompressedResourceSize, DefragBlockKind.Used, FileName: e.FileName + " (resource)");
    }
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "StuffIt";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "StuffIt";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".sit";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sit"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'S', (byte)'I', (byte)'T', (byte)'!'], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stuffit", "StuffIt")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Macintosh StuffIt archive, classic Mac compression";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new StuffItReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.DataForkSize,
      e.CompressedDataSize, $"Method {e.DataMethod}", false, false, e.LastModified)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new StuffItReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single StuffIt entry as a bounded read-only <see cref="Stream"/>.
  /// The reader's per-entry extractor returns the data-fork bytes;
  /// they are wrapped in a <see cref="BoundedEntryStream"/> sized to the
  /// entry's data-fork size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new StuffItReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new StuffItWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
  }
}
