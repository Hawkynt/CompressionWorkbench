#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Amf;

/// <summary>
/// Exposes a DSMI Advanced Module Format (<c>.amf</c>) file as a read-only
/// pseudo-archive of <c>FULL.amf</c> (byte-exact original), <c>metadata.ini</c> and
/// one playable mono WAV per sample under <c>samples/NN_{name}.wav</c>.
/// </summary>
/// <remarks>
/// Layout interpretation (all little-endian) for the v10–v14 layout. Header:
/// <c>"AMF"</c>, <c>u8 version</c>, <c>char[32] title</c>, <c>u8 numSamples</c>,
/// <c>u8 numOrders</c>, <c>u16 numTracks</c>, <c>u8 numChannels</c>. After the
/// header come the channel-remap table (<c>numChannels</c> bytes), the per-order
/// pattern-length table (<c>numOrders</c> × <c>u16</c>) and the per-order
/// channel-track index table (<c>numOrders</c> × <c>numChannels</c> × <c>u16</c>).
/// Then <c>numSamples</c> sample headers: <c>u8 type</c>, <c>char[32] name</c>,
/// <c>char[13] dosName</c>, <c>u32 index</c>, <c>u32 length</c>, <c>u16 c2spd</c>,
/// <c>u8 volume</c>, <c>u32 loopStart</c>, <c>u32 loopEnd</c>. The sample data for
/// all instruments follows the header table in instrument order. DSMI samples are
/// 8-bit UNSIGNED and are surfaced as unsigned-8 WAV verbatim; the per-sample
/// <c>c2spd</c> sets each WAV's sample rate (8363 Hz fallback when zero).
/// Simplification: the track table, order tables and channel map are spanned by
/// the documented sizes only — they are not individually surfaced. Older
/// (&lt; v10) layouts fall back to <c>FULL.amf</c> + metadata only.
/// </remarks>
public sealed class AmfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Amf";
  public string DisplayName => "AMF (DSMI Advanced Module Format)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".amf";
  public IReadOnlyList<string> Extensions => [".amf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("AMF"u8.ToArray(), Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "DSMI AMF module; full file + per-sample WAVs.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

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
      new("FULL.amf", "Container", blob),
    };
    var info = new StringBuilder();
    info.AppendLine("format=AMF");

    if (blob.Length < 40 || blob[0] != 'A' || blob[1] != 'M' || blob[2] != 'F') {
      info.AppendLine("parsed=false");
      entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
      return entries;
    }

    var version = blob[3];
    var title = ReadAsciiTrim(blob, 4, 32);
    info.AppendLine($"version={version}");
    info.AppendLine($"title={title}");

    if (version < 10) {
      // Older layouts are not laid out the same; surface FULL + metadata only.
      info.AppendLine("parsed=false");
      info.AppendLine("note=AMF versions below 10 are surfaced as FULL.amf only.");
      entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
      return entries;
    }

    var numSamples = blob[36];
    var numOrders = blob[37];
    var numTracks = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(38, 2));
    var numChannels = blob.Length > 40 ? blob[40] : 0;

    var off = 41;
    off += numChannels;                       // channel remap table
    off += numOrders * 2;                      // per-order pattern lengths
    off += numOrders * numChannels * 2;        // per-order channel-track indices

    var samples = new List<(string Name, long Length, int C2Spd)>();
    for (var s = 0; s < numSamples; ++s) {
      if (off + 60 > blob.Length) break;
      // type(1) name(32) dosName(13) index(4) length(4) c2spd(2) volume(1) loopStart(4) loopEnd(4) = 65
      var name = ReadAsciiTrim(blob, off + 1, 32);
      var length = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 50, 4));
      var c2spd = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 54, 2));
      samples.Add((name, length, c2spd));
      off += 65;
    }

    var samplesWithData = 0;
    for (var s = 0; s < samples.Count; ++s) {
      var (name, length, c2spd) = samples[s];
      if (length <= 0) continue;
      if (off >= blob.Length) break;
      var take = (int)Math.Min(length, blob.Length - off);
      if (take <= 0) break;
      // 8-bit unsigned verbatim.
      var pcm = new byte[take];
      Buffer.BlockCopy(blob, off, pcm, 0, take);
      off += (int)length;
      var rate = c2spd > 0 ? c2spd : DefaultRate;
      var wav = PcmCodec.ToWavBlob(pcm, channels: 1, rate, bitsPerSample: 8);
      var label = string.IsNullOrWhiteSpace(name) ? "sample" : SanitizeFileName(name);
      entries.Add(new($"samples/{(s + 1):D2}_{label}.wav", "Sample", wav));
      ++samplesWithData;
    }

    info.AppendLine($"num_samples={numSamples}");
    info.AppendLine($"num_orders={numOrders}");
    info.AppendLine($"num_tracks={numTracks}");
    info.AppendLine($"num_channels={numChannels}");
    info.AppendLine($"samples_with_data={samplesWithData}");
    info.AppendLine($"sample_8bit_encoding=unsigned");
    info.AppendLine($"note=Per-sample c2spd sets WAV rate; 8363 Hz fallback when zero.");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
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
