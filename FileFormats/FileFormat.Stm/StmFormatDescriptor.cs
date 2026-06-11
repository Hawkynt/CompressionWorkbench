#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Stm;

/// <summary>
/// Exposes a Scream Tracker 2 (STM) module as an archive of <c>FULL.stm</c>,
/// <c>metadata.ini</c>, <c>patterns/pattern_NN.bin</c> (raw 1024-byte pattern
/// blocks — 64 rows x 4 channels x 4 bytes, NO decode) and
/// <c>samples/NN_{name}.raw</c> per non-empty sample (raw signed 8-bit PCM).
/// Distinguished from S3M by the <c>!Scream!</c> signature at offset 20 (S3M
/// carries <c>SCRM</c> at offset 44). The STM layout was recovered through binary
/// inspection of the documented Scream Tracker 2 file format.
/// </summary>
public sealed class StmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Stm";
  public string DisplayName => "STM (Scream Tracker 2)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".stm";
  public IReadOnlyList<string> Extensions => [".stm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("!Scream!"u8.ToArray(), Offset: 20, Confidence: 0.93),
    new("BMOD2STM"u8.ToArray(), Offset: 20, Confidence: 0.93),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Scream Tracker 2 module; full file + 4-channel pattern blocks + raw signed 8-bit PCM samples.";

  private const int PatternBytes = 64 * 4 * 4; // 1024 bytes per pattern

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
      ("FULL.stm", "Track", blob),
    };
    var tracker = blob.Length >= 28 ? Encoding.ASCII.GetString(blob, 20, 8) : "";
    var validMagic = tracker is "!Scream!" or "BMOD2STM" or "WUZAMOD!";
    // Header is 48 bytes, then 31 sample headers of 32 bytes = 1040, then order table 128.
    if (blob.Length < 48 + 31 * 32 + 128 || !validMagic) {
      AddPartial(entries);
      return entries;
    }

    var title = ReadAsciiTrim(blob, 0, 20);
    var verMajor = blob[30];
    var verMinor = blob[31];
    var tempo = blob[32];
    var numPatterns = blob[33];
    var globalVol = blob[34];

    // 31 sample headers at offset 48, 32 bytes each.
    const int sampleHdrOff = 48;
    var samples = new List<(string Name, int Length)>();
    for (var s = 0; s < 31; ++s) {
      var off = sampleHdrOff + s * 32;
      var name = ReadAsciiTrim(blob, off, 12);
      // reserved(1) at +12, instDisk(1) at +13, reserved(2) at +14, length u16 at +16.
      var len = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 16, 2));
      samples.Add((name, len));
    }

    // Order table: 128 bytes at offset 48 + 31*32 = 1040.
    // Patterns begin after the 128-byte order table.
    var patternsStart = sampleHdrOff + 31 * 32 + 128;

    var off2 = patternsStart;
    var emitted = 0;
    for (var p = 0; p < numPatterns; ++p) {
      if (off2 + PatternBytes > blob.Length) break;
      var data = new byte[PatternBytes];
      Buffer.BlockCopy(blob, off2, data, 0, PatternBytes);
      entries.Add(($"patterns/pattern_{p:D2}.bin", "Pattern", data));
      off2 += PatternBytes;
      ++emitted;
    }

    // Sample data follows the patterns.
    for (var s = 0; s < samples.Count; ++s) {
      var (name, len) = samples[s];
      if (len <= 0) continue;
      if (off2 >= blob.Length) break;
      var take = Math.Min(len, blob.Length - off2);
      var data = new byte[take];
      Buffer.BlockCopy(blob, off2, data, 0, take);
      var safe = string.IsNullOrWhiteSpace(name) ? "sample" : SanitizeFileName(name);
      entries.Add(($"samples/{(s + 1):D2}_{safe}.raw", "Sample", data));
      off2 += len;
    }

    var info = new StringBuilder();
    info.AppendLine($"title={title}");
    info.AppendLine($"format=STM");
    info.AppendLine($"tracker={tracker}");
    info.AppendLine($"version={verMajor}.{verMinor}");
    info.AppendLine($"channels=4");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"num_patterns_emitted={emitted}");
    info.AppendLine($"num_samples={samples.Count(s => s.Length > 0)}");
    info.AppendLine($"tempo={tempo}");
    info.AppendLine($"global_volume={globalVol}");
    info.AppendLine($"sample_format=8-bit signed PCM");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static void AddPartial(List<(string, string, byte[])> entries) {
    var info = new StringBuilder();
    info.AppendLine("parse_status=partial");
    info.AppendLine("format=STM");
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
