#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mod;

/// <summary>
/// Exposes a ProTracker / SoundTracker / NoiseTracker MOD file as an archive of
/// <c>FULL.mod</c>, a <c>metadata.ini</c> summary, <c>patterns/pattern_NN.bin</c>
/// (raw 1024-byte 4-channel pattern blocks, or N×channels×64×4 for multi-channel
/// variants) and <c>samples/NN_{name}.raw</c> per non-empty sample (raw signed
/// 8-bit PCM).
/// </summary>
public sealed class ModFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Mod";
  public string DisplayName => "MOD (ProTracker / SoundTracker)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mod";
  public IReadOnlyList<string> Extensions => [".mod"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("M.K."u8.ToArray(), Offset: 1080, Confidence: 0.95),
    new("M!K!"u8.ToArray(), Offset: 1080, Confidence: 0.95),
    new("4CHN"u8.ToArray(), Offset: 1080, Confidence: 0.95),
    new("6CHN"u8.ToArray(), Offset: 1080, Confidence: 0.95),
    new("8CHN"u8.ToArray(), Offset: 1080, Confidence: 0.95),
    new("FLT4"u8.ToArray(), Offset: 1080, Confidence: 0.95),
    new("FLT8"u8.ToArray(), Offset: 1080, Confidence: 0.95),
    new("CD81"u8.ToArray(), Offset: 1080, Confidence: 0.95),
    new("OKTA"u8.ToArray(), Offset: 1080, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Amiga MOD tracker module; full file + patterns + raw 8-bit PCM samples.";

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
    var blob = ms.ToArray();
    return Parse(blob);
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> Parse(byte[] blob) {
    var entries = new List<(string, string, byte[])> {
      ("FULL.mod", "Track", blob),
    };
    if (blob.Length < 1084)
      return entries;

    var title = ReadAsciiTrim(blob, 0, 20);

    // 31 samples, 30 bytes each, starting at offset 20.
    var samples = new List<(string Name, int Length)>();
    for (var s = 0; s < 31; ++s) {
      var off = 20 + s * 30;
      var name = ReadAsciiTrim(blob, off, 22);
      var words = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(off + 22, 2));
      var len = words * 2;
      samples.Add((name, len));
    }

    var songLen = blob[950];
    var sig = Encoding.ASCII.GetString(blob, 1080, 4);
    var channels = ChannelsForSignature(sig);

    // Determine number of unique patterns from the order table (bytes 952..952+128).
    var numPatterns = 0;
    for (var i = 0; i < 128; ++i) {
      var p = blob[952 + i];
      if (p > numPatterns) numPatterns = p;
    }
    numPatterns += 1; // highest pattern id → count

    var patternBytesEach = 64 * channels * 4;
    var patternsStart = 1084;
    var patternsTotal = numPatterns * patternBytesEach;
    if (patternsStart + patternsTotal > blob.Length) {
      // Truncate numPatterns to what actually fits.
      numPatterns = Math.Max(0, (blob.Length - patternsStart) / patternBytesEach);
      patternsTotal = numPatterns * patternBytesEach;
    }

    for (var p = 0; p < numPatterns; ++p) {
      var off = patternsStart + p * patternBytesEach;
      var data = new byte[patternBytesEach];
      Buffer.BlockCopy(blob, off, data, 0, patternBytesEach);
      entries.Add(($"patterns/pattern_{p:D2}.bin", "Pattern", data));
    }

    // Sample data follows.
    var sampleOff = patternsStart + patternsTotal;
    for (var s = 0; s < samples.Count; ++s) {
      var (name, len) = samples[s];
      if (len <= 0) continue;
      if (sampleOff >= blob.Length) break;
      var take = Math.Min(len, blob.Length - sampleOff);
      var data = new byte[take];
      Buffer.BlockCopy(blob, sampleOff, data, 0, take);
      var safeName = string.IsNullOrWhiteSpace(name) ? "sample" : SanitizeFileName(name);
      entries.Add(($"samples/{(s + 1):D2}_{safeName}.raw", "Sample", data));
      sampleOff += len;
    }

    // Synthetic metadata.
    var info = new StringBuilder();
    info.AppendLine($"title={title}");
    info.AppendLine($"signature={sig}");
    info.AppendLine($"channels={channels}");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"num_samples={samples.Count(s => s.Length > 0)}");
    info.AppendLine($"song_length={songLen}");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static int ChannelsForSignature(string sig) => sig switch {
    "M.K." or "M!K!" or "FLT4" or "4CHN" => 4,
    "6CHN" => 6,
    "8CHN" or "FLT8" or "CD81" or "OKTA" => 8,
    _ => 4,
  };

  private static string ReadAsciiTrim(byte[] blob, int offset, int length) {
    var end = offset + length;
    if (end > blob.Length) end = blob.Length;
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
