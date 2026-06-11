#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Mtm;

/// <summary>
/// Exposes a MultiTracker (MTM) module as an archive of <c>FULL.mtm</c>,
/// <c>metadata.ini</c>, <c>patterns/pattern_NN.bin</c> (one raw track block of
/// 192 packed bytes — 64 rows x 3 bytes, NO decode) and
/// <c>samples/NN_{name}.raw</c> per non-empty sample (raw PCM). The MTM layout
/// was recovered through binary inspection of the documented MultiTracker file
/// format and the OpenMPT/libmodplug loaders.
/// </summary>
public sealed class MtmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Mtm";
  public string DisplayName => "MTM (MultiTracker)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mtm";
  public IReadOnlyList<string> Extensions => [".mtm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("MTM"u8.ToArray(), Offset: 0, Confidence: 0.92),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "MultiTracker module; full file + per-track pattern blocks + raw 8-bit PCM samples.";

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
      ("FULL.mtm", "Track", blob),
    };
    // Header is 66 bytes: "MTM" + version + 20-char title + counts.
    if (blob.Length < 66 || blob[0] != (byte)'M' || blob[1] != (byte)'T' || blob[2] != (byte)'M') {
      AddPartial(entries, "mtm");
      return entries;
    }

    var version = blob[3];
    var title = ReadAsciiTrim(blob, 4, 20);
    var numTracks = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(24, 2));
    var lastPattern = blob[26];
    var lastOrder = blob[27];
    var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(28, 2));
    var numSamples = blob[30];
    // blob[31] = attribute, blob[32] = beats/track.
    var beatsPerTrack = blob[32];
    var numChannels = blob[33];

    var numPatterns = lastPattern + 1;

    // Sample headers: 37 bytes each, starting at offset 66.
    var off = 66;
    var samples = new List<(string Name, int Length, bool Is16)>();
    for (var s = 0; s < numSamples; ++s) {
      if (off + 37 > blob.Length) break;
      var name = ReadAsciiTrim(blob, off, 22);
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 22, 4));
      // attribute byte at +36; bit0 = 16-bit.
      var attr = blob[off + 36];
      var is16 = (attr & 0x01) != 0;
      samples.Add((name, len, is16));
      off += 37;
    }

    // Order table: 128 bytes.
    off += 128;

    // Track data: numTracks blocks of 192 bytes each (64 rows x 3 bytes).
    const int trackBytes = 192;
    for (var t = 0; t < numTracks; ++t) {
      if (off + trackBytes > blob.Length) break;
      var data = new byte[trackBytes];
      Buffer.BlockCopy(blob, off, data, 0, trackBytes);
      entries.Add(($"patterns/track_{(t + 1):D2}.bin", "Pattern", data));
      off += trackBytes;
    }

    // Pattern -> track sequence tables: numPatterns * 32 u16 entries.
    off += numPatterns * 32 * 2;

    // Comment block.
    off += commentLen;

    // Sample data follows.
    for (var s = 0; s < samples.Count; ++s) {
      var (name, len, _) = samples[s];
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
    info.AppendLine($"title={title}");
    info.AppendLine($"format=MTM");
    info.AppendLine($"version=0x{version:X2}");
    info.AppendLine($"channels={numChannels}");
    info.AppendLine($"tracks={numTracks}");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"num_orders={lastOrder + 1}");
    info.AppendLine($"num_samples={numSamples}");
    info.AppendLine($"beats_per_track={beatsPerTrack}");
    info.AppendLine($"sample_format=8-bit unsigned PCM (16-bit if sample attr bit0)");
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static void AddPartial(List<(string, string, byte[])> entries, string ext) {
    var info = new StringBuilder();
    info.AppendLine("parse_status=partial");
    info.AppendLine($"format={ext.ToUpperInvariant()}");
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
