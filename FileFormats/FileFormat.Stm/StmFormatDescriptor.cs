#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Stm;

/// <summary>
/// Exposes a Scream Tracker 2 (<c>.stm</c>) module as a read-only pseudo-archive of
/// <c>FULL.stm</c> (byte-exact original), <c>metadata.ini</c>, the packed pattern
/// blocks as <c>patterns/pattern_NN.bin</c> (each 1024 bytes) and one playable mono
/// WAV per instrument that carries sample data under <c>samples/NN_{name}.wav</c>.
/// </summary>
/// <remarks>
/// Layout interpretation (all little-endian). Header (48 bytes): <c>char[20]
/// songname</c>, <c>char[8] tracker tag</c> (<c>"!Scream!"</c> or <c>"BMOD2STM"</c>),
/// <c>u8 0x1A</c>, <c>u8 fileType</c> (2 = module), <c>u8 verMajor</c>,
/// <c>u8 verMinor</c>, <c>u8 initTempo</c>, <c>u8 numPatterns</c>,
/// <c>u8 globalVolume</c>, <c>u8[13] reserved</c>. Then 31 × 32-byte instrument
/// headers: <c>char[12] filename</c>, <c>u8 0</c>, <c>u8 instDisk</c>,
/// <c>u16 reserved</c>, <c>u16 length</c>, <c>u16 loopStart</c>, <c>u16 loopEnd</c>,
/// <c>u8 volume</c>, <c>u8 reserved</c>, <c>u16 c2spd</c>, <c>u32 reserved</c>,
/// <c>u16 paragraphLength</c>. After the instrument table comes the 128-byte order
/// table, then <c>numPatterns</c> × 1024-byte packed patterns, then the sample data
/// in instrument order. STM samples are 8-bit SIGNED and are converted to unsigned-8
/// WAV. The per-instrument <c>c2spd</c> sets each WAV's sample rate (falling back to
/// 8363 Hz when zero).
/// </remarks>
public sealed class StmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Stm";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "STM (Scream Tracker 2)";
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
  public string DefaultExtension => ".stm";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".stm"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("!Scream!"u8.ToArray(), Offset: 20, Confidence: 0.90),
    new("BMOD2STM"u8.ToArray(), Offset: 20, Confidence: 0.90),
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
  public string Description => "Scream Tracker 2 module; full file + patterns + per-sample WAVs.";

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
  private const int InstrumentCount = 31;

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.stm", "Container", blob),
    };
    if (blob.Length < 48) return entries;
    var tag = ReadAscii(blob, 20, 8);
    if (tag != "!Scream!" && tag != "BMOD2STM") return entries;

    var songName = ReadAsciiTrim(blob, 0, 20);
    var fileType = blob[29];
    var verMajor = blob[30];
    var verMinor = blob[31];
    var initTempo = blob[32];
    var numPatterns = blob[33];
    var globalVolume = blob[34];

    var instrTableOff = 48;
    var samples = new List<(string Name, int Length, int Volume, int C2Spd)>();
    for (var i = 0; i < InstrumentCount; ++i) {
      var o = instrTableOff + i * 32;
      if (o + 32 > blob.Length) {
        samples.Add(("", 0, 0, 0));
        continue;
      }
      var fileName = ReadAsciiTrim(blob, o, 12);
      var length = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(o + 16, 2));
      var volume = blob[o + 24];
      var c2spd = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(o + 26, 2));
      samples.Add((fileName, length, volume, c2spd));
    }

    // Order table (128 bytes) follows the instrument table.
    var orderOff = instrTableOff + InstrumentCount * 32;
    var patternsOff = orderOff + 128;

    for (var p = 0; p < numPatterns; ++p) {
      var o = patternsOff + p * 1024;
      if (o + 1024 > blob.Length) break;
      var data = new byte[1024];
      Buffer.BlockCopy(blob, o, data, 0, 1024);
      entries.Add(new($"patterns/pattern_{p:D2}.bin", "Pattern", data));
    }

    var sampleDataOff = patternsOff + numPatterns * 1024;
    var off = sampleDataOff;
    var samplesWithData = 0;
    for (var s = 0; s < samples.Count; ++s) {
      var (name, length, _, c2spd) = samples[s];
      if (length <= 0) continue;
      if (off >= blob.Length) break;
      var take = Math.Min(length, blob.Length - off);
      if (take <= 0) break;
      // 8-bit signed → unsigned-8 WAV.
      var u = new byte[take];
      for (var i = 0; i < take; ++i) u[i] = (byte)(unchecked((sbyte)blob[off + i]) + 128);
      off += length;
      var rate = c2spd > 0 ? c2spd : DefaultRate;
      var wav = PcmCodec.ToWavBlob(u, channels: 1, rate, bitsPerSample: 8);
      var label = string.IsNullOrWhiteSpace(name) ? "sample" : SanitizeFileName(name);
      entries.Add(new($"samples/{(s + 1):D2}_{label}.wav", "Sample", wav));
      ++samplesWithData;
    }

    var info = new StringBuilder();
    info.AppendLine($"format=STM");
    info.AppendLine($"tracker_tag={tag}");
    info.AppendLine($"song_name={songName}");
    info.AppendLine($"version={verMajor}.{verMinor}");
    info.AppendLine($"file_type={fileType}");
    info.AppendLine($"init_tempo={initTempo}");
    info.AppendLine($"global_volume={globalVolume}");
    info.AppendLine($"num_patterns={numPatterns}");
    info.AppendLine($"num_instruments={InstrumentCount}");
    info.AppendLine($"samples_with_data={samplesWithData}");
    info.AppendLine($"sample_8bit_encoding=signed");
    info.AppendLine($"note=Per-sample c2spd sets WAV rate; 8363 Hz fallback when zero.");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
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
