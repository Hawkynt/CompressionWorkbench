#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Sndt;

/// <summary>
/// Exposes a "SoundTool" <c>.sndt</c> file as an archive of <c>FULL.sndt</c>,
/// <c>MONO.wav</c> and <c>metadata.ini</c>, and assembles one back from a mono 8-bit
/// WAV. SoundTool is a mono 8-bit-unsigned format identified by the ASCII magic
/// <c>SOUND</c> followed by the 0x1A (EOF) byte. The 18-byte little-endian header is:
/// <list type="bullet">
///   <item><c>char[5]</c> magic <c>"SOUND"</c>.</item>
///   <item><c>u8</c> 0x1A terminator.</item>
///   <item><c>u16</c> reserved / padding.</item>
///   <item><c>u32</c> sample-data length in bytes (at offset 8).</item>
///   <item><c>u32</c> sample rate in hertz (at offset 12, sanity-clamped to
///     4000..96000; otherwise the default 8000 Hz is assumed).</item>
///   <item><c>u16</c> bits-per-sample hint (at offset 16; only 8-bit data is decoded).</item>
/// </list>
/// The 8-bit unsigned sample data follows the header.
/// </summary>
public sealed class SndtFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

    /// <summary>
  /// Defines the header size constant value.
  /// </summary>
public const int HeaderSize = 18;
  private static readonly byte[] Magic = [(byte)'S', (byte)'O', (byte)'U', (byte)'N', (byte)'D', 0x1A];
  private const int MinRate = 4000;
  private const int MaxRate = 96000;
  private const int DefaultRate = 8000;

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Sndt";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "SoundTool";
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
public string DefaultExtension => ".sndt";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".sndt"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Magic, Confidence: 0.90),
  ];
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
public string Description => "SoundTool mono 8-bit sampled sound; full file + MONO.wav.";

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

  // ── IArchiveCreatable: assemble a SoundTool file from a mono 8-bit WAV ──

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.sndt", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("SoundTool create needs FULL.sndt or a mono WAV.");

    var wav = new WavReader().Read(wavInput.Data);
    if (wav.NumChannels != 1)
      throw new InvalidOperationException("SoundTool is mono; supply a single-channel WAV.");

    var samples8 = To8BitUnsigned(wav.InterleavedPcm, wav.BitsPerSample);
    var rate = Math.Clamp(wav.SampleRate, MinRate, MaxRate);
    var file = new byte[HeaderSize + samples8.Length];
    var s = file.AsSpan();
    Magic.CopyTo(s);
    BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 0);                   // reserved
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)samples8.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)rate);
    BinaryPrimitives.WriteUInt16LittleEndian(s[16..], 8);                  // bits
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
public string AcceptedInputsDescription => "SoundTool archive accepts: FULL.sndt or one mono WAV.";

    /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.sndt" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not a SoundTool-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    if (blob.Length < HeaderSize || !blob.AsSpan(0, Magic.Length).SequenceEqual(Magic))
      throw new InvalidDataException("Not a SoundTool file (missing 'SOUND'\\x1A magic).");

    var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8));
    var rawRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12));
    var rate = rawRate is >= MinRate and <= MaxRate ? rawRate : DefaultRate;

    var available = blob.Length - HeaderSize;
    var count = declaredLength > 0 && declaredLength <= (uint)available ? (int)declaredLength : available;
    var sampleData = blob.AsSpan(HeaderSize, count).ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sndt", "Container", blob),
    };
    if (sampleData.Length > 0)
      entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(sampleData, 1, rate, 8)));

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={rate}");
    if (rawRate != rate) info.AppendLine($"sample_rate_raw={rawRate} (out of range; defaulted)");
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
      throw new InvalidOperationException("SoundTool accepts 8-bit or 16-bit mono WAVs.");
    var r = new byte[pcm.Length / 2];
    for (var i = 0; i < r.Length; ++i) {
      var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
      r[i] = (byte)((sample >> 8) + 128);
    }
    return r;
  }
}
