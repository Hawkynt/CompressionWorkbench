#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for Shrinkler-packed Amiga binaries — Blueberry's
/// range-coding-based context-mixing exe compressor used by virtually every
/// modern 4K and 64K Amiga demoscene production. The compressor emits a
/// distinctive header containing a magic identifier and the original size.
/// </summary>
/// <remarks>
/// <para>
/// Shrinkler header layout (big-endian, used for executable variants):
/// </para>
/// <list type="bullet">
///   <item>Bytes 0..3: magic <c>$00 $00 $03 $F3</c> — same as the AmigaOS HUNK_HEADER magic, since Shrinkler-packed binaries are loadable as AmigaOS hunks.</item>
///   <item>The first hunk is a tiny decompression stub that contains the literal ASCII string <c>"Shrinkler"</c>.</item>
///   <item>Subsequent hunks hold the compressed data; an internal header stores the original size and adler-32 checksum.</item>
/// </list>
/// <para>
/// Detection here looks for the AmigaOS HUNK magic AND the embedded
/// <c>"Shrinkler"</c> string within the first 64 KB; either alone is too
/// permissive (HUNK is generic; "Shrinkler" might appear in tag strings).
/// </para>
/// </remarks>
public sealed class ShrinklerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Shrinkler";
  public string DisplayName => "Shrinkler (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".shr";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Shrinkler (Blueberry) — range-coded context-mixing compressor used by " +
    "Amiga 4K/64K demoscene productions. Detection by AmigaOS HUNK magic + " +
    "embedded \"Shrinkler\" stub identifier; decompression delegated to the " +
    "Shrinkler reference tool.";

  /// <summary>AmigaOS HUNK_HEADER magic (big-endian 0x000003F3).</summary>
  private static ReadOnlySpan<byte> HunkMagic => [0x00, 0x00, 0x03, 0xF3];
  private static ReadOnlySpan<byte> ShrinklerLiteral => "Shrinkler"u8;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var bytes = ms.ToArray();

    if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual(HunkMagic))
      throw new InvalidDataException("Shrinkler: file does not start with the AmigaOS HUNK_HEADER magic.");

    var literalIdx = PackerScanner.IndexOfBounded(bytes, ShrinklerLiteral, 0x10000);
    if (literalIdx < 0)
      throw new InvalidDataException("Shrinkler: HUNK header present but \"Shrinkler\" identifier not found.");

    return [
      ("metadata.ini", BuildMetadata(bytes, literalIdx)),
      ("hunk_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(ReadOnlySpan<byte> data, int literalOffset) {
    var sb = new StringBuilder();
    sb.AppendLine("[shrinkler]");
    sb.Append(CultureInfo.InvariantCulture, $"shrinkler_marker_offset = 0x{literalOffset:X4}\n");

    // The HUNK header stores hunk count (u32 BE) and first/last hunk indices.
    if (data.Length >= 16) {
      var resBlocks = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
      var hunkCount = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
      var first = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
      sb.Append(CultureInfo.InvariantCulture, $"resident_blocks = {resBlocks}\n");
      sb.Append(CultureInfo.InvariantCulture, $"hunk_count = {hunkCount}\n");
      sb.Append(CultureInfo.InvariantCulture, $"first_hunk_index = {first}\n");
    }
    sb.Append("note = decompression delegated to the Shrinkler reference tool\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
