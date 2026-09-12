#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.WsAdpcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Aud;

/// <summary>
/// Westwood Studios <c>.aud</c> audio (Command &amp; Conquer-era games). The format has no
/// reliable magic, so detection rests on header-field validation. The little-endian
/// header is <c>u16 sampleRate | u32 dataSize | u32 outputSize | u8 flags | u8 codec</c>:
/// <list type="bullet">
///   <item><c>flags</c> bit 0 = stereo, bit 1 = 16-bit;</item>
///   <item><c>codec</c> 1 = Westwood WS-ADPCM, 99 = standard IMA-ADPCM.</item>
/// </list>
/// The body is a sequence of chunks, each <c>u16 inSize | u16 outSize | u32 magic
/// 0x0000DEAF | payload[inSize]</c>. WS-ADPCM chunks decode through
/// <see cref="WsAdpcmCodec"/>; IMA chunks run a single continuous predictor across all
/// chunks via <see cref="StandardImaCodec"/> (low nibble first).
/// <para>
/// Surfaced as a pseudo-archive: <c>FULL.aud</c> (Container), one mono <c>MONO.wav</c>
/// or <c>LEFT.wav</c>/<c>RIGHT.wav</c> (Channel) and <c>metadata.ini</c> (Tag). Authoring
/// writes IMA chunks (codec 99) with the 0xDEAF magic, round-tripping back through this
/// reader within IMA's lossy tolerance.
/// </para>
/// </summary>
public sealed class AudFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable, IFormatValidator {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Aud";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Westwood AUD (Command & Conquer)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".aud";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".aud"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  // No reliable magic: detection is extension-only, with deep header validation below.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ws-adpcm", "WS-ADPCM"), new("ima-adpcm", "IMA-ADPCM")];
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
  public string Description => "Westwood AUD (Command & Conquer); WS-ADPCM / IMA-ADPCM, full file + decoded WAV channels.";

  private const int HeaderSize = 12;
  private const uint ChunkMagic = 0x0000DEAF;
  private const byte CodecWsAdpcm = 1;
  private const byte CodecImaAdpcm = 99;

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

  // ── IFormatValidator: header-field validation in lieu of magic ──────────────

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var issues = new List<ValidationIssue>();
    if (header.Length < HeaderSize)
      return Fail(issues, "AUD_SHORT", "File shorter than the 12-byte AUD header.");

    var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(header);
    var codec = header[11];
    if (sampleRate is < 4000 or > 48000)
      return Fail(issues, "AUD_RATE", $"Implausible sample rate {sampleRate} Hz.");
    if (codec is not (CodecWsAdpcm or CodecImaAdpcm))
      return Fail(issues, "AUD_CODEC", $"Unknown AUD codec id {codec}.");

    return new ValidationResult {
      IsValid = true, Confidence = 0.8, Health = FormatHealth.Good,
      Level = ValidationLevel.Header, Issues = issues,
    };
  }

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
  public ValidationResult ValidateStructure(Stream stream) {
    try {
      var entries = BuildEntries(stream);
      return new ValidationResult {
        IsValid = true, Confidence = 0.9, Health = FormatHealth.Good,
        Level = ValidationLevel.Structure, Issues = [],
        TotalEntries = entries.Count, ValidEntries = entries.Count,
      };
    } catch (Exception e) {
      return new ValidationResult {
        IsValid = false, Confidence = 0.0, Health = FormatHealth.Damaged,
        Level = ValidationLevel.Structure,
        Issues = [new(ValidationLevel.Structure, IssueSeverity.Error, "AUD_PARSE", e.Message)],
      };
    }
  }

  /// <summary>
  /// Validates the supplied data.
  /// </summary>
  public ValidationResult ValidateIntegrity(Stream stream) => ValidateStructure(stream);

  private static ValidationResult Fail(List<ValidationIssue> issues, string code, string description) {
    issues.Add(new(ValidationLevel.Header, IssueSeverity.Error, code, description));
    return new ValidationResult {
      IsValid = false, Confidence = 0.0, Health = FormatHealth.Damaged,
      Level = ValidationLevel.Header, Issues = issues,
    };
  }

  // ── IArchiveCreatable: WAV → IMA-encoded AUD (codec 99) ─────────────────────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.aud", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("AUD archive create needs either FULL.aud or a WAV.");

    var wav = new WavReader().ReadCanonicalPcm(wavInput.Data);
    if (wav.NumChannels is not (1 or 2))
      throw new InvalidOperationException("AUD create supports mono or stereo WAV.");
    if (wav.BitsPerSample != 16)
      throw new InvalidOperationException("AUD create expects 16-bit PCM input.");

    var samples = LePcmToShorts(wav.InterleavedPcm);
    WriteAud(output, samples, wav.SampleRate, wav.NumChannels);
  }

  // Writes an IMA-ADPCM (codec 99) AUD: a single chunk over the whole (interleaved) stream.
  private static void WriteAud(Stream output, short[] interleaved, int sampleRate, int channels) {
    var state = new StandardImaCodec.State(0, 0);
    var payload = StandardImaCodec.Encode(interleaved, ref state);

    var header = new byte[HeaderSize];
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0), (ushort)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), (uint)payload.Length); // dataSize
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6), (uint)(interleaved.Length * 2)); // outputSize bytes
    header[10] = (byte)((channels == 2 ? 0x01 : 0x00) | 0x02); // stereo bit | 16-bit bit
    header[11] = CodecImaAdpcm;
    output.Write(header);

    var chunkHeader = new byte[8];
    BinaryPrimitives.WriteUInt16LittleEndian(chunkHeader.AsSpan(0), (ushort)payload.Length);          // inSize
    BinaryPrimitives.WriteUInt16LittleEndian(chunkHeader.AsSpan(2), (ushort)(interleaved.Length * 2)); // outSize bytes
    BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.AsSpan(4), ChunkMagic);
    output.Write(chunkHeader);
    output.Write(payload);
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
    "AUD archive accepts: FULL.aud or a mono/stereo 16-bit WAV (encoded to IMA-ADPCM)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.aud" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not an AUD-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── parsing ─────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = ParseAud(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.aud", "Container", blob),
    };

    var method = parsed.Codec == CodecWsAdpcm ? "ws-adpcm" : "ima-adpcm";
    var pcm = WsAdpcmCodec.ShortsToLePcm(parsed.Samples);

    if (parsed.Channels == 1) {
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(pcm, channels: 1, parsed.SampleRate, bitsPerSample: 16, formatCode: 1), method));
    } else {
      foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, parsed.Channels, parsed.SampleRate, 16))
        entries.Add(new($"{name}.wav", "Channel", wav, method));
    }

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"channels={parsed.Channels}");
    info.AppendLine($"codec={(parsed.Codec == CodecWsAdpcm ? "ws-adpcm" : "ima-adpcm")}");
    info.AppendLine($"bits_per_sample={parsed.BitsPerSample}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private readonly record struct ParsedAud(int SampleRate, int Channels, int BitsPerSample, byte Codec, short[] Samples);

  private static ParsedAud ParseAud(byte[] blob) {
    if (blob.Length < HeaderSize)
      throw new InvalidDataException("AUD too short for 12-byte header.");

    var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0));
    var flags = blob[10];
    var codec = blob[11];
    var channels = (flags & 0x01) != 0 ? 2 : 1;
    var bits = (flags & 0x02) != 0 ? 16 : 8;
    if (codec is not (CodecWsAdpcm or CodecImaAdpcm))
      throw new InvalidDataException($"Unknown AUD codec id {codec}.");

    // Decode every chunk into a single 16-bit PCM stream. The IMA decoder keeps one
    // continuous predictor across chunk boundaries; WS resets per chunk by construction.
    var pcm = new List<short>();
    var imaState = new StandardImaCodec.State(0, 0);
    var pos = HeaderSize;
    while (pos + 8 <= blob.Length) {
      var inSize = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos));
      var outSize = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos + 2));
      var magic = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 4));
      pos += 8;
      if (magic != ChunkMagic)
        throw new InvalidDataException($"Bad AUD chunk magic 0x{magic:X8} at offset {pos - 8}.");
      if (pos + inSize > blob.Length)
        throw new InvalidDataException("AUD chunk runs past end of file.");

      var payload = blob.AsSpan(pos, inSize);
      pos += inSize;

      if (codec == CodecWsAdpcm) {
        var decoded8 = WsAdpcmCodec.Decode(payload, outSize);
        pcm.AddRange(WsAdpcmCodec.ToPcm16(decoded8));
      } else {
        // outSize is in output bytes (2 per 16-bit sample); IMA yields 2 samples/byte.
        pcm.AddRange(StandardImaCodec.Decode(payload, ref imaState));
      }
    }

    return new ParsedAud(sampleRate <= 0 ? 22050 : sampleRate, channels, bits, codec, pcm.ToArray());
  }

  private static short[] LePcmToShorts(byte[] pcm) {
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return samples;
  }
}
