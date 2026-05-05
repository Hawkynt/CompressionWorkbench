#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WavPack;

/// <summary>
/// Surfaces a WavPack stream (<c>.wv</c>/<c>.wvc</c>) as a read-only archive of
/// its constituent blocks. Each 32-byte <c>wvpk</c> block header plus its body
/// is extracted verbatim as <c>block_NNNN.wv</c>; a <c>metadata.ini</c> lists
/// the top-level stream parameters (sample count, rate, channels, bit depth).
/// </summary>
public sealed class WavPackFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "WavPack";
  public string DisplayName => "WavPack (.wv)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".wv";
  public IReadOnlyList<string> Extensions => [".wv", ".wvc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("wvpk"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "WavPack hybrid lossless; each wvpk block surfaced verbatim.";

  private const int HeaderSize = 32;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
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

    var entries = new List<(string, string, byte[])>();

    var blockIndex = 0;
    var pos = 0;
    ParsedHeader? first = null;
    while (pos + HeaderSize <= file.Length) {
      // Magic "wvpk" at pos.
      if (file[pos] != 0x77 || file[pos + 1] != 0x76 || file[pos + 2] != 0x70 || file[pos + 3] != 0x6B) {
        // Skip one byte to re-sync; real files should be tightly packed.
        ++pos;
        continue;
      }
      var ckSize = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
      // Per WavPack: total block size = ckSize + 8.
      var blockSize = (long)ckSize + 8;
      if (blockSize < HeaderSize || pos + blockSize > file.Length) break;

      var header = ParseHeader(file.AsSpan(pos, HeaderSize));
      first ??= header;

      var blob = file.AsSpan(pos, (int)blockSize).ToArray();
      var name = $"block_{blockIndex:D4}.wv";
      entries.Add((name, "Block", blob));
      ++blockIndex;
      pos += (int)blockSize;
    }

    if (first.HasValue)
      entries.Add(("metadata.ini", "Metadata", Encoding.UTF8.GetBytes(BuildMetadata(first.Value))));

    return entries;
  }

  private readonly record struct ParsedHeader(
    ushort Version,
    uint TotalSamples,
    uint BlockIndex,
    uint BlockSamples,
    uint Flags
  );

  private static ParsedHeader ParseHeader(ReadOnlySpan<byte> hdr) {
    // Layout (little-endian):
    //   0:  char ckID[4]       "wvpk"
    //   4:  u32  ckSize         block size - 8
    //   8:  u16  version        0x402..0x410
    //  10:  u8   block_index_u8 (high byte of 40-bit counters)
    //  11:  u8   total_samples_u8
    //  12:  u32  total_samples  (low 32 bits)
    //  16:  u32  block_index    (low 32 bits)
    //  20:  u32  block_samples
    //  24:  u32  flags
    //  28:  u32  crc
    var version = BinaryPrimitives.ReadUInt16LittleEndian(hdr[8..]);
    var total = BinaryPrimitives.ReadUInt32LittleEndian(hdr[12..]);
    var idx = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..]);
    var samples = BinaryPrimitives.ReadUInt32LittleEndian(hdr[20..]);
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(hdr[24..]);
    return new ParsedHeader(version, total, idx, samples, flags);
  }

  private static string BuildMetadata(ParsedHeader h) {
    // Bits-per-sample encoded as 2-bit field in flags[0..1]: 0=8, 1=16, 2=24, 3=32.
    var bps = ((int)(h.Flags & 0x3) + 1) * 8;
    // Sample rate index is flags[23..26] (4 bits) with the following table.
    var rateIdx = (int)((h.Flags >> 23) & 0xF);
    var sampleRate = rateIdx switch {
      0 => 6000, 1 => 8000, 2 => 9600, 3 => 11025, 4 => 12000,
      5 => 16000, 6 => 22050, 7 => 24000, 8 => 32000, 9 => 44100,
      10 => 48000, 11 => 64000, 12 => 88200, 13 => 96000, 14 => 192000,
      _ => 0,
    };
    // Mono flag = bit 2 of flags.
    var mono = (h.Flags & 0x4) != 0;
    var channels = mono ? 1 : 2; // Multichannel streams split across hybrid blocks; 2 is a baseline.

    var sb = new StringBuilder();
    sb.AppendLine("[wavpack]");
    sb.Append("version=0x").AppendLine(h.Version.ToString("X4", CultureInfo.InvariantCulture));
    sb.Append("total_samples=").AppendLine(h.TotalSamples.ToString(CultureInfo.InvariantCulture));
    sb.Append("sample_rate=").AppendLine(sampleRate.ToString(CultureInfo.InvariantCulture));
    sb.Append("channels=").AppendLine(channels.ToString(CultureInfo.InvariantCulture));
    sb.Append("bits_per_sample=").AppendLine(bps.ToString(CultureInfo.InvariantCulture));
    sb.Append("flags=0x").AppendLine(h.Flags.ToString("X8", CultureInfo.InvariantCulture));
    return sb.ToString();
  }
}
