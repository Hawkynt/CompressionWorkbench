#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.SpuAdpcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Vag;

/// <summary>
/// Exposes a Sony <c>.vag</c> file (PS1/PS2 SPU-ADPCM, "VAGp" container) as an archive of
/// <c>FULL.vag</c>, one decoded mono PCM <c>MONO.wav</c>, and a <c>metadata.ini</c> carrying
/// the stream name, sample rate and version. VAG is a mono container — stereo content is
/// distributed as two files or interleaved variants and is treated here as a single mono
/// stream.
/// <para>The header is BIG-endian:
/// <c>magic "VAGp" | u32 version | u32 reserved | u32 dataSize | u32 sampleRate |
/// 12 reserved bytes | char name[16] @0x20 | data @0x30</c>.</para>
/// </summary>
public sealed class VagFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Vag";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Sony VAG (PS1/PS2 SPU-ADPCM)";
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
  public string DefaultExtension => ".vag";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".vag"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("VAGp"u8.ToArray(), Confidence: 0.95),
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
  public string Description => "Sony VAG (PS1/PS2 SPU-ADPCM); full file + decoded mono WAV.";

  private const int HeaderSize = 0x30;
  private const int NameOffset = 0x20;
  private const int NameLength = 16;

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

  // ── IArchiveCreatable: passthrough FULL.vag, or encode a mono WAV → VAGp ──────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a provided FULL.vag verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.vag", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("VAG archive create needs either FULL.vag or a mono WAV.");

    var wav = new WavReader().ReadCanonicalPcm(wavInput.Data);
    if (wav.NumChannels != 1)
      throw new InvalidOperationException("VAG is mono; supply a single-channel WAV.");
    if (wav.BitsPerSample != 16)
      throw new InvalidOperationException("VAG create expects 16-bit PCM input.");

    var samples = LePcmToShorts(wav.InterleavedPcm);
    var adpcm = SpuAdpcmCodec.Encode(samples);

    var name = Path.GetFileNameWithoutExtension(wavInput.Name);
    WriteVag(output, adpcm, wav.SampleRate, name);
  }

  private static void WriteVag(Stream output, byte[] adpcm, int sampleRate, string name) {
    var header = new byte[HeaderSize];
    "VAGp"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 0x20);                  // version
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0);                     // reserved
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)adpcm.Length);   // dataSize
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)sampleRate);     // sampleRate
    // 12 reserved bytes at 0x14..0x1F stay zero.

    var nameBytes = Encoding.ASCII.GetBytes(name ?? string.Empty);
    var copy = Math.Min(nameBytes.Length, NameLength - 1); // keep the field NUL-terminated
    Array.Copy(nameBytes, 0, header, NameOffset, copy);

    output.Write(header);
    output.Write(adpcm);
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
    "VAG archive accepts: FULL.vag or a mono 16-bit WAV (encoded to SPU-ADPCM)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.vag" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a VAG-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = ParseVag(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.vag", "Container", blob),
    };

    var samples = SpuAdpcmCodec.Decode(parsed.AdpcmData);
    var pcm = ShortsToLePcm(samples);
    entries.Add(new("MONO.wav", "Channel",
      PcmCodec.ToWavBlob(pcm, channels: 1, parsed.SampleRate, bitsPerSample: 16, formatCode: 1), "spu-adpcm"));

    var info = new StringBuilder();
    info.AppendLine($"name={parsed.Name}");
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"version={parsed.Version}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private readonly record struct ParsedVag(uint Version, int SampleRate, string Name, byte[] AdpcmData);

  private static ParsedVag ParseVag(byte[] blob) {
    if (blob.Length < HeaderSize)
      throw new InvalidDataException("VAG too short for 0x30-byte header.");
    if (blob[0] != 'V' || blob[1] != 'A' || blob[2] != 'G' || blob[3] != 'p')
      throw new InvalidDataException("Missing VAGp magic.");

    var version = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(4));
    var dataSize = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(12));
    var sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(16));

    var nameSpan = blob.AsSpan(NameOffset, NameLength);
    var nameLen = nameSpan.IndexOf((byte)0);
    if (nameLen < 0) nameLen = NameLength;
    var name = Encoding.ASCII.GetString(nameSpan[..nameLen]);

    var available = blob.Length - HeaderSize;
    var dataLen = available;
    if (dataSize > 0 && dataSize <= available)
      dataLen = (int)dataSize;
    var adpcm = blob.AsSpan(HeaderSize, dataLen).ToArray();

    return new ParsedVag(version, sampleRate <= 0 ? 44100 : sampleRate, name, adpcm);
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
