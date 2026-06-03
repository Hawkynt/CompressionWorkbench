#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.EaSchl;

/// <summary>
/// Exposes an Electronic Arts SCHl audio stream as a pseudo-archive of <c>FULL.eam</c>, one
/// decoded mono PCM WAV per channel (named per <see cref="ChannelLayout"/>), and a
/// <c>metadata.ini</c> with the channel count, sample rate and compression type. When the
/// carried audio uses a compression this build can't decode, only <c>FULL</c> plus metadata
/// are surfaced.
/// <para>The <c>.asf</c>/<c>.str</c> extensions real EA files use are avoided here because
/// they clash with Microsoft ASF and generic stream containers; detection leans on the
/// <c>SCHl</c> magic plus the <c>.eam</c>/<c>.sng</c> extensions.</para>
/// </summary>
public sealed class EaSchlFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  public string Id => "EaSchl";
  public string DisplayName => "Electronic Arts SCHl Stream";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".eam";
  public IReadOnlyList<string> Extensions => [".eam", ".sng"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SCHl"u8.ToArray(), Confidence: 0.9),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ea-xa", "EA-XA ADPCM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Electronic Arts SCHl stream (EA-XA ADPCM); full file + decoded per-channel WAVs.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: passthrough FULL.eam, or encode a WAV → SCHl ──────────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.eam", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("EaSchl archive create needs either FULL.eam or a WAV.");

    var wav = new WavReader().Read(wavInput.Data);
    if (wav.BitsPerSample != 16)
      throw new InvalidOperationException("EaSchl create expects 16-bit PCM input.");

    var samples = LePcmToShorts(wav.InterleavedPcm);
    var schl = EaSchlWriter.Write(samples, wav.NumChannels, wav.SampleRate);
    output.Write(schl);
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "EaSchl archive accepts: FULL.eam or a 16-bit WAV (encoded to EA-XA ADPCM)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.eam" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not an EaSchl-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.eam", "Container", blob),
    };

    var reader = new EaSchlReader(blob);

    var info = new StringBuilder();
    info.AppendLine($"channels={reader.Channels}");
    info.AppendLine($"sample_rate={reader.SampleRate}");
    info.AppendLine($"compression=0x{reader.Compression:X2}");
    info.AppendLine($"total_samples={reader.TotalSamples}");

    var decoded = reader.DecodeInterleaved();
    if (decoded == null) {
      info.AppendLine("note=carried compression not decodable in this build; FULL only");
    } else {
      var pcm = ShortsToLePcm(decoded);
      var split = PcmCodec.SplitInterleavedPcm(pcm, reader.Channels, reader.SampleRate, bitsPerSample: 16);
      foreach (var (name, wavBlob) in split)
        entries.Add(new($"{name}.wav", "Channel", wavBlob, "ea-xa"));
    }

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
