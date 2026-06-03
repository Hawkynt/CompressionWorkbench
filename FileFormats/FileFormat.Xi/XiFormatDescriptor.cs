#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Xi;

/// <summary>
/// Exposes a FastTracker II instrument (<c>.xi</c>) as a pseudo-archive: <c>FULL.xi</c>
/// (the byte-exact instrument) plus one playable WAV per sample
/// (<c>samples/NN_&lt;name&gt;.wav</c>) and a <c>metadata.ini</c> summary. Sample data is
/// stored FT2 delta-encoded (the same running-sum scheme XM uses); it is decoded here
/// and surfaced as canonical PCM (8-bit → WAV unsigned 8-bit; 16-bit → signed). Each
/// sample's WAV rate is the C-4 playback rate derived from its relative note and
/// finetune. Read-only — rebuilding a valid instrument requires the full envelope chain.
/// </summary>
public sealed class XiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Xi";
  public string DisplayName => "XI (FastTracker II Instrument)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".xi";
  public IReadOnlyList<string> Extensions => [".xi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("Extended Instrument: "u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "FastTracker II instrument; full file + one WAV per sample (FT2 delta decoded).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── parsing ────────────────────────────────────────────────────────────────

  // Header layout (little-endian):
  //   0   char[21]  "Extended Instrument: "
  //   21  char[22]  instrument name
  //   43  u8        0x1A
  //   44  char[20]  tracker name
  //   64  u16       version (0x0102)
  //   66  …232-byte instrument header (96 keymap bytes, vol/pan envelopes, vibrato, …)
  //   266 u16       number of samples
  //   268 numSamples × 40-byte sample headers
  //   …   sample data (FT2 delta-encoded, in header order)
  private const int NameOffset = 21;
  private const int TrackerOffset = 44;
  private const int NumSamplesOffset = 0x10A;     // 266
  private const int SampleHeadersOffset = 0x10C;  // 268
  private const int SampleHeaderSize = 40;

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.xi", "Container", blob),
    };
    if (blob.Length < SampleHeadersOffset)
      return entries;

    var instrName = ReadAsciiTrim(blob, NameOffset, 22);
    var trackerName = ReadAsciiTrim(blob, TrackerOffset, 20);
    var version = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(64, 2));
    var numSamples = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(NumSamplesOffset, 2));

    // Sample headers.
    var headers = new List<SampleHeader>();
    var headersEnd = SampleHeadersOffset + numSamples * SampleHeaderSize;
    if (headersEnd > blob.Length)
      numSamples = 0; // malformed count — fall back to FULL-only.

    for (var i = 0; i < numSamples; ++i) {
      var off = SampleHeadersOffset + i * SampleHeaderSize;
      headers.Add(new SampleHeader(
        Length: (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off, 4))),
        Volume: blob[off + 12],
        Finetune: unchecked((sbyte)blob[off + 13]),
        Type: blob[off + 14],
        Panning: blob[off + 15],
        RelativeNote: unchecked((sbyte)blob[off + 16]),
        Name: ReadAsciiTrim(blob, off + 18, 22)));
    }

    // Sample data follows the header table, concatenated in header order.
    var dataCursor = SampleHeadersOffset + numSamples * SampleHeaderSize;
    var sampleCount = 0;
    for (var i = 0; i < headers.Count; ++i) {
      var h = headers[i];
      if (h.Length <= 0 || dataCursor >= blob.Length)
        continue;
      var take = Math.Min(h.Length, blob.Length - dataCursor);
      if (take <= 0)
        continue;

      var is16 = (h.Type & 0x10) != 0;
      var raw = blob.AsSpan(dataCursor, take).ToArray();
      var rate = ComputeRate(h.RelativeNote, h.Finetune);

      byte[] wav;
      if (is16) {
        // 16-bit signed delta → running sum, kept as signed little-endian PCM.
        var pcm = DecodeDelta16(raw);
        wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: rate, bitsPerSample: 16);
      } else {
        // 8-bit signed delta → running sum, rebiased to WAV's unsigned 8-bit.
        var signed = DecodeDelta8(raw);
        var unsigned = new byte[signed.Length];
        for (var s = 0; s < signed.Length; ++s)
          unsigned[s] = unchecked((byte)(signed[s] + 128));
        wav = PcmCodec.ToWavBlob(unsigned, channels: 1, sampleRate: rate, bitsPerSample: 8);
      }

      var safe = string.IsNullOrWhiteSpace(h.Name) ? $"sample_{i:D2}" : SanitizeFileName(h.Name);
      entries.Add(new($"samples/{i:D2}_{safe}.wav", "Sample", wav));
      dataCursor += h.Length;
      ++sampleCount;
    }

    var info = new StringBuilder();
    info.AppendLine($"name={instrName}");
    info.AppendLine($"tracker={trackerName}");
    info.AppendLine($"version={version >> 8}.{version & 0xFF}");
    info.AppendLine($"sample_count={sampleCount}");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  /// <summary>C-4 playback rate: 8363 · 2^((relativeNote + finetune/128)/12), rounded.</summary>
  private static int ComputeRate(sbyte relativeNote, sbyte finetune) {
    var period = (relativeNote + finetune / 128.0) / 12.0;
    var rate = 8363.0 * Math.Pow(2.0, period);
    if (rate < 1) rate = 1;
    if (rate > int.MaxValue) rate = int.MaxValue;
    return (int)Math.Round(rate);
  }

  /// <summary>FT2 8-bit delta decode: each stored byte is a signed delta; output is the running sum.</summary>
  public static byte[] DecodeDelta8(ReadOnlySpan<byte> delta) {
    var output = new byte[delta.Length];
    byte running = 0;
    for (var i = 0; i < delta.Length; ++i) {
      running = unchecked((byte)(running + delta[i]));
      output[i] = running;
    }
    return output;
  }

  /// <summary>FT2 16-bit delta decode: each stored s16 LE value is a delta; output is the running sum (s16 LE).</summary>
  public static byte[] DecodeDelta16(ReadOnlySpan<byte> delta) {
    var count = delta.Length / 2;
    var output = new byte[count * 2];
    short running = 0;
    for (var i = 0; i < count; ++i) {
      var d = BinaryPrimitives.ReadInt16LittleEndian(delta.Slice(i * 2, 2));
      running = unchecked((short)(running + d));
      BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), running);
    }
    return output;
  }

  private readonly record struct SampleHeader(
    int Length, byte Volume, sbyte Finetune, byte Type, byte Panning, sbyte RelativeNote, string Name);

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
      if (char.IsLetterOrDigit(c) || c is '_' or '-' or '.') sb.Append(c);
      else sb.Append('_');
    }
    var s = sb.ToString().Trim('.', '_', ' ');
    return s.Length == 0 ? "sample" : s;
  }
}
