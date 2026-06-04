#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Lpc10;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Lpc10;

/// <summary>
/// Raw FS-1015 (LPC-10e, 2400 bit/s) container: a headerless stream of packed 54-bit LPC-10
/// frames (7 bytes each). Like raw CVSD and raw G.711 there is no magic and no embedded sample
/// rate or channel count, so dispatch is extension-only. The FS-1015 vocoder is defined at mono
/// 8000 Hz; that assumption is documented in the surfaced <c>metadata.ini</c>.
/// <para>The archive view surfaces <c>FULL.lpc10</c> (the byte-exact coded stream, Kind
/// <c>Container</c>), <c>MONO.wav</c> (the whole payload synthesized to 16-bit LE PCM @ 8000 Hz,
/// Kind <c>Channel</c>) and <c>metadata.ini</c> (Kind <c>Tag</c>). Create either passes a
/// provided <c>FULL.lpc10</c> through verbatim or analysis-encodes a single mono 16-bit WAV into
/// the LPC-10 bitstream.</para>
/// </summary>
public sealed class Lpc10FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>The FS-1015 sample rate (8 kHz mono).</summary>
  private const int DefaultSampleRate = 8000;

  public string Id => "Lpc10";
  public string DisplayName => "FS-1015 LPC-10";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lpc10";
  public IReadOnlyList<string> Extensions => [".lpc10", ".lpc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Headerless: no magic — dispatch is extension-only (precedent: raw CVSD / raw G.711).
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lpc10", "LPC-10")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Raw FS-1015 LPC-10 (2400 bit/s) speech stream; full file + synthesized mono PCM WAV.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable ───────────────────────────────────────────────────────────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a provided FULL.lpc10 verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.lpc10", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    // Otherwise analysis-encode a single mono 16-bit WAV into the LPC-10 bitstream.
    var wav = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wav.Data == null)
      throw new InvalidOperationException("Raw LPC-10 create needs either FULL.lpc10 or a single mono 16-bit WAV.");

    var parsed = new WavReader().Read(wav.Data);
    if (parsed.NumChannels != 1)
      throw new InvalidOperationException("LPC-10 is mono; the source WAV must have exactly one channel.");
    if (parsed.BitsPerSample != 16)
      throw new InvalidOperationException("Raw LPC-10 create expects a 16-bit PCM WAV.");

    output.Write(Lpc10Codec.Encode(LePcmToShorts(parsed.InterleavedPcm)));
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "Raw LPC-10 archive accepts: FULL.lpc10 or a single mono 16-bit WAV.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.lpc10" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a raw LPC-10 input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.lpc10", "Container", blob, "lpc10"),
    };

    var linear = Lpc10Codec.Decode(blob);
    var wavBlob = PcmCodec.ToWavBlob(
      ShortsToLePcm(linear), channels: 1, DefaultSampleRate, bitsPerSample: 16, formatCode: 1);
    entries.Add(new("MONO.wav", "Channel", wavBlob, "pcm"));

    var frames = blob.Length / 7;
    var durationSeconds = frames * 180.0 / DefaultSampleRate;

    var info = new StringBuilder();
    info.AppendLine("codec=LPC-10 (FS-1015, LPC-10e 2400 bit/s)");
    info.AppendLine("channels=1");
    info.AppendLine($"sample_rate={DefaultSampleRate}");
    info.AppendLine("frame_bits=54");
    info.AppendLine("frame_samples=180");
    info.AppendLine($"frames={frames}");
    info.AppendLine($"duration_seconds={durationSeconds:0.###}");
    info.AppendLine("note=headerless raw stream; mono 8000 Hz assumed per the FS-1015 default.");
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
