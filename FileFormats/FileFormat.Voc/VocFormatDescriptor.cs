#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Voc;

/// <summary>
/// Exposes a Creative Voice File (<c>.voc</c>) as an archive of <c>FULL.voc</c>
/// (Kind <c>Track</c>), one mono <c>&lt;CHANNEL&gt;.wav</c> per decoded channel
/// (Kind <c>Channel</c>), a <c>metadata.ini</c> with the stream geometry/codec and
/// a <c>metadata/text.txt</c> for any embedded ASCII text blocks. Creation assembles
/// a fresh VOC from per-channel mono WAV inputs (or passes through a supplied
/// <c>FULL.voc</c>).
/// </summary>
public sealed class VocFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Voc";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Creative Voice (.voc)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".voc";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".voc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("Creative Voice File"u8.ToArray(), Confidence: 0.95),
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
  public string Description => "Creative Voice (.voc) audio; full file + per-channel PCM + text/metadata.";

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

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "VOC archive accepts: FULL.voc, LEFT/RIGHT/MONO/… .wav (per-channel), metadata/*.txt, metadata/*.bin";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";

    if (dir == "" && name == "full.voc") { reason = null; return true; }
    if (dir == "" && name.EndsWith(".wav")) { reason = null; return true; }
    if (dir == "metadata" && (name.EndsWith(".txt") || name.EndsWith(".bin"))) { reason = null; return true; }
    reason = $"not a VOC-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── IArchiveCreatable: assemble per-channel mono WAVs into one VOC ─────────

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a supplied FULL.voc verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.voc", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelOrder(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("VOC archive create needs either FULL.voc or one or more per-channel WAVs.");

    var channels = new List<WavReader.ParsedWav>();
    foreach (var (_, data) in channelBlobs) channels.Add(new WavReader().Read(data));

    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.BitsPerSample != first.BitsPerSample || c.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate + bit depth.");

    var bytesPerSample = first.BitsPerSample / 8;
    var frameCount = first.InterleavedPcm.Length / bytesPerSample;
    if (channels.Any(c => c.InterleavedPcm.Length / bytesPerSample != frameCount))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(c => c.InterleavedPcm).ToList(), first.BitsPerSample);

    WriteVoc(output, interleaved, channels.Count, first.SampleRate, first.BitsPerSample);
  }

  /// <summary>
  /// Writes a valid VOC: 26-byte header, a single type-9 sound block carrying the
  /// interleaved LE PCM at the given geometry, then a type-0 terminator.
  /// </summary>
  private static void WriteVoc(Stream output, byte[] interleaved, int channels, int sampleRate, int bitsPerSample) {
    Span<byte> header = stackalloc byte[26];
    "Creative Voice File"u8.CopyTo(header);
    header[19] = 0x1A;
    BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 0x001A);   // data-block offset
    const ushort version = 0x0114;                                   // v1.20
    BinaryPrimitives.WriteUInt16LittleEndian(header[22..], version);
    var checksum = (ushort)((0x1234 + (~version & 0xFFFF) + 1) & 0xFFFF);
    BinaryPrimitives.WriteUInt16LittleEndian(header[24..], checksum);
    output.Write(header);

    // Block type 9: uint32 rate | uint8 bits | uint8 channels | uint16 codec | uint32 reserved | samples
    var bodyLength = 12 + interleaved.Length;
    Span<byte> blockHeader = stackalloc byte[4];
    blockHeader[0] = 9;
    blockHeader[1] = (byte)(bodyLength & 0xFF);
    blockHeader[2] = (byte)((bodyLength >> 8) & 0xFF);
    blockHeader[3] = (byte)((bodyLength >> 16) & 0xFF);
    output.Write(blockHeader);

    Span<byte> blockBody = stackalloc byte[12];
    BinaryPrimitives.WriteUInt32LittleEndian(blockBody, (uint)sampleRate);
    blockBody[4] = (byte)bitsPerSample;
    blockBody[5] = (byte)channels;
    BinaryPrimitives.WriteUInt16LittleEndian(blockBody[6..], 0);      // codec 0 = PCM
    BinaryPrimitives.WriteUInt32LittleEndian(blockBody[8..], 0);      // reserved
    output.Write(blockBody);
    output.Write(interleaved);

    output.WriteByte(0); // terminator
  }

  // Canonical speaker ordering (FFmpeg/WAVE bit order, mono through 22.2).
  private static int ChannelOrder(string name) => ChannelLayout.OrderIndex(name);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new VocReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.voc", "Container", blob),
    };

    if (parsed.InterleavedPcm != null && parsed.BitsPerSample is 8 or 16 or 24 or 32 && parsed.NumChannels >= 1) {
      if (parsed.NumChannels == 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(parsed.InterleavedPcm, 1, parsed.SampleRate, parsed.BitsPerSample, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
            parsed.InterleavedPcm, parsed.NumChannels, parsed.SampleRate, parsed.BitsPerSample))
          entries.Add(new($"{name}.wav", "Channel", wavBlob, "pcm"));
      }
    }

    var info = new StringBuilder();
    info.AppendLine($"codec={parsed.Codec} ({CodecName(parsed.Codec)})");
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"channels={parsed.NumChannels}");
    info.AppendLine($"bits_per_sample={parsed.BitsPerSample}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    if (parsed.TextBlocks.Count > 0)
      entries.Add(new("metadata/text.txt", "Tag",
        Encoding.UTF8.GetBytes(string.Join("\n", parsed.TextBlocks))));

    return entries;
  }

  private static string CodecName(int codec) => codec switch {
    0 => "8-bit unsigned PCM",
    1 => "4-bit Creative ADPCM",
    2 => "2.6-bit Creative ADPCM",
    3 => "2-bit Creative ADPCM",
    4 => "16-bit signed PCM",
    6 => "A-law",
    7 => "u-law",
    _ => $"unknown ({codec})",
  };
}
