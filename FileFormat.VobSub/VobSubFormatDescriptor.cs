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
/// </summary>
/// <remarks>
/// When invoked without filesystem context (pure stream input), only the parsed index
/// metadata is returned — the .sub sibling cannot be discovered. The <see cref="ListPair"/>
/// / <see cref="ExtractPair"/> overloads accept both files explicitly for callers that
/// have filesystem access.
/// </remarks>
public sealed class VobSubFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "VobSub";
  public string DisplayName => "VobSub DVD Subtitles";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".idx";
  public IReadOnlyList<string> Extensions => [".idx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "# VobSub index file" — first 19 bytes of the .idx text header.
    new("# VobSub index file"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "VobSub DVD subtitle index (.idx) plus sibling MPEG-PS subtitle stream (.sub).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (idxBytes, subBytes) = ReadIndexAndSibling(stream);
    return BuildEntries(idxBytes, subBytes).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (idxBytes, subBytes) = ReadIndexAndSibling(stream);
    foreach (var e in BuildEntries(idxBytes, subBytes)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

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
