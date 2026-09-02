#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Mtm;

/// <summary>
/// Exposes a MultiTracker (<c>.mtm</c>) module as a read-only pseudo-archive of
/// <c>FULL.mtm</c> (byte-exact original), <c>metadata.ini</c>, the packed track
/// blocks as <c>patterns/track_NN.bin</c> (each a 64-row × 3-byte track) and one
/// playable mono WAV per sample under <c>samples/NN_{name}.wav</c>.
/// </summary>
/// <remarks>
/// Layout interpretation (all little-endian): 66-byte header — <c>"MTM"</c>,
/// <c>u8 version</c>, <c>char[20] songName</c>, <c>u16 numTracks</c>,
/// <c>u8 lastPattern</c>, <c>u8 lastOrder</c>, <c>u16 commentLen</c>,
/// <c>u8 numSamples</c>, <c>u8 attribute</c>, <c>u8 beatsPerTrack</c>,
/// <c>u8 numChannels</c>, <c>u8 panPositions[32]</c>. Then <c>numSamples</c> ×
/// 37-byte sample headers (name[22], len u32, loopStart u32, loopEnd u32,
/// finetune u8, volume u8, attribute u8 with bit0 = 16-bit). After that come the
/// 128-byte order table, the track data (<c>numTracks</c> × 192 bytes, each a
/// 64-row × 3-byte track — track 0 is implicit silence and not stored), the
/// pattern grid (<c>(lastPattern+1)</c> × 32 × <c>u16</c> track references), the
/// comment block (<c>commentLen</c> bytes) and finally the sample data. MTM
/// 8-bit samples are stored UNSIGNED (centred on 0x80); they are surfaced as
/// unsigned-8 WAV verbatim. 16-bit samples are signed little-endian. MTM stores
/// no per-sample replay rate, so a fixed 8363 Hz (the ProTracker/MOD middle-C
/// reference) is used for the WAV and recorded in <c>metadata.ini</c>.
/// </remarks>
public sealed class MtmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Mtm";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MTM (MultiTracker)";
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
public string DefaultExtension => ".mtm";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".mtm"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'M', (byte)'T', (byte)'M', 0x10], Confidence: 0.90),
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
public string Description => "MultiTracker module; full file + track blocks + per-sample WAVs.";

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

  private const int SampleRate = 8363;

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.mtm", "Container", blob),
    };
    if (blob.Length < 66 || blob[0] != 'M' || blob[1] != 'T' || blob[2] != 'M')
      return entries;

    var version = blob[3];
    var songName = ReadAsciiTrim(blob, 4, 20);
    var numTracks = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(24, 2));
    var lastPattern = blob[26];
    var lastOrder = blob[27];
    var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(28, 2));
    var numSamples = blob[30];
    var attribute = blob[31];
    var beatsPerTrack = blob[32];
    var numChannels = blob[33];
    // panPositions[32] at 34..65; header end = 66.

    var samples = new List<(string Name, long Length, bool Is16Bit, int Volume, int Finetune)>();
    var off = 66;
    for (var s = 0; s < numSamples; ++s) {
      if (off + 37 > blob.Length) break;
      var name = ReadAsciiTrim(blob, off, 22);
      var length = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 22, 4));
      var finetune = blob[off + 34];
      var volume = blob[off + 35];
      var sampleAttr = blob[off + 36];
      var is16Bit = (sampleAttr & 0x01) != 0;
      samples.Add((name, length, is16Bit, volume, finetune));
      off += 37;
    }

    // 128-byte order table.
    var orderOff = off;
    off += 128;

    // Track data: numTracks × 192 bytes (track index 0 is implicit, not stored).
    var trackBytes = 192;
    for (var t = 0; t < numTracks; ++t) {
      if (off + trackBytes > blob.Length) break;
      var data = new byte[trackBytes];
      Buffer.BlockCopy(blob, off, data, 0, trackBytes);
      entries.Add(new($"patterns/track_{(t + 1):D2}.bin", "Pattern", data));
      off += trackBytes;
    }
    off = Math.Min(off, blob.Length);

    // Pattern grid: (lastPattern+1) × 32 × u16 = (lastPattern+1) × 64 bytes.
    var patternGridBytes = (lastPattern + 1) * 32 * 2;
    off += patternGridBytes;

    // Comment block.
    off += commentLen;

    // Sample data follows. 8-bit unsigned / 16-bit signed LE.
    var samplesWithData = 0;
    for (var s = 0; s < samples.Count; ++s) {
      var (name, length, is16Bit, _, _) = samples[s];
      if (length <= 0) continue;
      var bits = is16Bit ? 16 : 8;
      var byteLen = is16Bit ? length * 2 : length;
      if (off >= blob.Length) break;
      var take = (int)Math.Min(byteLen, blob.Length - off);
      if (take <= 0) break;
      var pcm = new byte[take];
      Buffer.BlockCopy(blob, off, pcm, 0, take);
      off += (int)byteLen;
      var wav = PcmCodec.ToWavBlob(pcm, channels: 1, SampleRate, bitsPerSample: bits);
      var label = string.IsNullOrWhiteSpace(name) ? "sample" : SanitizeFileName(name);
      entries.Add(new($"samples/{(s + 1):D2}_{label}.wav", "Sample", wav));
      ++samplesWithData;
    }

    var info = new StringBuilder();
    info.AppendLine($"format=MTM");
    info.AppendLine($"version=0x{version:X2}");
    info.AppendLine($"song_name={songName}");
    info.AppendLine($"num_tracks={numTracks}");
    info.AppendLine($"last_pattern={lastPattern}");
    info.AppendLine($"last_order={lastOrder}");
    info.AppendLine($"num_channels={numChannels}");
    info.AppendLine($"beats_per_track={beatsPerTrack}");
    info.AppendLine($"attribute={attribute}");
    info.AppendLine($"comment_length={commentLen}");
    info.AppendLine($"num_samples={numSamples}");
    info.AppendLine($"samples_with_data={samplesWithData}");
    info.AppendLine($"order_table_offset={orderOff}");
    info.AppendLine($"sample_rate={SampleRate}");
    info.AppendLine($"sample_8bit_encoding=unsigned");
    info.AppendLine($"note=MTM stores no per-sample replay rate; WAVs use 8363 Hz.");
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
