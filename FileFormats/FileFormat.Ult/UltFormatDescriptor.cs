#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ult;

/// <summary>
/// Exposes an UltraTracker (ULT) module as an archive of <c>FULL.ult</c>,
/// <c>metadata.ini</c>, <c>patterns/track_NN.bin</c> (raw RLE-packed track data,
/// NO decode) and <c>samples/NN_{name}.raw</c> per non-empty sample (raw PCM).
/// The ULT layout was recovered through binary inspection of the documented
/// UltraTracker file format and the OpenMPT/libmodplug loaders.
/// </summary>
public sealed class UltFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Ult";
  public string DisplayName => "ULT (UltraTracker)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ult";
  public IReadOnlyList<string> Extensions => [".ult"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MAS_UTrack_V00"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "UltraTracker module; full file + RLE-packed track blocks + raw PCM samples.";

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
      ("FULL.ult", "Track", blob),
    };
    var magic = "MAS_UTrack_V00"u8;
    var validMagic = blob.Length >= 15 && blob.AsSpan(0, 14).SequenceEqual(magic);
    if (!validMagic) {
      AddPartial(entries);
      return entries;
    }

    // Version digit at offset 14: '1'..'4'.
    var version = blob[14] - '0';
    var title = ReadAsciiTrim(blob, 15, 32);

    var off = 47; // after 14-byte magic + version digit + 32-char title.

    // Song message: one byte = number of 32-char lines, followed by that many lines.
    if (off >= blob.Length) { AddPartial(entries, title, version); return entries; }
    var msgLines = blob[off];
    ++off;
    off += msgLines * 32;

    if (off >= blob.Length) { AddPartial(entries, title, version); return entries; }
    var numSamples = blob[off];
    ++off;

    // Sample headers: 64 bytes (version < 4) or 66 bytes (version >= 4).
    var sampleHeaderSize = version >= 4 ? 66 : 64;
    var samples = new List<(string Name, long Length, bool Is16)>();
    for (var s = 0; s < numSamples; ++s) {
      if (off + sampleHeaderSize > blob.Length) break;
      var name = ReadAsciiTrim(blob, off, 32);
      // dosName(12) at +32, loopStart u32 at +44, loopEnd u32 at +48,
      // sizeStart u32 at +52, sizeEnd u32 at +56, volume(1) at +60, flags(1) at +61.
      var sizeStart = ReadU32(blob, off + 52);
      var sizeEnd = ReadU32(blob, off + 56);
      var flags = off + 61 < blob.Length ? blob[off + 61] : (byte)0;
      var is16 = (flags & 0x04) != 0;
      var len = (long)(sizeEnd - sizeStart) * (is16 ? 2 : 1);
      samples.Add((name, len, is16));
      off += sampleHeaderSize;
    }

    // Order table: 256 bytes.
    off += 256;

    if (off + 2 > blob.Length) { AddPartial(entries, title, version); return entries; }
    var lastChannel = blob[off];     // numChannels - 1
    var lastPattern = blob[off + 1]; // numPatterns - 1
    off += 2;
    var numChannels = lastChannel + 1;
    var numPatterns = lastPattern + 1;

    // Pan positions: one byte per channel (version >= 3 only).
    if (version >= 3) off += numChannels;

    // Track data: numChannels * numPatterns tracks, each RLE-packed (64 rows decoded).
    // We surface raw track bytes per track without RLE decode; each track is a
    // self-delimiting RLE stream terminating once 64 rows are accounted for.
    var totalTracks = numChannels * numPatterns;
    for (var t = 0; t < totalTracks; ++t) {
      var start = off;
      var rows = 0;
      // Walk the RLE stream: 0xFC = repeat-count prefix (count, then 5-byte event);
      // otherwise a single 5-byte event.
      while (rows < 64 && off + 5 <= blob.Length) {
        if (blob[off] == 0xFC) {
          if (off + 6 > blob.Length) { off = blob.Length; break; }
          var count = blob[off + 1];
          rows += count;
          off += 6;
        } else {
          rows += 1;
          off += 5;
        }
      }
      if (off <= start) break;
      var len = off - start;
      var data = new byte[len];
      Buffer.BlockCopy(blob, start, data, 0, len);
      entries.Add(($"patterns/track_{(t + 1):D3}.bin", "Pattern", data));
      if (off >= blob.Length) break;
    }

    // Sample data follows the track data.
    for (var s = 0; s < samples.Count; ++s) {
      var (name, len, _) = samples[s];
      if (len <= 0) continue;
      if (off >= blob.Length) break;
      var take = (int)Math.Min(len, blob.Length - off);
      var data = new byte[take];
      Buffer.BlockCopy(blob, off, data, 0, take);
      var safe = string.IsNullOrWhiteSpace(name) ? "sample" : SanitizeFileName(name);
      entries.Add(($"samples/{(s + 1):D2}_{safe}.raw", "Sample", data));
      off += (int)len;
    }

    var info = new StringBuilder();
    info.AppendLine($"title={title}");
    info.AppendLine($"format=ULT");
    info.AppendLine($"version=V{version:D2}");
    info.AppendLine($"channels={numChannels}");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"num_samples={numSamples}");
    info.AppendLine($"sample_format=8/16-bit signed PCM (per-sample flags bit2)");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static uint ReadU32(byte[] blob, int off) =>
    off + 4 <= blob.Length
      ? (uint)(blob[off] | (blob[off + 1] << 8) | (blob[off + 2] << 16) | (blob[off + 3] << 24))
      : 0u;

  private static void AddPartial(List<(string, string, byte[])> entries, string? title = null, int version = 0) {
    var info = new StringBuilder();
    info.AppendLine("parse_status=partial");
    info.AppendLine("format=ULT");
    if (title != null) info.AppendLine($"title={title}");
    if (version > 0) info.AppendLine($"version=V{version:D2}");
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
