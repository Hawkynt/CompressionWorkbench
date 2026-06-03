#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Swav;

/// <summary>
/// Exposes a Nintendo DS <c>.swav</c> sample as a pseudo-archive: <c>FULL.swav</c> (the byte-exact
/// file), a decoded <c>MONO.wav</c> (16-bit mono at the sample's rate) and a <c>metadata.ini</c>
/// describing wave type, rate and loop. PCM8, PCM16 and IMA-ADPCM wave types decode; anything the
/// reader cannot handle falls back to <c>FULL.swav</c> only.
/// <para>
/// Creatable from a single mono WAV (encoded losslessly as PCM16) or a <c>FULL.swav</c> passthrough.
/// </para>
/// </summary>
public sealed class SwavFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  public string Id => "Swav";
  public string DisplayName => "SWAV (Nintendo DS sample)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".swav";
  public IReadOnlyList<string> Extensions => [".swav"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SWAV"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SWAV (Nintendo DS sample); full file + decoded mono WAV + metadata.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: FULL passthrough OR mono WAV → PCM16 SWAV ──

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.swav", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("SWAV archive create needs either FULL.swav or one mono WAV.");

    var wav = new WavReader().Read(wavInput.Data);
    if (wav.NumChannels != 1 || wav.BitsPerSample != 16)
      throw new InvalidOperationException("SWAV create requires a mono 16-bit WAV.");

    var pcm = new short[wav.InterleavedPcm.Length / 2];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.InterleavedPcm.AsSpan(i * 2));

    output.Write(new SwavWriter().Write(pcm, wav.SampleRate));
  }

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "SWAV archive accepts: FULL.swav, or one mono 16-bit WAV (MONO.wav)";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/') ?? "";
    if (dir == "" && (name == "full.swav" || name.EndsWith(".wav"))) { reason = null; return true; }
    reason = $"not a SWAV-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.swav", "Container", blob),
    };

    try {
      var parsed = new SwavReader().Read(blob);
      var pcm = SwavReader.ShortsToLe(parsed.Pcm);
      var wav = PcmCodec.ToWavBlob(pcm, channels: 1, parsed.SampleRate, bitsPerSample: 16);
      entries.Add(new("MONO.wav", "Channel", wav, "pcm"));
      entries.Add(new("metadata.ini", "Tag", BuildMetadata(parsed)));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                   or IndexOutOfRangeException or ArgumentOutOfRangeException) {
      // Unparseable / unsupported SWAV: surface FULL only.
    }

    return entries;
  }

  internal static byte[] BuildMetadata(SwavReader.ParsedSwav parsed) {
    var typeName = parsed.WaveType switch {
      0 => "PCM8",
      1 => "PCM16",
      2 => "IMA-ADPCM",
      _ => $"unknown({parsed.WaveType})",
    };
    var sb = new StringBuilder();
    sb.Append("[swav]\n");
    sb.Append(CultureInfo.InvariantCulture, $"waveType={typeName}\n");
    sb.Append(CultureInfo.InvariantCulture, $"sampleRate={parsed.SampleRate}\n");
    sb.Append(CultureInfo.InvariantCulture, $"samples={parsed.Pcm.Length}\n");
    sb.Append(CultureInfo.InvariantCulture, $"loop={(parsed.Loop ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"loopOffset={parsed.LoopOffset}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

}
