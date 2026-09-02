#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ha;

/// <summary>
/// HA archive (Harri Hirvola) with ASC (sliding-window LZ + arithmetic coding) and HSC (context modelling + arithmetic coding) methods.
///
/// References:
/// <list type="bullet">
///   <item><description>HA.DOC shipped with the HA 0.999 archiver (Harri Hirvola) — the canonical format and method description</description></item>
///   <item><description>No online specification — the format is known from the archiver's released source code</description></item>
/// </list>
/// </summary>
public sealed class HaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the HA archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the HA archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new HaReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new HaWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddFile(n, d, DateTime.Now);
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
    if (archive.Length < 2) yield break;

    // 2-byte magic "HA"
    yield return new DefragBlockInfo(0, 2, DefragBlockKind.MetadataReserved, FileName: "HA Magic");

    var r = new HaReader(archive);
    foreach (var e in r.Entries) {
      var headerSize = e.DataOffset - (e.DataOffset - (17 + System.Text.Encoding.Latin1.GetByteCount(e.FileName) + 1));
      var headerStart = e.DataOffset - (17 + System.Text.Encoding.Latin1.GetByteCount(e.FileName) + 1);
      // Actually: header = 1(versionMethod) + 4(compSize) + 4(origSize) + 4(crc32) + 4(dosDateTime) + nameLen+1(null)
      var nameLen = System.Text.Encoding.Latin1.GetByteCount(e.FileName) + 1;
      var entryHeaderSize = 17 + nameLen;
      var entryHeaderStart = e.DataOffset - entryHeaderSize;
      yield return new DefragBlockInfo(entryHeaderStart, entryHeaderSize, DefragBlockKind.MetadataReserved, FileName: $"Header: {e.FileName}");
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
    }
  }

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ha";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "HA";
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
  /// Adds (or replaces by name) files inside an existing HA archive. Uses
  /// <see cref="HaModifier"/> — Add appends a Stored entry at EOF; Remove
  /// walks the entry chain and shifts trailing bytes (no central directory).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      HaModifier.RemoveFile(archive, name, wipeData: true);
      HaModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="HaModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      HaModifier.RemoveFile(archive, name, wipeData: true);
  }
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".ha";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ha"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'H', (byte)'A'], Confidence: 0.60)];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("ha", "HA")];
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
public string Description => "HA archive with arithmetic coding, ASC/HSC methods";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HaReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      $"Method {e.Method}", e.IsDirectory, false, e.LastModified)).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HaReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new HaWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
  }
}
