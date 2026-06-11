#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Far;

/// <summary>
/// Exposes a Farandole Composer (FAR) module as an archive of <c>FULL.far</c>,
/// <c>metadata.ini</c>, <c>patterns/pattern_NN.bin</c> (raw packed pattern blocks
/// with their 2-byte length prefix stripped, NO decode) and
/// <c>samples/NN_{name}.raw</c> per non-empty sample (raw PCM). The FAR layout
/// was recovered through binary inspection of the documented Farandole Composer
/// file format and the OpenMPT/libmodplug loaders.
/// </summary>
public sealed class FarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Far";
  public string DisplayName => "FAR (Farandole Composer)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".far";
  public IReadOnlyList<string> Extensions => [".far"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'F', (byte)'A', (byte)'R', 0xFE], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Farandole Composer module; full file + 16-channel pattern blocks + raw signed 8-bit PCM samples.";

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

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Parse(ms.ToArray());
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> Parse(byte[] blob) {
    var entries = new List<(string, string, byte[])> {
      ("FULL.far", "Track", blob),
    };
    var validMagic = blob.Length >= 4 &&
      blob[0] == 'F' && blob[1] == 'A' && blob[2] == 'R' && blob[3] == 0xFE;
    // Fixed header is 98 bytes (before the variable pattern-size table).
    if (blob.Length < 98 || !validMagic) {
      AddPartial(entries);
      return entries;
    }

    var title = ReadAsciiTrim(blob, 4, 40);
    var headerLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(47, 2));
    var version = blob[49];
    var channelsOn = 0;
    for (var c = 0; c < 16; ++c)
      if (blob[50 + c] != 0) ++channelsOn;

    // Layout after channel bytes:
    //   50..65 channel on/off (16)
    //   66..81 editing state (16)
    //   82     break position
    //   83     panning (16) -> 83..98
    //   99..   reserved (1 byte editing string len) ... but per spec orders begin at 0x42? Use documented offsets:
    //   0x42 (66) order list (256), 0x142 (322) numPatterns, 0x143 songLen, 0x144 restart,
    //   0x145 (325) pattern size table: 256 u16.
    var numPatterns = blob[0x142];  // 322
    var restart = blob[0x143];      // 323 (songLength/restart per variant)
    var patternSizeTableOff = 0x145; // 325

    // Read up to 256 pattern sizes (u16). Sizes of 0 indicate no pattern.
    var patternSizes = new int[256];
    for (var p = 0; p < 256; ++p) {
      var so = patternSizeTableOff + p * 2;
      if (so + 2 > blob.Length) break;
      patternSizes[p] = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(so, 2));
    }

    // Pattern data begins right after the fixed header (headerLen bytes from start).
    int off = headerLen;
    var emittedPatterns = 0;
    for (var p = 0; p < 256; ++p) {
      var size = patternSizes[p];
      if (size <= 0) continue;
      if (off + size > blob.Length) break;
      // First 2 bytes of each pattern block are the break-row + tempo header.
      var dataLen = size >= 2 ? size - 2 : size;
      var src = size >= 2 ? off + 2 : off;
      var data = new byte[dataLen];
      Buffer.BlockCopy(blob, src, data, 0, dataLen);
      entries.Add(($"patterns/pattern_{p:D2}.bin", "Pattern", data));
      off += size;
      ++emittedPatterns;
    }

    // After the patterns: sample on/off bitmap (8 bytes = 64 bits), then sample headers
    // (48 bytes each) for each set bit, then sample data inline after each header.
    var numSamples = 0;
    if (off + 8 <= blob.Length) {
      var bitmap = new bool[64];
      for (var i = 0; i < 64; ++i)
        bitmap[i] = (blob[off + (i >> 3)] & (1 << (i & 7))) != 0;
      off += 8;

      for (var i = 0; i < 64; ++i) {
        if (!bitmap[i]) continue;
        if (off + 48 > blob.Length) break;
        var name = ReadAsciiTrim(blob, off, 32);
        var byteLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 32, 4));
        off += 48;
        if (byteLen > 0 && off < blob.Length) {
          var take = Math.Min(byteLen, blob.Length - off);
          var data = new byte[take];
          Buffer.BlockCopy(blob, off, data, 0, take);
          var safe = string.IsNullOrWhiteSpace(name) ? "sample" : SanitizeFileName(name);
          entries.Add(($"samples/{(i + 1):D2}_{safe}.raw", "Sample", data));
          off += byteLen;
        }
        ++numSamples;
      }
    }

    var info = new StringBuilder();
    info.AppendLine($"title={title}");
    info.AppendLine($"format=FAR");
    info.AppendLine($"version=0x{version:X2}");
    info.AppendLine($"channels=16");
    info.AppendLine($"channels_enabled={channelsOn}");
    info.AppendLine($"num_patterns_header={numPatterns}");
    info.AppendLine($"num_patterns_emitted={emittedPatterns}");
    info.AppendLine($"num_samples={numSamples}");
    info.AppendLine($"restart_position={restart}");
    info.AppendLine($"sample_format=8/16-bit PCM (per-sample type byte bit0)");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static void AddPartial(List<(string, string, byte[])> entries) {
    var info = new StringBuilder();
    info.AppendLine("parse_status=partial");
    info.AppendLine("format=FAR");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
  }

  private static string ReadAsciiTrim(byte[] blob, int offset, int length) {
    var end = Math.Min(offset + length, blob.Length);
    var sb = new StringBuilder();
    for (var i = offset; i < end; ++i) {
      var b = blob[i];
      if (b == 0) break;
      if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
    }
    return sb.ToString().Trim();
  }

  private static string SanitizeFileName(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
      else sb.Append('_');
    }
    var s = sb.ToString().Trim('.');
    return s.Length == 0 ? "sample" : s;
  }
}
