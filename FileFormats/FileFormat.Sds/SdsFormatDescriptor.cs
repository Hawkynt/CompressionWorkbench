#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Sds;

/// <summary>
/// Exposes a MIDI Sample Dump Standard file (<c>.sds</c>) as a pseudo-archive:
/// <c>FULL.sds</c> (the byte-exact SysEx dump) plus the decoded mono sample
/// (<c>samples/000.wav</c>) and a <c>metadata.ini</c> summary. The dump-header packet
/// describes word size, sample rate (1e9 / period) and length; data packets carry the
/// samples packed MSB-first as septets (7 bits per byte, bit 6 first), which are
/// reassembled, normalised to 16-bit and surfaced as a playable WAV. Read-only;
/// trailing/truncated data packets are tolerated.
/// </summary>
public sealed class SdsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Sds";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "SDS (MIDI Sample Dump)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Audio;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".sds";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sds"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xF0, 0x7E], Offset: 0, Confidence: 0.6),
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
public string Description => "MIDI Sample Dump Standard; full file + decoded mono WAV.";

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

  // ── parsing ────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sds", "Container", blob),
    };

    var parsed = Parse(blob);
    if (parsed == null)
      return entries;

    if (parsed.Pcm16.Length > 0)
      entries.Add(new("samples/000.wav", "Sample",
        PcmCodec.ToWavBlob(parsed.Pcm16, channels: 1, sampleRate: parsed.SampleRate, bitsPerSample: 16)));

    var info = new StringBuilder();
    info.AppendLine($"sample_number={parsed.SampleNumber}");
    info.AppendLine($"bits_per_sample={parsed.BitsPerSample}");
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"length_words={parsed.LengthWords}");
    info.AppendLine($"loop_start={parsed.LoopStart}");
    info.AppendLine($"loop_end={parsed.LoopEnd}");
    info.AppendLine($"loop_type={parsed.LoopType}");
    info.AppendLine($"decoded_samples={parsed.Pcm16.Length / 2}");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  internal sealed record ParsedSds(
    int SampleNumber, int BitsPerSample, int SampleRate, int LengthWords,
    int LoopStart, int LoopEnd, int LoopType, byte[] Pcm16);

  /// <summary>Three 7-bit bytes, little-endian (LSB first).</summary>
  private static int Read21(ReadOnlySpan<byte> b)
    => (b[0] & 0x7F) | ((b[1] & 0x7F) << 7) | ((b[2] & 0x7F) << 14);

  internal static ParsedSds? Parse(byte[] blob) {
    // Locate the dump header packet: F0 7E cc 01 … F7 (21 bytes).
    var hdr = FindPacket(blob, 0, 0x01);
    if (hdr < 0 || hdr + 21 > blob.Length || blob[hdr + 20] != 0xF7)
      return null;

    var sampleNumber = (blob[hdr + 4] & 0x7F) | ((blob[hdr + 5] & 0x7F) << 7);
    var bits = blob[hdr + 6] & 0x7F;
    var period = Read21(blob.AsSpan(hdr + 7, 3));   // sample period in nanoseconds
    var lengthWords = Read21(blob.AsSpan(hdr + 10, 3));
    var loopStart = Read21(blob.AsSpan(hdr + 13, 3));
    var loopEnd = Read21(blob.AsSpan(hdr + 16, 3));
    var loopType = blob[hdr + 19] & 0x7F;
    var rate = period > 0 ? (int)Math.Round(1_000_000_000.0 / period) : 8000;
    if (bits is < 8 or > 28) bits = 16;

    // Septets per sample word.
    var septets = (bits + 6) / 7;
    var samples = new List<int>(lengthWords > 0 ? lengthWords : 0);

    // Walk data packets: F0 7E cc 02 kk <120 payload> checksum F7 (127 bytes).
    var pos = hdr + 21;
    while (pos < blob.Length) {
      var dp = FindPacket(blob, pos, 0x02);
      if (dp < 0)
        break;
      // payload starts after F0 7E cc 02 kk (5 bytes); 120 payload bytes nominally.
      var payloadStart = dp + 5;
      // Determine payload length: up to the trailing checksum+F7, capped at 120.
      var end = FindByte(blob, payloadStart, 0xF7);
      var available = (end < 0 ? blob.Length : end) - payloadStart;
      var payloadLen = Math.Min(120, Math.Max(0, available - 1)); // exclude checksum byte
      if (payloadLen <= 0) { pos = end < 0 ? blob.Length : end + 1; continue; }

      DecodePayload(blob.AsSpan(payloadStart, payloadLen), septets, bits, samples);
      pos = end < 0 ? blob.Length : end + 1;
    }

    if (lengthWords > 0 && samples.Count > lengthWords)
      samples.RemoveRange(lengthWords, samples.Count - lengthWords);

    // Convert each sample to 16-bit signed. SDS samples are left-justified unsigned
    // magnitude with the MSB as a sign-ish offset (0x8000 = zero); shift to 16-bit and
    // re-bias to signed PCM.
    var pcm = new byte[samples.Count * 2];
    for (var i = 0; i < samples.Count; ++i) {
      var v = samples[i];               // left-justified into `bits` bits, MSB first
      // Scale the `bits`-wide value up to a full 16-bit range.
      var shift = bits - 16;
      int s16 = shift >= 0 ? (v >> shift) : (v << -shift);
      // SDS stores unsigned with 0x8000(scaled) = silence → re-center to signed.
      s16 -= 0x8000;
      if (s16 < short.MinValue) s16 = short.MinValue;
      if (s16 > short.MaxValue) s16 = short.MaxValue;
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)s16);
    }

    return new ParsedSds(sampleNumber, bits, rate, lengthWords, loopStart, loopEnd, loopType, pcm);
  }

  /// <summary>
  /// Decodes one data-packet payload into <paramref name="samples"/>. Each sample word
  /// occupies <paramref name="septets"/> consecutive bytes, MSB-first (bit 6 first),
  /// left-justified into a 7·septets-bit field.
  /// </summary>
  private static void DecodePayload(ReadOnlySpan<byte> payload, int septets, int bits, List<int> samples) {
    var fieldBits = septets * 7;
    var full = payload.Length / septets;
    for (var w = 0; w < full; ++w) {
      var value = 0;
      for (var s = 0; s < septets; ++s)
        value = (value << 7) | (payload[w * septets + s] & 0x7F);
      // value is left-justified into `fieldBits`; keep the top `bits` bits.
      var sample = fieldBits >= bits ? value >> (fieldBits - bits) : value << (bits - fieldBits);
      samples.Add(sample);
    }
  }

  /// <summary>Finds the start of a SysEx packet (F0 7E cc subId) from <paramref name="from"/>.</summary>
  private static int FindPacket(byte[] blob, int from, byte subId) {
    for (var i = Math.Max(0, from); i + 4 <= blob.Length; ++i)
      if (blob[i] == 0xF0 && blob[i + 1] == 0x7E && blob[i + 3] == subId)
        return i;
    return -1;
  }

  private static int FindByte(byte[] blob, int from, byte value) {
    for (var i = Math.Max(0, from); i < blob.Length; ++i)
      if (blob[i] == value) return i;
    return -1;
  }
}
