#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.G711;

/// <summary>
/// Shared plumbing for the two raw G.711 containers (A-law and µ-law). A raw G.711
/// stream is headerless — there is no magic and no embedded sample rate or channel
/// count — so dispatch is extension-only (precedent: <c>FlacArchiveDescriptor</c>).
/// By the G.711 convention the stream is assumed mono at 8000 Hz; that assumption is
/// documented in the surfaced <c>metadata.ini</c>.
/// <para>The archive view surfaces <c>FULL.&lt;ext&gt;</c> (the byte-exact companded
/// stream, Kind <c>Container</c>), <c>MONO.wav</c> (the whole payload decoded to
/// 16-bit LE PCM @ 8000 Hz, Kind <c>Channel</c>) and <c>metadata.ini</c> (Kind
/// <c>Tag</c>). Create either passes a provided <c>FULL.&lt;ext&gt;</c> through verbatim
/// or re-encodes a single mono 16-bit WAV back to the companded stream.</para>
/// </summary>
public abstract class G711FormatDescriptorBase : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>The G.711 default sample rate (8 kHz, narrowband telephony).</summary>
  protected const int DefaultSampleRate = 8000;

  // ── codec-specific surface (implemented per variant) ─────────────────────────

  /// <summary>Decodes the whole companded payload to 16-bit linear samples.</summary>
  protected abstract short[] Decode(byte[] companded);

  /// <summary>Encodes 16-bit linear samples back to the companded representation.</summary>
  protected abstract byte[] Encode(short[] linear);

  /// <summary>Human label for this variant, e.g. <c>A-law</c> / <c>µ-law</c>.</summary>
  protected abstract string Variant { get; }

  public abstract string Id { get; }
  public abstract string DisplayName { get; }
  public abstract string DefaultExtension { get; }
  public abstract IReadOnlyList<string> Extensions { get; }

  // ── common descriptor metadata ───────────────────────────────────────────────

  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public IReadOnlyList<string> CompoundExtensions => [];
  // Headerless: no magic — dispatch is extension-only (precedent: FlacArchiveDescriptor).
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => $"Raw {this.Variant} (G.711) stream; full file + decoded mono PCM WAV.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(this.BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(this.BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(this.BuildEntries(input), entryName, output);

  // ── IArchiveCreatable ─────────────────────────────────────────────────────────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();
    var fullName = "FULL" + this.DefaultExtension;

    // Passthrough a provided FULL.<ext> verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals(fullName, StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    // Otherwise re-encode a single mono 16-bit WAV into the companded stream.
    var wav = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wav.Data == null)
      throw new InvalidOperationException(
        $"Raw G.711 create needs either {fullName} or a single mono 16-bit WAV.");

    var parsed = new WavReader().Read(wav.Data);
    if (parsed.NumChannels != 1)
      throw new InvalidOperationException("Raw G.711 is mono; the source WAV must have exactly one channel.");
    if (parsed.BitsPerSample != 16)
      throw new InvalidOperationException("Raw G.711 create expects a 16-bit PCM WAV.");

    var linear = LePcmToShorts(parsed.InterleavedPcm);
    output.Write(this.Encode(linear));
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    $"Raw {this.Variant} archive accepts: FULL{this.DefaultExtension} or a single mono 16-bit WAV.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == ("full" + this.DefaultExtension) || name.EndsWith(".wav") || name == "metadata.ini") {
      reason = null;
      return true;
    }
    reason = $"not a raw G.711 input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  private IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL" + this.DefaultExtension, "Container", blob),
    };

    var linear = this.Decode(blob);
    var wavBlob = PcmCodec.ToWavBlob(
      ShortsToLePcm(linear), channels: 1, DefaultSampleRate, bitsPerSample: 16, formatCode: 1);
    entries.Add(new("MONO.wav", "Channel", wavBlob, "pcm"));

    var info = new StringBuilder();
    info.AppendLine($"codec={this.Variant} (G.711)");
    info.AppendLine("channels=1");
    info.AppendLine($"sample_rate={DefaultSampleRate}");
    info.AppendLine("bits_per_sample=8 (companded)");
    info.AppendLine("note=headerless raw stream; mono 8000 Hz assumed per the G.711 default.");
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
