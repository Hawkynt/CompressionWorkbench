#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.SolDpcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Sol;

/// <summary>
/// Sierra SOL (<c>.sol</c>) sound effects, as parsed by FFmpeg's <c>sol.c</c>. The
/// little-endian header is <c>u32 magic | u16 sampleRate | u8 type</c>, where the
/// magic is one of <c>0x0B8D</c>, <c>0x0C0D</c>, <c>0x0C8D</c> and the
/// <c>type</c> byte's low bits are flags: bit 0 = 16-bit, bit 1 = stereo,
/// bit 2 = DPCM. The payload is therefore one of:
/// <list type="bullet">
///   <item>8-bit unsigned PCM (no flags),</item>
///   <item>16-bit signed LE PCM (bit 0),</item>
///   <item>SOL DPCM (bit 2) — 8-bit old/new table or 16-bit integrate, decoded via
///     <see cref="SolDpcmCodec"/>.</item>
/// </list>
/// Surfaced as a pseudo-archive: <c>FULL.sol</c> (Container), one mono <c>MONO.wav</c>
/// or <c>LEFT.wav</c>/<c>RIGHT.wav</c> (Channel) and <c>metadata.ini</c> (Tag).
/// Authoring writes the 16-bit PCM variant (magic <c>0x0C8D</c>, type bit 0 set),
/// symmetric with the reader.
/// </summary>
public sealed class SolFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Sol";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Sierra SOL";
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
public string DefaultExtension => ".sol";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sol"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];

  // The three documented SOL magic words, little-endian (low byte first).
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x8D, 0x0B], Confidence: 0.85),
    new([0x0D, 0x0C], Confidence: 0.85),
    new([0x8D, 0x0C], Confidence: 0.85),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("pcm", "PCM"), new("sol-dpcm", "SOL DPCM")];
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
public string Description => "Sierra SOL; PCM / SOL-DPCM, full file + decoded WAV channels.";

  private const ushort Magic0B8D = 0x0B8D;
  private const ushort Magic0C0D = 0x0C0D;
  private const ushort Magic0C8D = 0x0C8D;
  private const int HeaderSize = 7; // u32 magic + u16 rate + u8 type

  private const byte FlagSixteenBit = 0x01;
  private const byte FlagStereo = 0x02;
  private const byte FlagDpcm = 0x04;

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

  // ── IArchiveCreatable: WAV → 16-bit PCM SOL (magic 0x0C8D) ──────────────────

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.sol", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("SOL archive create needs either FULL.sol or a WAV.");

    var wav = new WavReader().Read(wavInput.Data);
    if (wav.NumChannels is not (1 or 2))
      throw new InvalidOperationException("SOL create supports mono or stereo WAV.");
    if (wav.BitsPerSample != 16)
      throw new InvalidOperationException("SOL create expects 16-bit PCM input.");

    var type = (byte)(FlagSixteenBit | (wav.NumChannels == 2 ? FlagStereo : 0));
    var header = new byte[HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), Magic0C8D);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)wav.SampleRate);
    header[6] = type;
    output.Write(header);
    output.Write(wav.InterleavedPcm); // 16-bit LE PCM passthrough
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
    "SOL archive accepts: FULL.sol or a mono/stereo 16-bit WAV (written as 16-bit PCM SOL)";

    /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.sol" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a SOL-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── parsing ─────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = ParseSol(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sol", "Container", blob),
    };

    var pcm = ShortsToLePcm(parsed.Samples);
    if (parsed.Channels == 1) {
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(pcm, channels: 1, parsed.SampleRate, bitsPerSample: 16, formatCode: 1), parsed.Method));
    } else {
      foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, parsed.Channels, parsed.SampleRate, 16))
        entries.Add(new($"{name}.wav", "Channel", wav, parsed.Method));
    }

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"channels={parsed.Channels}");
    info.AppendLine($"coding={parsed.Method}");
    info.AppendLine($"magic=0x{parsed.Magic:X4}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private readonly record struct ParsedSol(
    ushort Magic, int SampleRate, int Channels, string Method, short[] Samples);

  private static ParsedSol ParseSol(byte[] blob) {
    if (blob.Length < HeaderSize)
      throw new InvalidDataException("SOL too short for 7-byte header.");

    var magic = (ushort)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0));
    if (magic is not (Magic0B8D or Magic0C0D or Magic0C8D))
      throw new InvalidDataException($"Unknown SOL magic 0x{magic:X4}.");

    var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(4));
    var type = blob[6];
    var sixteenBit = (type & FlagSixteenBit) != 0;
    var stereo = (type & FlagStereo) != 0;
    var dpcm = (type & FlagDpcm) != 0;
    var channels = stereo ? 2 : 1;

    var data = blob.AsSpan(HeaderSize);
    short[] samples;
    string method;

    if (dpcm) {
      // 0x0B8D historically selects the "old" 8-bit table; the others select "new".
      var mode = sixteenBit
        ? SolDpcmCodec.Mode.Sixteen
        : magic == Magic0B8D ? SolDpcmCodec.Mode.Old8 : SolDpcmCodec.Mode.New8;
      samples = SolDpcmCodec.Decode(data, mode);
      method = "sol-dpcm";
    } else if (sixteenBit) {
      samples = new short[data.Length / 2];
      for (var i = 0; i < samples.Length; ++i)
        samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
      method = "pcm";
    } else {
      samples = SolDpcmCodec.DecodePcm8(data);
      method = "pcm";
    }

    // Stereo interleaving is sample-by-sample for all variants; for stereo DPCM the
    // decoded stream is already L,R,L,R because the deltas alternate per nibble.
    return new ParsedSol(magic, sampleRate <= 0 ? 22050 : sampleRate, channels, method, samples);
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
