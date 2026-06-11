#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Tta;

/// <summary>
/// Exposes a True Audio (.tta) lossless file as a read-only archive of
/// <c>FULL.tta</c>, a <c>metadata.ini</c> describing the header (channels, bits,
/// sample rate, sample count), one raw block per frame carved via the seek table
/// (<c>frames/frame_NNNN.bin</c>), and a <c>tags.ini</c> when an ID3v2 or APEv2
/// tag is present.
/// </summary>
/// <remarks>
/// <para>The TTA1 header is: magic "TTA1" + format(u16) + channels(u16) +
/// bits-per-sample(u16) + sample-rate(u32) + data-length-in-samples(u32) +
/// header CRC32(u32). It is followed by a seek table of one u32 compressed-frame
/// size per frame plus a trailing CRC32. The frame length in samples is
/// <c>floor(sampleRate * 256 / 245)</c>, so the frame count is
/// <c>ceil(dataLength / frameLength)</c>.</para>
/// <para><b>Deferred:</b> the adaptive-filter + Rice audio decode is not
/// implemented — this descriptor surfaces the container structure (header, seek
/// table, per-frame compressed blocks, tags) only; it does not reconstruct PCM.</para>
/// </remarks>
public sealed class TtaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "Tta";
  public string DisplayName => "True Audio (.tta)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tta";
  public IReadOnlyList<string> Extensions => [".tta"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TTA1"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored"), new("tta", "TTA")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description =>
    "True Audio lossless; full file + header metadata + per-frame blocks (seek table) + ID3/APE tags. Audio decode deferred — structural only.";

  private static readonly byte[] ApeTagMagic = "APETAGEX"u8.ToArray();

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Kind == "Frame" ? "tta" : "stored",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();

    var entries = new List<(string, string, byte[])> {
      ("FULL.tta", "Container", file),
    };

    var meta = new StringBuilder();
    meta.AppendLine("[tta]");

    // A leading ID3v2 tag may precede the TTA1 magic; account for it so we still
    // find the header.
    var headerOffset = SkipLeadingId3v2(file);

    if (file.Length < headerOffset + 22 ||
        file[headerOffset] != 'T' || file[headerOffset + 1] != 'T' ||
        file[headerOffset + 2] != 'A' || file[headerOffset + 3] != '1') {
      meta.AppendLine("parse_status=partial");
      entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
      return entries;
    }

    var format = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(headerOffset + 4));
    var channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(headerOffset + 6));
    var bits = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(headerOffset + 8));
    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(headerOffset + 10));
    var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(headerOffset + 14));
    // headerOffset + 18..22 = header CRC32

    meta.Append("format=").AppendLine(format.ToString(CultureInfo.InvariantCulture));
    meta.Append("channels=").AppendLine(channels.ToString(CultureInfo.InvariantCulture));
    meta.Append("bits_per_sample=").AppendLine(bits.ToString(CultureInfo.InvariantCulture));
    meta.Append("sample_rate=").AppendLine(sampleRate.ToString(CultureInfo.InvariantCulture));
    meta.Append("sample_count=").AppendLine(dataLength.ToString(CultureInfo.InvariantCulture));
    if (sampleRate > 0)
      meta.Append("duration_seconds=")
        .AppendLine(((double)dataLength / sampleRate).ToString("0.###", CultureInfo.InvariantCulture));

    // Frame length in samples: floor(sampleRate * 256 / 245). Frame count:
    // ceil(dataLength / frameLength).
    if (sampleRate > 0 && dataLength > 0) {
      var frameLength = (long)sampleRate * 256 / 245;
      if (frameLength > 0) {
        var frameCount = (int)((dataLength + frameLength - 1) / frameLength);
        meta.Append("frame_count=").AppendLine(frameCount.ToString(CultureInfo.InvariantCulture));

        // Seek table: frameCount × u32 frame sizes + u32 CRC, immediately after
        // the 22-byte header.
        var seekTableStart = headerOffset + 22;
        var seekTableBytes = (frameCount + 1) * 4; // sizes + trailing CRC
        var frameDataStart = seekTableStart + seekTableBytes;
        if (frameDataStart <= file.Length) {
          var pos = frameDataStart;
          for (var i = 0; i < frameCount; ++i) {
            var sizeOff = seekTableStart + i * 4;
            var frameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(sizeOff));
            if (frameSize <= 0 || pos + frameSize > file.Length) break;
            entries.Add(($"frames/frame_{i:D4}.bin", "Frame",
              file.AsSpan(pos, frameSize).ToArray()));
            pos += frameSize;
          }
        }
      }
    }

    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    // Tags: ID3v2 at the very front (before TTA1), or APEv2 footer at the tail.
    var tags = ExtractTags(file, headerOffset);
    if (tags != null)
      entries.Add(("tags.ini", "Tag", Encoding.UTF8.GetBytes(tags)));

    return entries;
  }

  // A leading ID3v2 tag is "ID3" + version(2) + flags(1) + syncsafe-size(4); the
  // header total length is 10 + size. Returns the byte offset of the TTA1 magic.
  private static int SkipLeadingId3v2(byte[] file) {
    if (file.Length < 10 || file[0] != 'I' || file[1] != 'D' || file[2] != '3') return 0;
    var size = (file[6] << 21) | (file[7] << 14) | (file[8] << 7) | file[9];
    var offset = 10 + size;
    return offset >= 0 && offset + 4 <= file.Length ? offset : 0;
  }

  private static string? ExtractTags(byte[] file, int headerOffset) {
    var sb = new StringBuilder();

    // Leading ID3v2 tag (before the header) — report its presence + size.
    if (headerOffset > 0 && file.Length >= 10 &&
        file[0] == 'I' && file[1] == 'D' && file[2] == '3') {
      sb.AppendLine("[id3v2]");
      sb.Append("version=2.").Append(file[3]).Append('.').AppendLine(file[4].ToString(CultureInfo.InvariantCulture));
      sb.Append("size_bytes=").AppendLine(headerOffset.ToString(CultureInfo.InvariantCulture));
    }

    // Trailing APEv2 footer (last 32 bytes, possibly before an ID3v1 trailer).
    var apeFooter = FindApeFooter(file);
    if (apeFooter >= 0) {
      var tagSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(apeFooter + 12));
      var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(apeFooter + 16));
      var itemsStart = apeFooter + 32 - tagSize;
      if (itemsStart >= 0 && tagSize >= 32) {
        sb.AppendLine("[apev2]");
        ParseApeItems(file, itemsStart, apeFooter, itemCount, sb);
      }
    }

    return sb.Length > 0 ? sb.ToString() : null;
  }

  private static int FindApeFooter(byte[] file) {
    // Search the last 256 bytes for the APETAGEX footer magic.
    var start = Math.Max(0, file.Length - 256);
    for (var p = file.Length - 32; p >= start; --p) {
      if (MatchesMagic(file, p, ApeTagMagic)) {
        // The footer flag bit 29 (0x20000000) distinguishes footer from header;
        // both are acceptable for our reporting.
        return p;
      }
    }
    return -1;
  }

  private static void ParseApeItems(byte[] file, int itemsStart, int itemsEnd, uint itemCount, StringBuilder sb) {
    var pos = itemsStart;
    for (var i = 0; i < itemCount && i < 256; ++i) {
      if (pos + 8 > itemsEnd) break;
      var valueLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
      pos += 8;
      var keyStart = pos;
      while (pos < itemsEnd && file[pos] != 0) ++pos;
      if (pos >= itemsEnd) break;
      var key = Encoding.ASCII.GetString(file, keyStart, pos - keyStart);
      ++pos;
      if (valueLen < 0 || pos + valueLen > itemsEnd) break;
      var isText = ((flags >> 1) & 0x03) == 0;
      if (isText)
        sb.Append(key).Append('=')
          .AppendLine(Encoding.UTF8.GetString(file, pos, valueLen).Replace("\0", "; "));
      else
        sb.Append("; ").Append(key).Append(" (binary, ").Append(valueLen).AppendLine(" bytes)");
      pos += valueLen;
    }
  }

  private static bool MatchesMagic(byte[] buffer, int offset, byte[] magic) {
    if (offset < 0 || offset + magic.Length > buffer.Length) return false;
    for (var i = 0; i < magic.Length; ++i)
      if (buffer[offset + i] != magic[i]) return false;
    return true;
  }
}
