#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.CriAdx;
using Codec.Mp3;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Adx;

/// <summary>
/// Archive-shaped view of a CRI ADX file (<c>.adx</c>, big-endian <c>0x8000</c> magic):
/// a byte-exact <c>FULL.adx</c> container plus one decoded mono PCM WAV per channel
/// (named per <see cref="ChannelLayout"/>) and a <c>metadata.ini</c> carrying the
/// stream's sample rate, channel count, version and high-pass cutoff. Decoding goes
/// through the in-repo <see cref="AdxCodec"/>; when the codec cannot handle the input
/// (encrypted streams, AHX/non-standard encoding types, malformed headers) the view
/// degrades gracefully to <c>FULL.adx</c> only.
/// </summary>
public sealed class AdxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Adx";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "CRI ADX";
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
  public string DefaultExtension => ".adx";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".adx"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x80, 0x00], Confidence: 0.4),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("adx", "CRI ADX ADPCM")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "CRI ADX ADPCM; full file + decoded per-channel PCM.";

  // ── IArchiveFormatOperations ─────────────────────────────────────────

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

  // ── IArchiveInMemoryExtract ──────────────────────────────────────────

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: passthrough FULL.adx, or encode per-channel WAVs → ADX ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a provided FULL.adx verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.adx", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("ADX archive create needs either FULL.adx or one or more per-channel mono WAVs.");

    var channels = new List<WavReader.ParsedWav>();
    foreach (var (_, data) in channelBlobs)
      channels.Add(new WavReader().ReadCanonicalPcm(data));

    var first = channels[0];
    if (channels.Any(c => c.NumChannels != 1))
      throw new InvalidOperationException("ADX create expects mono per-channel WAVs.");
    if (channels.Any(c => c.BitsPerSample != 16))
      throw new InvalidOperationException("ADX create expects 16-bit PCM input.");
    if (channels.Any(c => c.SampleRate != first.SampleRate))
      throw new InvalidOperationException("All channel WAVs must share the same sample rate.");

    const int bytesPerSample = 2;
    var frameCount = first.InterleavedPcm.Length / bytesPerSample;
    if (channels.Any(c => c.InterleavedPcm.Length / bytesPerSample != frameCount))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var interleavedBytes = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), bitsPerSample: 16);
    var samples = LePcmToShorts(interleavedBytes);

    var adx = AdxCodec.Encode(samples, channels.Count, first.SampleRate);
    output.Write(adx);
  }

  // ── IArchiveWriteConstraints ─────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "ADX archive accepts: FULL.adx, MONO/LEFT/RIGHT/CENTER/… .wav (per-channel, mono 16-bit)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.adx" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not an ADX-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── Shared archive-entry builder ─────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.adx", "Container", blob, "adx"),
    };

    // Decode to 16-bit LE interleaved PCM and split per channel. The codec throws
    // for encrypted / non-standard / malformed streams; either way keep FULL-only.
    try {
      var info = AdxCodec.ReadInfo(blob);

      // AHX (encoding type 0x10/0x11): the payload after the ADX header is an MPEG-2
      // Layer II (22.05 kHz mono) elementary stream — decode it via the MP3 codec.
      if (info.IsAhx) {
        AddAhxEntries(blob, info, entries);
        return entries;
      }

      var (samples, channels, sampleRate) = AdxCodec.Decode(blob);
      var pcm = ShortsToLePcm(samples);
      const int bitsPerSample = 16;

      if (channels == 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate, bitsPerSample, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels, sampleRate, bitsPerSample))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }

      var meta = new StringBuilder();
      meta.AppendLine($"sample_rate={sampleRate}");
      meta.AppendLine($"channels={channels}");
      meta.AppendLine($"total_samples={info.TotalSamples}");
      meta.AppendLine($"version={info.Version}");
      meta.AppendLine($"highpass_frequency={info.HighpassFrequency}");
      meta.AppendLine($"encoding_type={info.EncodingType}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
    } catch (Exception) {
      // Graceful fallback: surface FULL.adx only.
    }

    return entries;
  }

  /// <summary>
  /// Surfaces a decoded AHX stream. The bytes after the ADX header form an MPEG-2 Layer II
  /// (22.05 kHz mono) elementary stream; we feed them to the in-repo MP3 decoder, which
  /// covers MPEG-2 LSF Layer II, and emit a MONO.wav plus metadata. Any decode failure
  /// keeps the already-added FULL.adx and appends a metadata note.
  /// </summary>
  private static void AddAhxEntries(byte[] blob, AdxCodec.AdxInfo info, List<AudioPseudoArchive.Entry> entries) {
    var meta = new StringBuilder();
    meta.AppendLine($"encoding_type={info.EncodingType}");
    meta.AppendLine("codec=ahx");
    meta.AppendLine("payload=mpeg2-layer2");

    var payload = info.DataOffset < blob.Length ? blob[info.DataOffset..] : [];
    try {
      var streamInfo = Mp3Codec.ReadStreamInfo(new MemoryStream(payload));
      using var pcmStream = new MemoryStream();
      Mp3Codec.Decompress(new MemoryStream(payload), pcmStream);
      var pcm = pcmStream.ToArray();

      if (pcm.Length == 0)
        throw new InvalidDataException("AHX MPEG-2 Layer II payload decoded to no samples.");

      var rate = streamInfo.SampleRate > 0 ? streamInfo.SampleRate : 22050;
      var channels = streamInfo.Channels > 0 ? streamInfo.Channels : 1;
      const int bitsPerSample = 16;

      if (channels == 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm, channels: 1, rate, bitsPerSample, formatCode: 1), "mp2"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels, rate, bitsPerSample))
          entries.Add(new($"{name}.wav", "Channel", wav, "mp2"));
      }

      meta.AppendLine($"sample_rate={rate}");
      meta.AppendLine($"channels={channels}");
    } catch (Exception) {
      // MP3 decoder could not handle this AHX payload — keep FULL.adx and note it.
      meta.AppendLine("note=AHX MPEG-2 Layer II payload could not be decoded; FULL.adx only.");
    }

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static short[] LePcmToShorts(byte[] pcm) {
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return samples;
  }
}
