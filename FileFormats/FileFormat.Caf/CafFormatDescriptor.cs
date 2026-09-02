#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Caf;

/// <summary>
/// Exposes an Apple Core Audio Format (<c>.caf</c>) file as an archive of
/// <c>FULL.caf</c> plus one mono WAV per channel (for LPCM integer audio, or the
/// G.711 <c>ulaw</c>/<c>alaw</c> companded formats decoded to 16-bit PCM) plus any
/// ancillary chunks (<c>info</c>, <c>chan</c>, <c>free</c>, …) as
/// <c>metadata/&lt;type&gt;.bin</c>. Float LPCM and other compressed formats
/// (<c>ima4</c>, <c>aac </c>, …) are surfaced as <c>FULL.caf</c> only.
/// </summary>
public sealed class CafFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Caf";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "CAF (Core Audio Format)";
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
public string DefaultExtension => ".caf";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".caf"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("caff"u8.ToArray(), Confidence: 0.90),
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
public string Description => "CAF (Apple Core Audio Format); full file + per-channel LPCM + ancillary chunks.";

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

  // ── IArchiveCreatable: assemble a CAF from per-channel mono WAVs ──────────────

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    // Passthrough a provided FULL.caf verbatim (archive-view semantics).
    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.caf", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("FULL.caf", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelOrder(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("CAF archive create needs either FULL.caf or one or more per-channel WAVs.");

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

    WriteCaf(output, channels.Count, first.SampleRate, first.BitsPerSample, interleaved);
  }

  /// <summary>
  /// Writes a valid LPCM CAF. The little-endian flag is set in <c>mFormatFlags</c> so the
  /// interleaved little-endian PCM (the canonical buffer used throughout this codebase) can
  /// be written verbatim without byte-swapping; <see cref="CafReader"/> honours that flag.
  /// </summary>
  private static void WriteCaf(Stream output, int channels, int sampleRate, int bitsPerChannel, byte[] interleaved) {
    Span<byte> hdr = stackalloc byte[8];
    "caff"u8.CopyTo(hdr);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], 1); // mFileVersion
    BinaryPrimitives.WriteUInt16BigEndian(hdr[6..], 0); // mFileFlags
    output.Write(hdr);

    var bytesPerFrame = (uint)(channels * bitsPerChannel / 8);
    var desc = new byte[32];
    BinaryPrimitives.WriteDoubleBigEndian(desc.AsSpan(0), sampleRate);
    "lpcm"u8.CopyTo(desc.AsSpan(8));
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(12), FlagIsLittleEndian); // integer, little-endian samples
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(16), bytesPerFrame);      // mBytesPerPacket
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(20), 1);                  // mFramesPerPacket
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(24), (uint)channels);     // mChannelsPerFrame
    BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(28), (uint)bitsPerChannel); // mBitsPerChannel
    WriteChunk(output, "desc", desc);

    var dataBody = new byte[4 + interleaved.Length]; // 4-byte mEditCount (0) + audio
    interleaved.CopyTo(dataBody.AsSpan(4));
    WriteChunk(output, "data", dataBody);
  }

  private const uint FlagIsLittleEndian = 0x2;

  private static void WriteChunk(Stream s, string type, byte[] body) {
    Span<byte> head = stackalloc byte[12];
    Encoding.ASCII.GetBytes(type).CopyTo(head);
    BinaryPrimitives.WriteInt64BigEndian(head[4..], body.Length);
    s.Write(head);
    s.Write(body);
  }

  // Canonical speaker ordering (FFmpeg/WAVE bit order, mono through 22.2).
  private static int ChannelOrder(string name) => ChannelLayout.OrderIndex(name);

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

    /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
    /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "CAF archive accepts: FULL.caf, LEFT/RIGHT/CENTER/… .wav (per-channel), metadata/*.bin";

    /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";

    if (dir == "" && (name == "full.caf" || name.EndsWith(".wav"))) { reason = null; return true; }
    if (dir == "metadata" && name.EndsWith(".bin")) { reason = null; return true; }
    reason = $"not a CAF-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new CafReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.caf", "Container", blob),
    };

    // Split integer LPCM per-channel; float and non-LPCM are surfaced as FULL only.
    if (!parsed.IsFloat &&
        parsed.FormatId == "lpcm" &&
        parsed.BitsPerSample is 8 or 16 or 24 or 32 &&
        parsed.NumChannels >= 1 &&
        parsed.InterleavedPcm.Length > 0) {
      foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
          parsed.InterleavedPcm, parsed.NumChannels, parsed.SampleRate, parsed.BitsPerSample,
          parsed.ChannelMask))
        entries.Add(new($"{name}.wav", "Channel", wavBlob, "pcm"));
    }

    foreach (var (type, data) in parsed.OtherChunks)
      entries.Add(new($"metadata/{type.Trim()}.bin", "Tag", data));

    return entries;
  }
}
