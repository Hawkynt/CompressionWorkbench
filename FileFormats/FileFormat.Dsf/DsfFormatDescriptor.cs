#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Dsf;

/// <summary>
/// Exposes a Sony DSD Stream File (<c>.dsf</c>) as an archive of <c>FULL.dsf</c> plus, per
/// channel, the raw 1-bit DSD bitstream (<c>&lt;NAME&gt;.dsd</c>) and a playable decimated
/// 16-bit mono PCM WAV at <c>samplingFrequency / 64</c> (<c>&lt;NAME&gt;.wav</c>), plus an
/// <c>metadata.ini</c> summary and any trailing ID3v2 tag as <c>metadata/id3.bin</c>. The WAV
/// is produced by <see cref="DsdDecimator"/>, a crude windowed accumulator (documented there
/// as an inspection-grade approximation, not a fidelity-preserving FIR decimator).
/// </summary>
public sealed class DsfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Dsf";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "DSF (DSD Stream File)";
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
  public string DefaultExtension => ".dsf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dsf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("DSD "u8.ToArray(), Confidence: 0.95),
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
  public string Description => "Sony DSD Stream File (1-bit DSD); full file + per-channel DSD bitstreams + decimated PCM.";

  private const int DefaultBlockSize = 4096;

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

  // ── IArchiveCreatable: passthrough FULL.dsf, or assemble from per-channel .dsd streams ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.dsf", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelStreams = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".dsd", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelStreams.Count == 0)
      throw new InvalidOperationException("DSF archive create needs either FULL.dsf or one or more per-channel .dsd streams.");

    var channels = channelStreams.Select(f => f.Data).ToList();
    var len = channels[0].Length;
    if (channels.Any(c => c.Length != len))
      throw new InvalidOperationException("All per-channel .dsd streams must have equal length.");

    var sampleRate = options.GetOptionInt("rate", 2822400);
    WriteDsf(output, channels, sampleRate);
  }

  /// <summary>
  /// Writes a valid v1 raw-DSD DSF: a 28-byte <c>DSD&#160;</c> header, a 52-byte <c>fmt&#160;</c>
  /// chunk and a <c>data</c> chunk whose payload interleaves each channel in
  /// <see cref="DefaultBlockSize"/>-byte blocks. The last block per channel is zero-padded to
  /// the block size so the stream round-trips bit-exact through <see cref="DsfReader"/> for the
  /// significant byte range. Bits within each byte stay LSB-first (standard DSF).
  /// </summary>
  private static void WriteDsf(Stream output, IReadOnlyList<byte[]> channels, int sampleRate) {
    var channelNum = channels.Count;
    var channelBytes = channels[0].Length;
    var sampleCount = (long)channelBytes * 8;
    var blocksPerChannel = (channelBytes + DefaultBlockSize - 1) / DefaultBlockSize;
    if (blocksPerChannel == 0) blocksPerChannel = 1;

    var payloadLen = (long)blocksPerChannel * DefaultBlockSize * channelNum;
    var dataChunkSize = 12 + payloadLen;
    long totalFileSize = 28 + 52 + dataChunkSize;

    Span<byte> dsdHdr = stackalloc byte[28];
    "DSD "u8.CopyTo(dsdHdr);
    BinaryPrimitives.WriteUInt64LittleEndian(dsdHdr[4..], 28);
    BinaryPrimitives.WriteUInt64LittleEndian(dsdHdr[12..], (ulong)totalFileSize);
    BinaryPrimitives.WriteUInt64LittleEndian(dsdHdr[20..], 0); // no metadata
    output.Write(dsdHdr);

    Span<byte> fmt = stackalloc byte[52];
    "fmt "u8.CopyTo(fmt);
    BinaryPrimitives.WriteUInt64LittleEndian(fmt[4..], 52);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[12..], 1);                       // formatVersion
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[16..], 0);                       // formatId (raw DSD)
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[20..], (uint)ChannelType(channelNum));
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[24..], (uint)channelNum);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[28..], (uint)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[32..], 1);                       // bitsPerSample
    BinaryPrimitives.WriteUInt64LittleEndian(fmt[36..], (ulong)sampleCount);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[44..], DefaultBlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[48..], 0);                       // reserved
    output.Write(fmt);

    Span<byte> dataHdr = stackalloc byte[12];
    "data"u8.CopyTo(dataHdr);
    BinaryPrimitives.WriteUInt64LittleEndian(dataHdr[4..], (ulong)dataChunkSize);
    output.Write(dataHdr);

    var block = new byte[DefaultBlockSize];
    for (var b = 0; b < blocksPerChannel; ++b) {
      var srcOffset = b * DefaultBlockSize;
      for (var c = 0; c < channelNum; ++c) {
        Array.Clear(block);
        var copy = Math.Min(DefaultBlockSize, channelBytes - srcOffset);
        if (copy > 0)
          Array.Copy(channels[c], srcOffset, block, 0, copy);
        output.Write(block);
      }
    }
  }

  /// <summary>Maps a channel count to the DSF <c>channelType</c> field (1=mono … 7=5.1).</summary>
  private static int ChannelType(int channelNum) => channelNum switch {
    1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 6 => 7, _ => channelNum,
  };

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "DSF archive accepts: FULL.dsf, LEFT/RIGHT/MONO/… .dsd (per-channel raw DSD)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";

    if (dir == "" && (name == "full.dsf" || name.EndsWith(".dsd"))) { reason = null; return true; }
    reason = $"not a DSF-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new DsfReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.dsf", "Container", blob),
    };

    // DSF bits are LSB-first within a byte for 1-bit DSD; 8-bit is treated MSB-first.
    var lsbFirst = parsed.BitsPerSample == 1;
    var names = ChannelLayout.DefaultNames(parsed.ChannelNum);
    for (var c = 0; c < parsed.ChannelNum; ++c) {
      var name = names[c];
      entries.Add(new($"{name}.dsd", "Stream", parsed.ChannelDsd[c]));
      var pcm = DsdDecimator.DecimateToPcm16(parsed.ChannelDsd[c], lsbFirst, parsed.SampleCount);
      var wav = PcmCodec.ToWavBlob(pcm, channels: 1, parsed.SampleRate / DsdDecimator.DecimationFactor, bitsPerSample: 16, formatCode: 1);
      entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
    }

    if (parsed.Id3 is { Length: > 0 })
      entries.Add(new("metadata/id3.bin", "Tag", parsed.Id3));

    entries.Add(new("metadata.ini", "Tag", BuildMetadataIni(parsed)));

    return entries;
  }

  private static byte[] BuildMetadataIni(DsfReader.ParsedDsf parsed) {
    var sb = new StringBuilder();
    sb.Append("[dsf]\n");
    sb.Append(CultureInfo.InvariantCulture, $"rate={parsed.SampleRate}\n");
    sb.Append(CultureInfo.InvariantCulture, $"channels={parsed.ChannelNum}\n");
    sb.Append(CultureInfo.InvariantCulture, $"bits={parsed.BitsPerSample}\n");
    sb.Append(CultureInfo.InvariantCulture, $"sampleCount={parsed.SampleCount}\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
  }
}
