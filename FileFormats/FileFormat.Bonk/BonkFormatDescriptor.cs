#pragma warning disable CS1591
using System.Text;
using Codec.Bonk;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Bonk;

/// <summary>
/// Exposes a Bonk (<c>.bonk</c>) file as a pseudo-archive of <c>FULL.bonk</c> plus,
/// when the bitstream decodes, one mono WAV per channel and a <c>metadata.ini</c> tag.
/// The descriptor is creatable (WORM): it passes through <c>FULL.bonk</c> or assembles
/// a new lossless Bonk stream from one or two mono PCM16 WAV channel files.
/// </summary>
public sealed class BonkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Bonk";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Bonk Audio";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".bonk";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".bonk"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x00, (byte)'B', (byte)'O', (byte)'N', (byte)'K'], Offset: 0, Confidence: 0.30)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("bonk", "Bonk")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Bonk lossless/lossy audio; full file + decoded per-channel PCM.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    AudioPseudoArchive.List(BuildEntries(stream));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "Bonk archive accepts: FULL.bonk or one/two mono 16-bit PCM WAV channel files";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.bonk", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"not a Bonk-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.bonk", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();
    if (channelBlobs.Count is < 1 or > 2)
      throw new InvalidOperationException("Bonk create requires one or two mono PCM16 WAV channel files.");

    var channels = channelBlobs.Select(b => new WavReader().Read(b.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.NumChannels != 1 || c.BitsPerSample != 16))
      throw new InvalidOperationException("Bonk create requires mono 16-bit integer PCM WAV channel files.");
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All Bonk channel WAVs must share sample rate and frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), 16);
    output.Write(BonkCodec.Compress(interleaved, channels.Count, first.SampleRate));
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.bonk", "Container", blob, "bonk"),
    };

    try {
      var info = BonkCodec.ReadStreamInfo(blob, out _);
      var pcm = BonkCodec.Decompress(blob);

      if (info.Channels <= 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm, 1, info.SampleRate, 16, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcm, info.Channels, info.SampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }

      var meta = new StringBuilder();
      meta.AppendLine("format=Bonk");
      meta.AppendLine($"channels={info.Channels}");
      meta.AppendLine($"sample_rate={info.SampleRate}");
      meta.AppendLine("bits_per_sample=16");
      meta.AppendLine($"samples_per_channel={info.SamplesPerChannel}");
      meta.AppendLine($"lossless={info.Lossless}");
      meta.AppendLine($"mid_side={info.MidSide}");
      meta.AppendLine($"n_taps={info.NTaps}");
      meta.AppendLine($"down_sampling={info.DownSampling}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString()), "stored"));
    } catch (Exception) {
      // Graceful fallback: surface the original Bonk file only.
    }

    return entries;
  }
}
