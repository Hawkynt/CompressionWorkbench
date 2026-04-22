#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ape;

/// <summary>
/// Surfaces a Monkey's Audio (.ape) file as a read-only archive of the
/// container passthrough, the raw APE descriptor header, the preserved WAV
/// header bytes, the concatenated frame data, the seek table, and a
/// metadata.ini describing the stream parameters.
/// </summary>
public sealed class ApeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Ape";
  public string DisplayName => "Monkey's Audio (.ape)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ape";
  public IReadOnlyList<string> Extensions => [".ape", ".mac"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x4D, 0x41, 0x43, 0x20], Confidence: 0.95), // "MAC "
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored"), new("ape", "APE")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Monkey's Audio; exposes WAV header + frame blob + seek table.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Kind == "Frames" ? "ape" : "stored",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var file = ms.ToArray();

    var entries = new List<(string, string, byte[])> {
      ("FULL.ape", "Container", file),
    };

    if (file.Length < 6 || file[0] != 0x4D || file[1] != 0x41 || file[2] != 0x43 || file[3] != 0x20)
      return entries; // Missing "MAC " magic — treat as opaque.

    // Version is a little-endian u16 right after the magic.
    var version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));

    if (version >= 3980)
      ParseModern(file, version, entries);
    else
      ParseLegacy(file, version, entries);

    return entries;
  }

  // Modern (3.98+) APE layout:
  //   APE_DESCRIPTOR (52 bytes): magic(4) + version(2) + pad(2) + descriptorBytes(4) +
  //     headerBytes(4) + seekTableBytes(4) + wavHeaderBytes(4) + apeFrameDataBytes(4) +
  //     apeFrameDataBytesHigh(4) + terminatingDataBytes(4) + md5(16)
  //   APE_HEADER (24 bytes): compressionLevel(2) + formatFlags(2) + blocksPerFrame(4) +
  //     finalFrameBlocks(4) + totalFrames(4) + bitsPerSample(2) + channels(2) + sampleRate(4)
  private static void ParseModern(byte[] file, ushort version, List<(string Name, string Kind, byte[] Data)> entries) {
    const int DescSize = 52;
    if (file.Length < DescSize) return;

    var descriptorBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
    var headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
    var seekTableBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
    var wavHeaderBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20));
    var frameDataBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24));
    var terminatingBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(32));

    if (descriptorBytes < DescSize) descriptorBytes = DescSize;

    // Layout on disk follows the descriptor's byte counts: descriptor -> header ->
    // seek table -> wav header -> frame data -> terminating data.
    var descEnd = (long)descriptorBytes;
    var headerStart = descEnd;
    var headerEnd = headerStart + headerBytes;
    var seekStart = headerEnd;
    var seekEnd = seekStart + seekTableBytes;
    var wavStart = seekEnd;
    var wavEnd = wavStart + wavHeaderBytes;
    var frameStart = wavEnd;
    var frameEnd = frameStart + frameDataBytes;
    var termStart = frameEnd;
    var termEnd = termStart + terminatingBytes;

    if (headerEnd <= file.Length && headerBytes >= 24) {
      var hdr = file.AsSpan((int)headerStart, (int)headerBytes);
      var compressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(hdr);
      var formatFlags = BinaryPrimitives.ReadUInt16LittleEndian(hdr[2..]);
      var blocksPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);
      var finalFrameBlocks = BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..]);
      var totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(hdr[12..]);
      var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(hdr[16..]);
      var channels = BinaryPrimitives.ReadUInt16LittleEndian(hdr[18..]);
      var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(hdr[20..]);

      var totalSamples = totalFrames == 0
        ? 0L
        : (long)(totalFrames - 1) * blocksPerFrame + finalFrameBlocks;

      var sb = new StringBuilder();
      sb.AppendLine("[ape]");
      sb.Append("version=").AppendLine(version.ToString(CultureInfo.InvariantCulture));
      sb.Append("compression_level=").AppendLine(compressionLevel.ToString(CultureInfo.InvariantCulture));
      sb.Append("format_flags=0x").AppendLine(formatFlags.ToString("X4", CultureInfo.InvariantCulture));
      sb.Append("sample_rate=").AppendLine(sampleRate.ToString(CultureInfo.InvariantCulture));
      sb.Append("channels=").AppendLine(channels.ToString(CultureInfo.InvariantCulture));
      sb.Append("bits_per_sample=").AppendLine(bitsPerSample.ToString(CultureInfo.InvariantCulture));
      sb.Append("total_frames=").AppendLine(totalFrames.ToString(CultureInfo.InvariantCulture));
      sb.Append("total_samples=").AppendLine(totalSamples.ToString(CultureInfo.InvariantCulture));
      entries.Add(("metadata.ini", "Metadata", Encoding.UTF8.GetBytes(sb.ToString())));
    }

    AddSlice(file, wavStart, wavEnd, "wav_header.bin", "WavHeader", entries);
    AddSlice(file, seekStart, seekEnd, "seek_table.bin", "SeekTable", entries);
    AddSlice(file, frameStart, frameEnd, "frames.bin", "Frames", entries);
    AddSlice(file, termStart, termEnd, "terminating.bin", "Terminating", entries);
  }

  // Legacy (pre-3.98) APE_HEADER layout (32 bytes) immediately after "MAC " + version:
  //   compressionLevel(2), formatFlags(2), channels(2), sampleRate(4),
  //   headerBytes(4), terminatingBytes(4), totalFrames(4), finalFrameBlocks(4),
  //   peakLevel(4), seekElements(4), wavHeaderBytes(4), wavTerminatingBytes(4),
  //   bitsPerSample(2), ...  (exact layout varies across 3.93/3.95/3.97)
  // We extract what we can and fall back to "unknown" fields rather than throwing.
  private static void ParseLegacy(byte[] file, ushort version, List<(string Name, string Kind, byte[] Data)> entries) {
    const int MinLegacyHeader = 6 + 26;
    if (file.Length < MinLegacyHeader) return;

    var compressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
    var formatFlags = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8));
    var channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(10));
    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
    var headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
    var terminatingBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20));
    var totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24));
    var finalFrameBlocks = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(28));

    // Legacy files had a 16-bit sample-depth field implied by format flags;
    // assume 16 unless the FORMAT_FLAG_8_BIT or FORMAT_FLAG_24_BIT is set.
    var bitsPerSample = (formatFlags & 0x01) != 0 ? 8 : (formatFlags & 0x08) != 0 ? 24 : 16;

    var sb = new StringBuilder();
    sb.AppendLine("[ape]");
    sb.Append("version=").AppendLine(version.ToString(CultureInfo.InvariantCulture));
    sb.Append("compression_level=").AppendLine(compressionLevel.ToString(CultureInfo.InvariantCulture));
    sb.Append("format_flags=0x").AppendLine(formatFlags.ToString("X4", CultureInfo.InvariantCulture));
    sb.Append("sample_rate=").AppendLine(sampleRate.ToString(CultureInfo.InvariantCulture));
    sb.Append("channels=").AppendLine(channels.ToString(CultureInfo.InvariantCulture));
    sb.Append("bits_per_sample=").AppendLine(bitsPerSample.ToString(CultureInfo.InvariantCulture));
    sb.Append("total_frames=").AppendLine(totalFrames.ToString(CultureInfo.InvariantCulture));
    sb.Append("final_frame_blocks=").AppendLine(finalFrameBlocks.ToString(CultureInfo.InvariantCulture));
    sb.Append("terminating_bytes=").AppendLine(terminatingBytes.ToString(CultureInfo.InvariantCulture));
    sb.Append("header_bytes=").AppendLine(headerBytes.ToString(CultureInfo.InvariantCulture));
    entries.Add(("metadata.ini", "Metadata", Encoding.UTF8.GetBytes(sb.ToString())));

    // Legacy layout is not as cleanly delimited; emit the whole post-header span as "frames.bin".
    var bodyStart = 6L + 26;
    if (bodyStart < file.Length)
      entries.Add(("frames.bin", "Frames", file.AsSpan((int)bodyStart).ToArray()));
  }

  private static void AddSlice(
      byte[] file, long start, long end, string name, string kind,
      List<(string Name, string Kind, byte[] Data)> entries) {
    if (start < 0 || end <= start) return;
    var clampedEnd = Math.Min(end, file.Length);
    if (clampedEnd <= start) return;
    entries.Add((name, kind, file.AsSpan((int)start, (int)(clampedEnd - start)).ToArray()));
  }
}
