#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Caf;

/// <summary>
/// Exposes an Apple Core Audio Format (<c>.caf</c>) file as a pseudo-archive and
/// creates fresh LPCM CAF files from canonical per-channel WAV inputs.
/// </summary>
public sealed class CafFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  public string Id => "Caf";
  public string DisplayName => "CAF (Core Audio Format)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".caf";
  public IReadOnlyList<string> Extensions => [".caf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("caff"u8.ToArray(), Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "CAF (Apple Core Audio Format); full file + per-channel LPCM + ancillary chunks.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var fileList = FormatHelpers.FilesOnly(inputs).ToList();
    var full = fileList.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.caf", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(static file => Path.GetFileName(file.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .ToArray();
    if (channelBlobs.Length == 0)
      throw new InvalidOperationException("CAF archive create needs either FULL.caf or one or more per-channel WAVs.");

    var channels = channelBlobs.Select(static file => new WavReader().Read(file.Data)).ToArray();
    var first = channels[0];
    if (channels.Any(channel => channel.SampleRate != first.SampleRate ||
                                channel.BitsPerSample != first.BitsPerSample ||
                                channel.FormatCode != first.FormatCode || channel.NumChannels != 1))
      throw new InvalidOperationException("All channel WAVs must be mono and share sample rate, sample type, and bit depth.");
    if (channels.Any(channel => channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(static channel => channel.InterleavedPcm).ToList(), first.BitsPerSample);
    WriteCaf(output, channels.Length, first.SampleRate, first.BitsPerSample, first.FormatCode == 3, interleaved);
  }

  /// <summary>Writes standards-compliant little-endian packed LPCM CAF.</summary>
  internal static void WriteCaf(Stream output, int channels, int sampleRate, int bitsPerChannel,
    bool isFloat, ReadOnlySpan<byte> interleaved) {
    Span<byte> header = stackalloc byte[8];
    "caff"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
    BinaryPrimitives.WriteUInt16BigEndian(header[6..], 0);
    output.Write(header);

    var bytesPerFrame = checked((uint)(channels * ((bitsPerChannel + 7) / 8)));
    Span<byte> desc = stackalloc byte[32];
    BinaryPrimitives.WriteDoubleBigEndian(desc, sampleRate);
    "lpcm"u8.CopyTo(desc[8..]);
    var flags = isFloat ? FlagIsFloat | FlagIsPacked : FlagIsSignedInteger | FlagIsPacked;
    BinaryPrimitives.WriteUInt32BigEndian(desc[12..], flags);
    BinaryPrimitives.WriteUInt32BigEndian(desc[16..], bytesPerFrame);
    BinaryPrimitives.WriteUInt32BigEndian(desc[20..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(desc[24..], checked((uint)channels));
    BinaryPrimitives.WriteUInt32BigEndian(desc[28..], checked((uint)bitsPerChannel));
    WriteChunk(output, "desc"u8, desc);

    var data = new byte[4 + interleaved.Length];
    interleaved.CopyTo(data.AsSpan(4));
    WriteChunk(output, "data"u8, data);
  }

  private const uint FlagIsFloat = 0x1;
  private const uint FlagIsSignedInteger = 0x4;
  private const uint FlagIsPacked = 0x8;

  internal static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body) {
    if (type.Length != 4) throw new ArgumentException("CAF chunk type must be four bytes.", nameof(type));
    Span<byte> header = stackalloc byte[12];
    type.CopyTo(header);
    BinaryPrimitives.WriteInt64BigEndian(header[4..], body.Length);
    output.Write(header);
    output.Write(body);
  }

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "CAF archive accepts: FULL.caf, LEFT/RIGHT/CENTER/… .wav (per-channel), metadata/*.bin";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var directory = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";
    if (directory.Length == 0 && (name == "full.caf" || name.EndsWith(".wav"))) {
      reason = null;
      return true;
    }
    if (directory == "metadata" && name.EndsWith(".bin")) {
      reason = null;
      return true;
    }
    reason = $"not a CAF-archive input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    var blob = memory.ToArray();
    var parsed = new CafReader().Read(blob);
    var entries = new List<AudioPseudoArchive.Entry> { new("FULL.caf", "Container", blob) };

    if (parsed.FormatId == "lpcm" && parsed.BitsPerSample is 8 or 16 or 24 or 32 or 64 &&
        parsed.NumChannels >= 1 && parsed.InterleavedPcm.Length > 0) {
      if (parsed.IsFloat) {
        if (parsed.BitsPerSample is 32 or 64)
          foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedFloat(
              parsed.InterleavedPcm, parsed.NumChannels, parsed.SampleRate, parsed.BitsPerSample, parsed.ChannelMask))
            entries.Add(new($"{name}.wav", "Channel", wavBlob, "pcm_float"));
      } else if (parsed.BitsPerSample is 8 or 16 or 24 or 32) {
        foreach (var (name, wavBlob) in PcmCodec.SplitInterleavedPcm(
            parsed.InterleavedPcm, parsed.NumChannels, parsed.SampleRate, parsed.BitsPerSample, parsed.ChannelMask))
          entries.Add(new($"{name}.wav", "Channel", wavBlob, "pcm"));
      }
    }

    foreach (var (type, data) in parsed.OtherChunks)
      entries.Add(new($"metadata/{type.Trim()}.bin", "Tag", data));
    return entries;
  }
}
