#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.RoqDpcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Roq;

/// <summary>
/// id Software RoQ (<c>.roq</c>) multimedia container — the cinematics format of
/// Quake III and the id Tech 3 engine. A RoQ file is a stream of chunks, each a
/// <c>u16 id | u32 size | u16 arg</c> header followed by <c>size</c> payload bytes.
/// Audio lives in two chunk types: <c>0x1020</c> (mono) and <c>0x1021</c> (stereo),
/// both RoQ square-table DPCM at 22050 Hz, with the initial predictor(s) carried in
/// <c>arg</c>. Video chunks (<c>0x1001</c> info, <c>0x1002</c> codebook,
/// <c>0x1011</c> VQ frame) are counted but not decoded here.
/// <para>
/// Surfaced as a pseudo-archive: <c>FULL.roq</c> (Container), the concatenated decoded
/// sound as <c>MONO.wav</c> or <c>LEFT.wav</c>/<c>RIGHT.wav</c> (Channel) and a
/// <c>metadata.ini</c> (Tag) reporting the chunk inventory. Authoring encodes a WAV
/// to one RoQ sound chunk via <see cref="RoqDpcmCodec"/>.
/// </para>
/// </summary>
public sealed class RoqFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Roq";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "id Software RoQ";
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
  public string DefaultExtension => ".roq";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".roq"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  // RoQ file signature: 0x1084 0xFFFFFFFF (id 0x1084, size 0xFFFFFFFF).
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x84, 0x10, 0xFF, 0xFF, 0xFF, 0xFF], Confidence: 0.9),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("roq-dpcm", "RoQ DPCM")];
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
  public string Description => "id Software RoQ; square-table DPCM audio, full file + decoded WAV channels.";

  private const ushort ChunkSoundMono = 0x1020;
  private const ushort ChunkSoundStereo = 0x1021;
  private const ushort ChunkInfo = 0x1001;
  private const ushort ChunkCodebook = 0x1002;
  private const ushort ChunkVqFrame = 0x1011;

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

  // ── IArchiveCreatable: WAV → RoQ sound chunk ────────────────────────────────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.roq", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wavInput = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wavInput.Data == null)
      throw new InvalidOperationException("RoQ archive create needs either FULL.roq or a WAV.");

    var wav = new WavReader().ReadCanonicalPcm(wavInput.Data);
    if (wav.NumChannels is not (1 or 2))
      throw new InvalidOperationException("RoQ create supports mono or stereo WAV.");
    if (wav.BitsPerSample != 16)
      throw new InvalidOperationException("RoQ create expects 16-bit PCM input.");

    var samples = LePcmToShorts(wav.InterleavedPcm);
    var stereo = wav.NumChannels == 2;
    var (payload, arg) = RoqDpcmCodec.Encode(samples, stereo);

    // File header: 0x1084 chunk with sentinel size 0xFFFFFFFF and arg 0x1E (frame rate).
    var fileHeader = new byte[8];
    BinaryPrimitives.WriteUInt16LittleEndian(fileHeader.AsSpan(0), 0x1084);
    BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.AsSpan(2), 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt16LittleEndian(fileHeader.AsSpan(6), 0x1E);
    output.Write(fileHeader);

    var chunkHeader = new byte[8];
    BinaryPrimitives.WriteUInt16LittleEndian(chunkHeader.AsSpan(0), stereo ? ChunkSoundStereo : ChunkSoundMono);
    BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.AsSpan(2), (uint)payload.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(chunkHeader.AsSpan(6), arg);
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
    "RoQ archive accepts: FULL.roq or a mono/stereo 16-bit WAV (encoded to RoQ DPCM)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.roq" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a RoQ-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── parsing ─────────────────────────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = ParseRoq(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.roq", "Container", blob),
    };

    if (parsed.Channels >= 1 && parsed.Samples.Length > 0) {
      var pcm = ShortsToLePcm(parsed.Samples);
      if (parsed.Channels == 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm, channels: 1, RoqDpcmCodec.SampleRate, bitsPerSample: 16, formatCode: 1), "roq-dpcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, parsed.Channels, RoqDpcmCodec.SampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "roq-dpcm"));
      }
    }

    var info = new StringBuilder();
    info.AppendLine($"sample_rate={RoqDpcmCodec.SampleRate}");
    info.AppendLine($"channels={parsed.Channels}");
    info.AppendLine($"sound_chunks={parsed.SoundChunks}");
    info.AppendLine($"video_chunks={parsed.VideoChunks}");
    info.AppendLine($"total_chunks={parsed.TotalChunks}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private readonly record struct ParsedRoq(
    int Channels, short[] Samples, int SoundChunks, int VideoChunks, int TotalChunks);

  private static ParsedRoq ParseRoq(byte[] blob) {
    if (blob.Length < 8)
      throw new InvalidDataException("RoQ too short for the file header.");

    // Skip the 8-byte file signature chunk (id 0x1084, size 0xFFFFFFFF, arg = frame rate).
    var pos = 8;
    var mono = new List<short>();
    var leftRight = new List<short>();
    var channels = 0;
    int soundChunks = 0, videoChunks = 0, totalChunks = 0;

    while (pos + 8 <= blob.Length) {
      var id = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos + 2));
      var arg = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos + 6));
      pos += 8;
      ++totalChunks;
      if (size > (uint)(blob.Length - pos))
        size = (uint)(blob.Length - pos); // tolerate truncation
      var payload = blob.AsSpan(pos, (int)size);
      pos += (int)size;

      switch (id) {
        case ChunkSoundMono:
          ++soundChunks;
          if (channels == 0) channels = 1;
          mono.AddRange(RoqDpcmCodec.Decode(payload, arg, stereo: false));
          break;
        case ChunkSoundStereo:
          ++soundChunks;
          channels = 2;
          leftRight.AddRange(RoqDpcmCodec.Decode(payload, arg, stereo: true));
          break;
        case ChunkInfo:
        case ChunkCodebook:
        case ChunkVqFrame:
          ++videoChunks;
          break;
        default:
          // Unknown/other chunk types are simply skipped (counted in the total).
          break;
      }
    }

    var samples = channels == 2 ? leftRight.ToArray() : mono.ToArray();
    return new ParsedRoq(channels, samples, soundChunks, videoChunks, totalChunks);
  }

  private static short[] LePcmToShorts(byte[] pcm) {
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return samples;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
