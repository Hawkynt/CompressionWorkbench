#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lzh;

/// <summary>
/// LHA/LZH archive — the LZSS+Huffman archiver family historically dominant in Japan and on the Amiga.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/jca02266/lha</c> — LHa for UNIX — maintained canonical implementation; header layouts documented in the source tree</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/LHA_(file_format)</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class LzhFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => LzhLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag: extracts then re-creates the LHA archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the LHA archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new LhaReader(stream);
        return r.Entries.Select(e => (e.FileName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new LhaWriter(LhaConstants.MethodLh5);
        foreach (var (n, d) in files) w.AddFile(n, d);
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Lzh";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZH";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing LHA/LZH archive.
  /// Uses <see cref="LhaModifier"/> — Add appends after the EOF position,
  /// Remove walks the chain and shifts trailing bytes (no central directory).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      LhaModifier.RemoveFile(archive, name, wipeData: true);
      LhaModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="LhaModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      LhaModifier.RemoveFile(archive, name, wipeData: true);
  }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".lzh";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".lzh", ".lha"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'-', (byte)'l'], Offset: 2, Confidence: 0.70)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("lh5", "LH5"), new("lh0", "LH0 (Store)"), new("lh1", "LH1"), new("lh4", "LH4"),
    new("lh6", "LH6"), new("lh7", "LH7"), new("lzs", "LZS"), new("lz5", "LZ5"),
    new("pm0", "PM0"), new("pm1", "PM1"), new("pm2", "PM2")
  ];
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
  public string Description => "LHA/LZH archive, popular in Japan, Amiga";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LhaReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.Method, false, false, e.LastModified)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LhaReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  /// <summary>
  /// Opens a single LHA/LZH entry as a bounded read-only <see cref="Stream"/>.
  /// The reader's per-entry extractor returns the fully-decompressed bytes;
  /// they are wrapped in a <see cref="BoundedEntryStream"/> sized to the
  /// entry's original size so callers see the universal isolation contract.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new LhaReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractEntry(e);
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
    var lzhMethod = options.MethodName switch {
      "lh0" or "store" => LhaConstants.MethodLh0,
      "lh1" => LhaConstants.MethodLh1,
      "lh2" => LhaConstants.MethodLh2,
      "lh3" => LhaConstants.MethodLh3,
      "lh4" => LhaConstants.MethodLh4,
      "lh6" => LhaConstants.MethodLh6,
      "lh7" => LhaConstants.MethodLh7,
      "lzs" => LhaConstants.MethodLzs,
      "lz5" => LhaConstants.MethodLz5,
      "pm0" => LhaConstants.MethodPm0,
      "pm1" => LhaConstants.MethodPm1,
      "pm2" => LhaConstants.MethodPm2,
      _ => LhaConstants.MethodLh5,
    };
    var w = new LhaWriter(lzhMethod);
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }
}
