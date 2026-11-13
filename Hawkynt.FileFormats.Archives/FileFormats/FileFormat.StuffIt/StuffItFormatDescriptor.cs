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
public sealed class StuffItFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IFormatOptionsSchema {

  /// <summary>Rebuild-based defrag: extracts then re-creates the StuffIt archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the StuffIt archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new StuffItReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.Extract(e)));
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
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
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
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'S', (byte)'I', (byte)'T', (byte)'!'], Confidence: 0.95),
    new("StuffIt (c)1997-"u8.ToArray(), Confidence: 0.95),
    // The same archives as the data fork of a MacBinary file (128-byte header), which is how a
    // classic archive keeps the resource fork that holds its password check. They outrank the
    // generic MacBinary reading so that detection lands on the reader that decrypts them.
    new("SIT!\0\0\0\0\0\0rLau"u8.ToArray(), Offset: 128, Confidence: 0.9,
      Mask: [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]),
    new("StuffIt (c)1997-"u8.ToArray(), Offset: 128, Confidence: 0.9),
  ];
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
  public string Description => "Macintosh StuffIt archive (classic SIT! and StuffIt 5); reads methods None/RLE/Compress/Huffman/LZAH/LZ+Huffman/Arsenic, writes None/RLE/LZ+Huffman in classic and StuffIt 5 containers";

  private const string Classic = "Classic";
  private const string StuffIt5 = "StuffIt5";

  /// <summary>The container the writer emits.</summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: FormatOptionKeys.TargetCompatibility,
      DisplayName: "Container",
      Kind: FormatOptionKind.Enum,
      Default: Classic,
      AllowedValues: [Classic, StuffIt5],
      Description: "Classic writes a \"SIT!\" archive (StuffIt 1-4); StuffIt5 writes the container of StuffIt 5 and later.",
      IsOptimizationAxis: false),
    new FormatOptionDescriptor(
      Key: "Compression",
      DisplayName: "Compression",
      Kind: FormatOptionKind.Enum,
      Default: nameof(StuffItCompression.Smallest),
      AllowedValues: Enum.GetNames<StuffItCompression>(),
      Description: "Smallest tries Arsenic (method 15), LZ+Huffman (13) and RLE (1) for every fork and keeps the smallest; the others force one method (storing a fork it would enlarge)."),
  ];

  private static StuffItCompression CompressionOf(FormatCreateOptions options)
    => Enum.TryParse<StuffItCompression>(options.GetString("Compression"), ignoreCase: true, out var c) ? c : StuffItCompression.Smallest;

  /// <summary>Suffix under which a file's resource fork is listed and extracted.</summary>
  public const string ResourceForkSuffix = ".rsrc";

  private static IEnumerable<(StuffItEntry Entry, bool Resource, string Name)> Items(StuffItReader r) {
    foreach (var e in r.Entries) {
      if (e.IsDirectory) { yield return (e, false, e.FileName); continue; }
      if (e.DataForkSize > 0 || !e.HasResourceFork) yield return (e, false, e.FileName);
      if (e.HasResourceFork) yield return (e, true, e.FileName + ResourceForkSuffix);
    }
  }

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new StuffItReader(stream, leaveOpen: true);
    return Items(r).Select((x, i) => new ArchiveEntryInfo(i, x.Name,
      x.Entry.IsDirectory ? 0 : x.Resource ? x.Entry.ResourceForkSize : x.Entry.DataForkSize,
      x.Entry.IsDirectory ? 0 : x.Resource ? x.Entry.CompressedResourceSize : x.Entry.CompressedDataSize,
      x.Entry.IsDirectory ? "" : StuffItReader.MethodName(x.Resource ? x.Entry.ResourceMethod : x.Entry.DataMethod),
      x.Entry.IsDirectory, x.Entry.IsEncrypted, x.Entry.LastModified)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new StuffItReader(stream, password, leaveOpen: true);
    foreach (var (e, resource, name) in Items(r)) {
      if (files != null && !MatchesFilter(name, files)) continue;
      if (e.IsDirectory) continue; // folders come into being with the files inside them
      WriteFile(outputDir, name, resource ? r.ExtractResourceFork(e) : r.Extract(e));
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
    var r = new StuffItReader(archive, password, leaveOpen: true);
    foreach (var (e, resource, name) in Items(r)) {
      if (e.IsDirectory || !string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = resource ? r.ExtractResourceFork(e) : r.Extract(e);
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
  /// <remarks>
  /// Writes a classic "SIT!" archive, or a StuffIt 5 archive when
  /// <see cref="FormatOptionKeys.TargetCompatibility"/> is <c>StuffIt5</c>. The compression option
  /// selects store, RLE, LZ+Huffman, Arsenic, or the smallest encoding for each fork. A password
  /// encrypts a StuffIt 5 archive with StuffIt 5's RC4 scheme, and a classic archive with classic
  /// StuffIt's scheme, written as a MacBinary file whose resource fork holds the key check.
  /// </remarks>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    if (string.Equals(options.GetString(FormatOptionKeys.TargetCompatibility), StuffIt5, StringComparison.OrdinalIgnoreCase)) {
      using var w5 = new StuffIt5Writer(output, CompressionOf(options), options.Password, leaveOpen: true);
      foreach (var i in inputs) {
        if (i.IsDirectory) w5.AddDirectory(i.ArchiveName);
        else w5.AddFile(i.ArchiveName, i.ReadContent());
      }
      return;
    }
    // A password makes the classic archive a MacBinary file: its resource fork carries the key check.
    using var w = new StuffItWriter(output, CompressionOf(options), options.Password, leaveOpen: true);
    foreach (var i in inputs) {
      if (i.IsDirectory) w.AddDirectory(i.ArchiveName);
      else w.AddFile(i.ArchiveName, i.ReadContent());
    }
  }
}
