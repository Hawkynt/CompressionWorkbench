#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Codec.OkiAdpcm;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Vox;

/// <summary>
/// Exposes a Dialogic <c>.vox</c> (OKI / Dialogic 4-bit ADPCM) file as an archive of
/// <c>FULL.vox</c> (the byte-exact container), <c>MONO.wav</c> (the decoded 16-bit
/// PCM at the assumed sample rate) and <c>metadata.ini</c> recording the assumptions.
/// <para>
/// VOX is <b>headerless</b> — the raw file is nothing but packed ADPCM nibbles, so
/// there is no magic signature to match on (<see cref="MagicSignatures"/> is empty and
/// dispatch is by <c>.vox</c> extension only, the same approach
/// <c>FlacArchiveDescriptor</c> uses for its headerless archive view). With no header
/// the stream carries no rate or channel-count metadata, so the Dialogic default of
/// <b>mono, 8000 Hz</b> is assumed and surfaced in <c>metadata.ini</c>.
/// </para>
/// </summary>
public sealed class VoxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>Assumed sample rate for headerless Dialogic VOX (the Dialogic default).</summary>
  public const int AssumedSampleRate = 8000;

  public string Id => "Vox";
  public string DisplayName => "Dialogic VOX (OKI ADPCM)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vox";
  public IReadOnlyList<string> Extensions => [".vox"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Headerless: no byte signature exists for raw VOX ADPCM. Dispatch is by extension only
  // (precedent: FlacArchiveDescriptor's empty magic for its archive view).
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Dialogic VOX (OKI 4-bit ADPCM); decoded to a mono WAV at the assumed 8000 Hz.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: assemble a .vox from a mono WAV (or pass through FULL.vox) ──

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a provided FULL.vox verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.vox", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wav = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wav.Data == null)
      throw new InvalidOperationException("VOX archive create needs either FULL.vox or a mono WAV.");

    var parsed = new WavReader().Read(wav.Data);
    if (parsed.NumChannels != 1)
      throw new InvalidOperationException("VOX is mono — supply a single-channel WAV.");

    var samples = LePcmToShorts(parsed.InterleavedPcm, parsed.BitsPerSample);
    var encoded = OkiAdpcmCodec.Encode(samples);
    output.Write(encoded);
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "VOX archive accepts: FULL.vox, MONO.wav (single-channel), metadata.ini";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.vox" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null; return true;
    }
    reason = $"not a VOX-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.vox", "Container", blob),
    };

    if (blob.Length > 0) {
      var pcm = OkiAdpcmCodec.Decode(blob);
      var le = ShortsToLePcm(pcm);
      entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(le, channels: 1, AssumedSampleRate, bitsPerSample: 16), "pcm"));
    }

    var info = new StringBuilder();
    info.AppendLine("; Dialogic VOX is headerless; the following are assumed defaults.");
    info.AppendLine("codec=OKI/Dialogic ADPCM (4-bit)");
    info.Append("sample_rate=").AppendLine(AssumedSampleRate.ToString(CultureInfo.InvariantCulture));
    info.AppendLine("channels=1");
    info.AppendLine("bits_per_sample=16 (decoded)");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }

  /// <summary>Reads little-endian PCM as 16-bit samples; 8-bit unsigned PCM is promoted to 16-bit.</summary>
  private static short[] LePcmToShorts(byte[] pcm, int bitsPerSample) {
    if (bitsPerSample == 8) {
      var s8 = new short[pcm.Length];
      for (var i = 0; i < pcm.Length; ++i) s8[i] = (short)((pcm[i] - 128) << 8);
      return s8;
    }

    var count = pcm.Length / 2;
    var s = new short[count];
    for (var i = 0; i < count; ++i)
      s[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return s;
  }
}
