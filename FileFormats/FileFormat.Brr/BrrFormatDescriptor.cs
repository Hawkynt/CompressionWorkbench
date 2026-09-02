#pragma warning disable CS1591
using System.Text;
using Codec.Brr;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Brr;

/// <summary>
/// Exposes a raw SNES <c>.brr</c> sample (S-DSP Bit Rate Reduction) as a pseudo-archive:
/// <c>FULL.brr</c> (the byte-exact file), one decoded mono <c>MONO.wav</c> (32000 Hz by
/// default — the S-DSP's nominal playback rate), and a <c>metadata.ini</c> summary.
/// <para>BRR is headerless 9-byte blocks, so there is no magic signature to key on. Some
/// tools prepend a 2-byte little-endian loop-point header; that variant is detected by
/// <c>fileLength % 9 == 2</c>, the two bytes are skipped before decoding, and the loop
/// point is reported in the metadata.</para>
/// </summary>
public sealed class BrrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Brr";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "SNES BRR sample";
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
  public string DefaultExtension => ".brr";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".brr"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  // Empty — BRR is headerless 9-byte blocks with no distinguishing magic, so the format
  // is keyed purely on the .brr extension (and explicit registry lookup).
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "SNES BRR sample; full file + decoded mono WAV.";

  /// <summary>Nominal S-DSP playback rate used when surfacing the decoded WAV.</summary>
  private const int DefaultSampleRate = 32000;

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

  // ── IArchiveCreatable: passthrough FULL.brr, or encode a mono WAV → BRR ──────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.brr", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("BRR archive create needs either FULL.brr or a mono WAV.");

    var wav = new WavReader().Read(wavInput.Data);
    if (wav.NumChannels != 1)
      throw new InvalidOperationException("BRR is mono; supply a single-channel WAV.");
    if (wav.BitsPerSample != 16)
      throw new InvalidOperationException("BRR create expects 16-bit PCM input.");

    var samples = LePcmToShorts(wav.InterleavedPcm);
    output.Write(BrrCodec.Encode(samples));
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
    "BRR archive accepts: FULL.brr or a mono 16-bit WAV (encoded to BRR)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.brr" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a BRR-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── parsing ────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.brr", "Container", blob),
    };

    // A 2-byte loop-point header is present when the remaining length is a whole number of
    // 9-byte blocks, i.e. (length % 9) == 2. Skip it before decoding.
    var blockOffset = 0;
    int? loopPoint = null;
    if (blob.Length % BrrCodec.BlockSize == 2) {
      loopPoint = blob[0] | (blob[1] << 8);
      blockOffset = 2;
    }

    var samples = BrrCodec.Decode(blob.AsSpan(blockOffset));
    var pcm = ShortsToLePcm(samples);
    entries.Add(new("MONO.wav", "Channel",
      PcmCodec.ToWavBlob(pcm, channels: 1, DefaultSampleRate, bitsPerSample: 16, formatCode: 1), "brr"));

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={DefaultSampleRate}");
    info.AppendLine($"block_count={(blob.Length - blockOffset) / BrrCodec.BlockSize}");
    info.AppendLine($"decoded_samples={samples.Length}");
    info.AppendLine(loopPoint is { } lp
      ? $"loop_point={lp}"
      : "loop_point=none");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  private static short[] LePcmToShorts(byte[] pcm) {
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return samples;
  }
}
