#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Hcom;

/// <summary>
/// Exposes a Macintosh HCOM sound (<c>.hcom</c>) as a pseudo-archive: <c>FULL.hcom</c>
/// (the byte-exact file) plus the decoded mono channel (<c>MONO.wav</c>) and a
/// <c>metadata.ini</c> summary. HCOM stores 8-bit unsigned samples Huffman-compressed
/// over their (delta) values; this implementation reproduces sox's <c>hcom.c</c>
/// container faithfully: it locates the <c>HCOM</c> data fork (raw, or inside a
/// 128-byte MacBinary header whose <c>FSSD</c> sits at offset 65), reads the
/// big-endian header and the <c>dictsize</c> Huffman tree entries, then walks the
/// 32-bit-word bitstream MSB-first, accumulating deltas. Create emits the same
/// container (single mono WAV → delta Huffman bitstream) so round-trips are testable.
/// </summary>
public sealed class HcomFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Hcom";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "HCOM (Macintosh)";
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
  public string DefaultExtension => ".hcom";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".hcom"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  // The raw Mac sound data fork starts with "FSSD"; HCOM-compressed forks start with
  // "HCOM" (at offset 0, or at offset 65 inside a MacBinary header).
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("FSSD"u8.ToArray(), Offset: 0, Confidence: 0.85),
    new("HCOM"u8.ToArray(), Offset: 0, Confidence: 0.85),
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
  public string Description => "Macintosh HCOM sound; full file + decoded mono WAV (Huffman delta tree walk).";

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

  internal sealed record ParsedHcom(int SampleRate, int CompressType, int Divisor, byte[] Pcm8);

  /// <summary>Locates the "HCOM" data fork (offset 0 or inside MacBinary at 65); returns its start or -1.</summary>
  private static int FindHcom(byte[] blob) {
    if (HasMagic(blob, 0, "HCOM"u8)) return 0;
    // 128-byte MacBinary: "FSSD" lives at offset 65; the HCOM data fork begins at 128.
    if (blob.Length >= 132 && HasMagic(blob, 65, "FSSD"u8) && HasMagic(blob, 128, "HCOM"u8))
      return 128;
    // Raw Mac data fork "FSSD": some encoders prepend it directly before HCOM.
    if (HasMagic(blob, 0, "FSSD"u8)) {
      // Scan a small window for an embedded HCOM fork.
      for (var i = 4; i + 4 <= Math.Min(blob.Length, 4096); ++i)
        if (HasMagic(blob, i, "HCOM"u8)) return i;
    }
    return -1;
  }

  private static bool HasMagic(byte[] blob, int offset, ReadOnlySpan<byte> magic) {
    if (offset < 0 || offset + magic.Length > blob.Length) return false;
    for (var i = 0; i < magic.Length; ++i)
      if (blob[offset + i] != magic[i]) return false;
    return true;
  }

  internal static ParsedHcom? Parse(byte[] blob) {
    var start = FindHcom(blob);
    if (start < 0 || start + 22 > blob.Length)
      return null;

    var huffcount = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(start + 4, 4));
    // checksum at start+8 (verified loosely — we don't fail on mismatch).
    var compressType = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(start + 12, 4));
    var divisor = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(start + 16, 4));
    if (divisor is < 1 or > 8) divisor = 1;
    var rate = 22050 / divisor;

    var dictSize = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(start + 20, 2));
    var dictOff = start + 22;
    if (dictOff + dictSize * 4 > blob.Length)
      return null;

    // Each dictionary entry holds two int16 child fields. For a leaf, leftson is
    // negative (sox uses -1) and rightson carries the signed delta datum; for an
    // internal node both fields are child indices.
    var leftSon = new short[dictSize];
    var rightSon = new short[dictSize];
    for (var i = 0; i < dictSize; ++i) {
      leftSon[i] = BinaryPrimitives.ReadInt16BigEndian(blob.AsSpan(dictOff + i * 4, 2));
      rightSon[i] = BinaryPrimitives.ReadInt16BigEndian(blob.AsSpan(dictOff + i * 4 + 2, 2));
    }

    // One padding byte, then the bitstream of big-endian 32-bit words. The first
    // decoded sample is the running accumulator seeded at 0; sox writes the very
    // first sample literally, but in the delta tree it is reached like any other.
    var streamOff = dictOff + dictSize * 4 + 1;
    if (streamOff > blob.Length)
      return null;

    var samples = new byte[Math.Max(0, huffcount)];
    var produced = 0;
    var sample = 0;
    var node = 0;
    var bitPos = 0;
    uint word = 0;
    var wordOff = streamOff;

    while (produced < samples.Length) {
      if (bitPos == 0) {
        if (wordOff + 4 > blob.Length)
          break; // tolerate truncation: stop with what we have.
        word = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(wordOff, 4));
        wordOff += 4;
        bitPos = 32;
      }
      var bit = (word >> 31) & 1;
      word <<= 1;
      --bitPos;

      // Follow the child index (sox: dictentry = leftson/rightson of current node).
      node = bit == 1 ? rightSon[node] : leftSon[node];
      if (node < 0 || node >= dictSize)
        break; // malformed tree / ran off the end.

      if (leftSon[node] < 0) {
        // Leaf reached: rightson is the datum added to the running sample. sox always
        // accumulates here (the delta-vs-value choice is an encoder-side concern).
        var datum = rightSon[node];
        sample = (sample + datum) & 0xFF;
        samples[produced++] = (byte)sample;
        node = 0;
      }
    }

    if (produced < samples.Length)
      Array.Resize(ref samples, produced);

    return new ParsedHcom(rate, compressType, divisor, samples);
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.hcom", "Container", blob),
    };

    var parsed = Parse(blob);
    if (parsed == null)
      return entries;

    if (parsed.Pcm8.Length > 0)
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(parsed.Pcm8, channels: 1, sampleRate: parsed.SampleRate, bitsPerSample: 8)));

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"divisor={parsed.Divisor}");
    info.AppendLine($"compress_type={parsed.CompressType} ({(parsed.CompressType == 1 ? "delta" : "value")})");
    info.AppendLine($"sample_count={parsed.Pcm8.Length}");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  // ── IArchiveCreatable: encode a single mono WAV to a delta-Huffman HCOM fork ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.hcom", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("HCOM archive create needs FULL.hcom or one mono WAV.");

    var wav = new WavReader().ReadCanonicalPcm(wavInput.Data);
    if (wav.NumChannels != 1)
      throw new InvalidOperationException("HCOM assembly accepts a single mono WAV.");

    var pcm8 = wav.BitsPerSample switch {
      8 => wav.InterleavedPcm,                         // already unsigned 8-bit
      16 => Sixteen2Eight(wav.InterleavedPcm),
      _ => throw new InvalidOperationException("HCOM assembly accepts 8-bit or 16-bit mono WAVs."),
    };

    var divisor = DivisorForRate(wav.SampleRate);
    output.Write(HcomEncoder.Encode(pcm8, divisor));
  }

  private static int DivisorForRate(int rate) {
    // 22050 / divisor, divisor 1..4. Pick the nearest.
    var best = 1;
    var bestErr = int.MaxValue;
    for (var d = 1; d <= 4; ++d) {
      var err = Math.Abs(22050 / d - rate);
      if (err < bestErr) { bestErr = err; best = d; }
    }
    return best;
  }

  private static byte[] Sixteen2Eight(byte[] pcm16) {
    var samples = pcm16.Length / 2;
    var r = new byte[samples];
    for (var i = 0; i < samples; ++i) {
      var s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2));
      r[i] = unchecked((byte)((s >> 8) + 128));   // signed 16-bit → unsigned 8-bit
    }
    return r;
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "HCOM archive accepts: FULL.hcom or one mono WAV (delta-Huffman encoded)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.hcom" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not an HCOM-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }
}
