#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Cvsd;

/// <summary>
/// Raw CVSD container: a headerless stream of continuously-variable-slope delta-modulation
/// bits (one bit per sample). Like raw G.711 (see <c>G711FormatDescriptorBase</c>) there is
/// no magic and no embedded sample rate or channel count, so dispatch is extension-only. By
/// the Bluetooth SCO convention the stream is assumed mono at 64000 Hz; that assumption is
/// documented in the surfaced <c>metadata.ini</c>.
/// <para>The archive view surfaces <c>FULL.cvsd</c> (the byte-exact CVSD stream, Kind
/// <c>Container</c>), <c>MONO.wav</c> (the whole payload decoded to 16-bit LE PCM @ 64000 Hz,
/// Kind <c>Channel</c>) and <c>metadata.ini</c> (Kind <c>Tag</c>). Create either passes a
/// provided <c>FULL.cvsd</c> through verbatim or re-encodes a single mono 16-bit WAV back to
/// the CVSD bitstream.</para>
/// </summary>
public sealed class CvsdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>The CVSD default sample rate (64 kHz, Bluetooth SCO narrowband voice).</summary>
  private const int DefaultSampleRate = 64000;

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Cvsd";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Raw CVSD";
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
public string DefaultExtension => ".cvsd";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".cvsd"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // Headerless: no magic — dispatch is extension-only (precedent: raw G.711).
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
public string Description => "Raw CVSD (delta-modulation) stream; full file + decoded mono PCM WAV.";

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

  // ── IArchiveCreatable ───────────────────────────────────────────────────────────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a provided FULL.cvsd verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.cvsd", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    // Otherwise re-encode a single mono 16-bit WAV into the CVSD bitstream.
    var wav = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wav.Data == null)
      throw new InvalidOperationException("Raw CVSD create needs either FULL.cvsd or a single mono 16-bit WAV.");

    var parsed = new WavReader().Read(wav.Data);
    if (parsed.NumChannels != 1)
      throw new InvalidOperationException("Raw CVSD is mono; the source WAV must have exactly one channel.");
    if (parsed.BitsPerSample != 16)
      throw new InvalidOperationException("Raw CVSD create expects a 16-bit PCM WAV.");

    output.Write(Codec.Cvsd.CvsdCodec.Encode(LePcmToShorts(parsed.InterleavedPcm)));
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "Raw CVSD archive accepts: FULL.cvsd or a single mono 16-bit WAV.";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.cvsd" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a raw CVSD input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.cvsd", "Container", blob),
    };

    var linear = Codec.Cvsd.CvsdCodec.Decode(blob);
    var wavBlob = PcmCodec.ToWavBlob(
      ShortsToLePcm(linear), channels: 1, DefaultSampleRate, bitsPerSample: 16, formatCode: 1);
    entries.Add(new("MONO.wav", "Channel", wavBlob, "pcm"));

    var info = new StringBuilder();
    info.AppendLine("codec=CVSD (continuously-variable-slope delta modulation)");
    info.AppendLine("channels=1");
    info.AppendLine($"sample_rate={DefaultSampleRate}");
    info.AppendLine("bits_per_sample=1 (delta-modulated)");
    info.AppendLine("note=headerless raw stream; mono 64000 Hz assumed per the Bluetooth SCO default.");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
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
