#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.StuffItX;

/// <summary>
/// StuffIt X (.sitx) archive (Aladdin/Smith Micro) — proprietary element-stream container.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/MacPaw/XADMaster</c> — XADMaster (The Unarchiver) — partial open StuffIt X decoder</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/StuffIt</c> — Wikipedia — covers StuffIt X</description></item>
///   <item><description>proprietary format; the element-stream codecs have no public specification</description></item>
/// </list>
/// </summary>
public sealed class StuffItXFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "StuffIt X writer only embeds a single opaque payload; rebuilding from extracted entries " +
      "would not match the original on-disk structure.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new StuffItXReader(archive);
    foreach (var e in r.Entries) {
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "StuffItX";
  public string DisplayName => "StuffIt X";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".sitx";
  public IReadOnlyList<string> Extensions => [".sitx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("StuffIt"u8.ToArray(), Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("sitx", "StuffIt X")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "StuffIt X archive (Aladdin/Smith Micro). Read-only modify by writer scope: " +
    "our StuffItXWriter emits the documented `StuffIt!` magic + header pointer + " +
    "a single embedded opaque payload at the catalog offset; the proprietary " +
    "element-stream encoding (Brimstone PPMd, Darkhorse LZSS, Cyanide/Iron BWT) " +
    "has no public spec, so we cannot append a real new element. Even a stored " +
    "append would shift catalog absolute offsets stored in the P2 length headers " +
    "of every later element. This descriptor advertises CanCreate (single-payload " +
    "embed only) but does not implement IArchiveModifiable.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new StuffItXReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.OriginalSize,
      e.CompressedSize, e.Method, e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new StuffItXReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single StuffIt X entry as a bounded read-only <see cref="Stream"/>.
  /// The reader's per-entry extractor returns the decompressed bytes;
  /// they are wrapped in a <see cref="BoundedEntryStream"/> sized to the
  /// entry's original size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new StuffItXReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FullPath, entryName, StringComparison.OrdinalIgnoreCase)) continue;
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    byte[]? embedded = null;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      embedded = i.ReadContent();
      break;
    }
    new StuffItXWriter().WriteTo(output, embedded);
  }
}
