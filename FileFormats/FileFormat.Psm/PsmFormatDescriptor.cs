#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Psm;

/// <summary>
/// Exposes an Epic MegaGames MASI (<c>.psm</c>, new chunked format) module as a
/// read-only pseudo-archive of <c>FULL.psm</c> (byte-exact original),
/// <c>metadata.ini</c>, the title text (<c>title.txt</c>), every pattern body
/// (<c>PBOD</c>) as <c>patterns/pattern_NN.bin</c> and one playable mono WAV per
/// sample (<c>DSMP</c>) under <c>samples/NN_{name}.wav</c>.
/// </summary>
/// <remarks>
/// Layout interpretation (all little-endian). File header: <c>"PSM "</c>,
/// <c>u32 fileSize</c>, <c>"FILE"</c>. The remainder is a sequence of chunks, each
/// a 4-character id, a <c>u32</c> body length and the body. Recognised chunks:
/// <c>TITL</c> (song title, surfaced as <c>title.txt</c>), <c>SDFT</c> (song
/// descriptor, e.g. <c>"MAINSONG"</c>), <c>SONG</c> (song sub-data, not surfaced),
/// <c>PBOD</c> (pattern body, surfaced verbatim) and <c>DSMP</c> (sample). A
/// <c>DSMP</c> body is read as: <c>u8 flags</c>, <c>char[8] fileName</c>,
/// <c>char[4] sampleId</c>, <c>char[33] name</c>, <c>u32 length</c> (@51),
/// <c>u32 loopStart</c> (@55), <c>u32 loopEnd</c> (@59), <c>u16 c2freq</c> (@70),
/// with sample data beginning at offset 96 of the body. MASI samples are
/// DELTA-encoded signed 8-bit (running sum); the decoded signed value is converted
/// to unsigned-8 for the WAV. The per-sample <c>c2freq</c> sets the WAV sample rate
/// (8363 Hz fallback when zero).
/// </remarks>
public sealed class PsmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Psm";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PSM (Epic MegaGames MASI)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Audio;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".psm";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".psm"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PSM "u8.ToArray(), Confidence: 0.90),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Classic;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Epic MegaGames MASI module; full file + patterns + delta-decoded per-sample WAVs.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

    /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return Parse(ms.ToArray());
  }

  private const int DefaultRate = 8363;

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.psm", "Container", blob),
    };
    var info = new StringBuilder();
    info.AppendLine("format=PSM");

    if (blob.Length < 12 || blob[0] != 'P' || blob[1] != 'S' || blob[2] != 'M' || blob[3] != ' '
        || blob[8] != 'F' || blob[9] != 'I' || blob[10] != 'L' || blob[11] != 'E') {
      info.AppendLine("parsed=false");
      entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
      return entries;
    }

    var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4, 4));
    info.AppendLine($"declared_file_size={fileSize}");

    string? title = null;
    string? sdft = null;
    var patternCount = 0;
    var samplesWithData = 0;
    var dsmpCount = 0;

    var off = 12;
    while (off + 8 <= blob.Length) {
      var id = ReadAscii(blob, off, 4);
      var len = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 4, 4));
      var bodyOff = off + 8;
      if (bodyOff + len > (uint)blob.Length) break;
      var bodyLen = (int)len;

      switch (id) {
        case "TITL": {
          title = ReadAsciiTrim(blob, bodyOff, bodyLen);
          entries.Add(new("title.txt", "Tag", Slice(blob, bodyOff, bodyLen)));
          break;
        }
        case "SDFT":
          sdft = ReadAsciiTrim(blob, bodyOff, bodyLen);
          break;
        case "PBOD": {
          entries.Add(new($"patterns/pattern_{patternCount:D2}.bin", "Pattern", Slice(blob, bodyOff, bodyLen)));
          ++patternCount;
          break;
        }
        case "DSMP": {
          ++dsmpCount;
          if (ParseDsmp(blob, bodyOff, bodyLen, dsmpCount, out var entry)) {
            entries.Add(entry!);
            ++samplesWithData;
          }
          break;
        }
      }

      off = bodyOff + bodyLen;
    }

    if (title != null) info.AppendLine($"title={title}");
    if (sdft != null) info.AppendLine($"song_descriptor={sdft}");
    info.AppendLine($"num_patterns={patternCount}");
    info.AppendLine($"num_samples={dsmpCount}");
    info.AppendLine($"samples_with_data={samplesWithData}");
    info.AppendLine($"sample_encoding=delta_signed");
    info.AppendLine($"note=DSMP samples are delta-decoded 8-bit; c2freq sets WAV rate.");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static bool ParseDsmp(byte[] blob, int bodyOff, int bodyLen, int index, out AudioPseudoArchive.Entry? entry) {
    entry = null;
    if (bodyLen < 96) return false;
    var name = ReadAsciiTrim(blob, bodyOff + 13, 33);
    var length = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(bodyOff + 51, 4));
    var c2freq = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(bodyOff + 70, 2));
    if (length == 0) return false;

    var dataOff = bodyOff + 96;
    var avail = bodyOff + bodyLen - dataOff;
    if (avail <= 0) return false;
    var take = (int)Math.Min(length, (uint)avail);
    if (take <= 0) return false;

    // 8-bit signed delta → running sum → unsigned-8.
    var pcm = new byte[take];
    sbyte acc = 0;
    for (var i = 0; i < take; ++i) {
      acc = unchecked((sbyte)(acc + unchecked((sbyte)blob[dataOff + i])));
      pcm[i] = (byte)(acc + 128);
    }
    var rate = c2freq > 0 ? c2freq : DefaultRate;
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, rate, bitsPerSample: 8);
    var label = string.IsNullOrWhiteSpace(name) ? "sample" : SanitizeFileName(name);
    entry = new($"samples/{index:D2}_{label}.wav", "Sample", wav);
    return true;
  }

  private static byte[] Slice(byte[] blob, int offset, int length) {
    var data = new byte[length];
    Buffer.BlockCopy(blob, offset, data, 0, length);
    return data;
  }

  private static string ReadAscii(byte[] blob, int offset, int length) {
    var end = Math.Min(offset + length, blob.Length);
    var sb = new StringBuilder();
    for (var i = offset; i < end; ++i) sb.Append((char)blob[i]);
    return sb.ToString();
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
    foreach (var c in name)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    var s = sb.ToString().Trim('.');
    return s.Length == 0 ? "sample" : s;
  }
}
