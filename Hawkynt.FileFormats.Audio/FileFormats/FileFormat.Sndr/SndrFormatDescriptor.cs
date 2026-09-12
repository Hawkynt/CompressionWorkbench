#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Sndr;

/// <summary>
/// Exposes a PC "Sounder" <c>.sndr</c> file as an archive of <c>FULL.sndr</c>,
/// <c>MONO.wav</c> and <c>metadata.ini</c>, and assembles one back from a mono 8-bit
/// WAV. Sounder is a minimal mono 8-bit-unsigned format with an 8-byte little-endian
/// header:
/// <list type="bullet">
///   <item><c>u16</c> mode/format word (0).</item>
///   <item><c>u16</c> sample rate in hertz.</item>
///   <item><c>u16</c> volume / playback flag (not surfaced as audio).</item>
///   <item><c>u16</c> reserved.</item>
/// </list>
/// The 8-bit unsigned sample data follows. The format carries no magic, so it is
/// reached by its <c>.sndr</c> extension or explicit lookup.
/// </summary>
public sealed class SndrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Defines the header size constant value.
  /// </summary>
  public const int HeaderSize = 8;
  private const int DefaultRate = 8000;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Sndr";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Sounder (PC)";
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
  public string DefaultExtension => ".sndr";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".sndr"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
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
  public string Description => "Sounder (PC) mono 8-bit sampled sound; full file + MONO.wav.";

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

  // ── IArchiveCreatable: assemble a Sounder file from a mono 8-bit WAV ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.sndr", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("Sounder create needs FULL.sndr or a mono WAV.");

    var wav = new WavReader().ReadCanonicalPcm(wavInput.Data);
    if (wav.NumChannels != 1)
      throw new InvalidOperationException("Sounder is mono; supply a single-channel WAV.");

    var samples8 = To8BitUnsigned(wav.InterleavedPcm, wav.BitsPerSample);
    var file = new byte[HeaderSize + samples8.Length];
    var s = file.AsSpan();
    BinaryPrimitives.WriteUInt16LittleEndian(s, 0);                       // mode/format
    BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)Math.Clamp(wav.SampleRate, 0, 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0);                  // volume
    BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 0);                  // reserved
    samples8.CopyTo(s[HeaderSize..]);
    output.Write(file);
  }

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription => "Sounder archive accepts: FULL.sndr or one mono WAV.";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.sndr" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not a Sounder-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    if (blob.Length < HeaderSize)
      throw new InvalidDataException("Sounder file too short for an 8-byte header.");

    var rate = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(2));
    if (rate == 0) rate = DefaultRate;
    var sampleData = blob.AsSpan(HeaderSize).ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sndr", "Container", blob),
    };
    if (sampleData.Length > 0)
      entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(sampleData, 1, rate, 8)));

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={rate}");
    info.AppendLine("channels=1");
    info.AppendLine("bits=8");
    info.AppendLine($"samples={sampleData.Length}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  /// <summary>WAV PCM (8-bit unsigned / 16-bit signed) → 8-bit unsigned.</summary>
  private static byte[] To8BitUnsigned(byte[] pcm, int bitsPerSample) {
    if (bitsPerSample == 8) return (byte[])pcm.Clone();
    if (bitsPerSample != 16)
      throw new InvalidOperationException("Sounder accepts 8-bit or 16-bit mono WAVs.");
    var r = new byte[pcm.Length / 2];
    for (var i = 0; i < r.Length; ++i) {
      var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
      r[i] = (byte)((sample >> 8) + 128);
    }
    return r;
  }
}
