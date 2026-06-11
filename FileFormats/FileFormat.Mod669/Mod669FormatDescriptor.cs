#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mod669;

/// <summary>
/// Exposes a Composer 669 / UNIS 669 module as an archive of <c>FULL.669</c>,
/// <c>metadata.ini</c>, <c>patterns/pattern_NN.bin</c> (raw 1536-byte pattern
/// blocks — 64 rows x 8 channels x 3 bytes, NO decode) and
/// <c>samples/NN_{name}.raw</c> per non-empty sample (raw signed 8-bit PCM).
/// The 669 layout was recovered through binary inspection of the documented
/// Composer 669 file format and the OpenMPT/XMP loaders.
/// </summary>
public sealed class Mod669FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Mod669";
  public string DisplayName => "669 (Composer 669)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".669";
  public IReadOnlyList<string> Extensions => [".669"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("if"u8.ToArray(), Offset: 0, Confidence: 0.70),
    new("JN"u8.ToArray(), Offset: 0, Confidence: 0.70),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Composer 669 module; full file + 8-channel pattern blocks + raw signed 8-bit PCM samples.";

  private const int PatternBytes = 64 * 8 * 3; // 1536 bytes per pattern

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
      ("FULL.669", "Track", blob),
    };
    var validMagic = blob.Length >= 2 &&
      ((blob[0] == 'i' && blob[1] == 'f') || (blob[0] == 'J' && blob[1] == 'N'));
    // Header is 0x1F1 (497) bytes.
    if (blob.Length < 0x1F1 || !validMagic) {
      AddPartial(entries);
      return entries;
    }

    var extended = blob[0] == 'J';
    var message = ReadAsciiTrim(blob, 2, 108);
    var numSamples = blob[0x6E];   // 110
    var numPatterns = blob[0x6F];  // 111
    var restart = blob[0x70];      // 112

    // Order table 128 bytes at 0x71, tempos 128 at 0xF1, breaks 128 at 0x171.
    // Sample headers begin at 0x1F1, 25 bytes each.
    var off = 0x1F1;
    var samples = new List<(string Name, int Length)>();
    for (var s = 0; s < numSamples; ++s) {
      if (off + 25 > blob.Length) break;
      var name = ReadAsciiTrim(blob, off, 13);
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 13, 4));
      samples.Add((name, len));
      off += 25;
    }

    // Pattern bodies: numPatterns * 1536 bytes.
    for (var p = 0; p < numPatterns; ++p) {
      if (off + PatternBytes > blob.Length) break;
      var data = new byte[PatternBytes];
      Buffer.BlockCopy(blob, off, data, 0, PatternBytes);
      entries.Add(($"patterns/pattern_{p:D2}.bin", "Pattern", data));
      off += PatternBytes;
    }

    // Sample data follows.
    for (var s = 0; s < samples.Count; ++s) {
      var (name, len) = samples[s];
      if (len <= 0) continue;
      if (off >= blob.Length) break;
      var take = Math.Min(len, blob.Length - off);
      var data = new byte[take];
      Buffer.BlockCopy(blob, off, data, 0, take);
      var safe = string.IsNullOrWhiteSpace(name) ? "sample" : SanitizeFileName(name);
      entries.Add(($"samples/{(s + 1):D2}_{safe}.raw", "Sample", data));
      off += len;
    }

    var info = new StringBuilder();
    info.AppendLine($"format=669");
    info.AppendLine($"variant={(extended ? "extended (JN)" : "standard (if)")}");
    info.AppendLine($"message={message}");
    info.AppendLine($"channels=8");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"num_samples={numSamples}");
    info.AppendLine($"restart_position={restart}");
    info.AppendLine($"sample_format=8-bit unsigned PCM");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static void AddPartial(List<(string, string, byte[])> entries) {
    var info = new StringBuilder();
    info.AppendLine("parse_status=partial");
    info.AppendLine("format=669");
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
