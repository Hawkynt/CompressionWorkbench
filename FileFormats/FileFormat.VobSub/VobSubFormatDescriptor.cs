#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.VobSub;

/// <summary>
/// Pseudo-archive descriptor for VobSub DVD subtitles. The primary file is the textual
/// <c>.idx</c>; the binary <c>.sub</c> sibling is resolved by replacing the extension.
/// Each subtitle frame from the <c>.sub</c> is exposed as <c>subtitle_NNN.bin</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://sam.zoy.org/writings/dvd/subtitles/</c> — Sam Hocevar's classic DVD subtitle (SPU/RLE) format description</description></item>
///   <item><description>VobSub / DirectVobSub (Gabest) — the defining tool producing .idx/.sub pairs</description></item>
/// </list>
/// </summary>
/// <remarks>
/// When invoked without filesystem context (pure stream input), only the parsed index
/// metadata is returned — the .sub sibling cannot be discovered. The <see cref="ListPair"/>
/// / <see cref="ExtractPair"/> overloads accept both files explicitly for callers that
/// have filesystem access.
/// </remarks>
public sealed class VobSubFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "VobSub";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "VobSub DVD Subtitles";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".idx";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".idx"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "# VobSub index file" — first 19 bytes of the .idx text header.
    new("# VobSub index file"u8.ToArray(), Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
  public string Description => "VobSub DVD subtitle index (.idx) plus sibling MPEG-PS subtitle stream (.sub).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (idxBytes, subBytes) = ReadIndexAndSibling(stream);
    return BuildEntries(idxBytes, subBytes).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (idxBytes, subBytes) = ReadIndexAndSibling(stream);
    foreach (var e in BuildEntries(idxBytes, subBytes)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Opens a single VobSub entry as a bounded read-only stream. The
  /// <c>metadata.ini</c> + <c>index.idx</c> + per-frame entries each
  /// produce a decoded byte buffer; the matched buffer is wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var (idxBytes, subBytes) = ReadIndexAndSibling(archive);
    foreach (var e in BuildEntries(idxBytes, subBytes)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    var (idxBytes, subBytes) = ReadIndexAndSibling(input);
    foreach (var e in BuildEntries(idxBytes, subBytes))
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  /// <summary>
  /// Lists entries given both files explicitly (preferred when the caller has filesystem
  /// access and can locate the sibling .sub).
  /// </summary>
  public List<ArchiveEntryInfo> ListPair(byte[] idxBytes, byte[] subBytes) =>
    BuildEntries(idxBytes, subBytes).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();

  /// <summary>
  /// Extracts entries given both files explicitly (preferred when the caller has filesystem
  /// access and can locate the sibling .sub).
  /// </summary>
  public void ExtractPair(byte[] idxBytes, byte[] subBytes, string outputDir, string[]? files) {
    foreach (var e in BuildEntries(idxBytes, subBytes)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Reads the .idx bytes from <paramref name="stream"/>; if the stream is a
  /// <see cref="FileStream"/>, also resolves the sibling .sub by extension swap.
  /// Returns an empty .sub byte array when no sibling is reachable.
  /// </summary>
  private static (byte[] Idx, byte[] Sub) ReadIndexAndSibling(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var idxBytes = ms.ToArray();

    var subBytes = Array.Empty<byte>();
    if (stream is FileStream fs) {
      var idxPath = fs.Name;
      // Sibling .sub: same base name, .sub extension. Match case-insensitively.
      var dir = Path.GetDirectoryName(idxPath);
      var stem = Path.GetFileNameWithoutExtension(idxPath);
      if (dir != null && stem != null) {
        var subPath = Path.Combine(dir, stem + ".sub");
        if (File.Exists(subPath)) subBytes = File.ReadAllBytes(subPath);
      }
    }
    return (idxBytes, subBytes);
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(byte[] idxBytes, byte[] subBytes) {
    var pair = VobSubReader.Read(idxBytes, subBytes);

    var result = new List<(string, string, byte[])> {
      ("metadata.ini", "Tag", BuildMetadata(pair, subBytes.Length)),
      ("index.idx", "Tag", idxBytes),
    };
    for (var i = 0; i < pair.Frames.Count; i++)
      result.Add(($"subtitle_{i:D3}.bin", "Payload", pair.Frames[i]));
    return result;
  }

  private static byte[] BuildMetadata(VobSubReader.Pair pair, int subBytesLength) {
    var sb = new StringBuilder();
    sb.AppendLine("[vobsub]");
    sb.Append(CultureInfo.InvariantCulture, $"size = {pair.Index.Width}x{pair.Index.Height}\n");
    sb.Append("language = ").AppendLine(pair.Index.Language ?? "(unset)");
    sb.Append("palette_entries = ").Append(pair.Index.Palette.Count).Append('\n');
    sb.Append("frame_count = ").Append(pair.Frames.Count).Append('\n');
    sb.Append("sub_bytes_available = ").Append(subBytesLength).Append('\n');
    if (pair.Index.Entries.Count > 0) {
      sb.Append(CultureInfo.InvariantCulture, $"first_timestamp = {pair.Index.Entries[0].Timestamp:c}\n");
      sb.Append(CultureInfo.InvariantCulture, $"last_timestamp = {pair.Index.Entries[^1].Timestamp:c}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
