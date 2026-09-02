#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.MonkeysAudio;
using Codec.Pcm;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ape;

/// <summary>
/// Surfaces a Monkey's Audio (.ape) file as a read-only archive of the
/// container passthrough, the raw APE descriptor header, the preserved WAV
/// header bytes, the concatenated frame data, the seek table, and a
/// metadata.ini describing the stream parameters.
/// <para>
/// When the stream is a compression-level-1000 ("fast") Monkey's Audio file the
/// decoder can handle, the listing also gains one playable mono WAV per speaker
/// (<c>LEFT.wav</c>/<c>RIGHT.wav</c>/<c>MONO.wav</c>/…, Kind <c>Channel</c>,
/// method <c>pcm</c>), named per <see cref="ChannelLayout"/>. The decode is
/// best-effort: higher compression levels, unsupported bit depths/channel counts
/// or malformed input leave the container/metadata view intact rather than
/// failing.
/// </para>
/// </summary>
public sealed class ApeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ape";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Monkey's Audio (.ape)";
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
public string DefaultExtension => ".ape";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ape", ".mac"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x4D, 0x41, 0x43, 0x20], Confidence: 0.95), // "MAC "
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored"), new("ape", "APE"), new("pcm", "PCM")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Monkey's Audio; WAV header + per-frame blocks + seek table + APEv2 tags.";

  private static readonly byte[] ApeTagMagic = "APETAGEX"u8.ToArray();

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Kind == "Channel" ? "pcm" : e.Kind == "Frames" ? "ape" : "stored",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
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

    AddChannelEntries(file, entries);

    return entries;
  }

  /// <summary>
  /// Best-effort decode-and-split. The codec handles level-1000 ("fast") Monkey's
  /// Audio and throws <see cref="NotSupportedException"/> for higher levels /
  /// unsupported geometry and <see cref="InvalidDataException"/> for malformed
  /// input; in every failure case the container/metadata listing is left untouched.
  /// </summary>
  private static void AddChannelEntries(byte[] file, List<(string Name, string Kind, byte[] Data)> entries) {
    try {
      using var probe = new MemoryStream(file, writable: false);
      var info = MonkeysAudioCodec.ReadStreamInfo(probe);

      using var src = new MemoryStream(file, writable: false);
      using var pcm = new MemoryStream();
      MonkeysAudioCodec.Decompress(src, pcm);
      var pcmBytes = pcm.ToArray();

      if (info.Channels <= 1) {
        entries.Add(("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcmBytes, 1, info.SampleRate, info.BitsPerSample, formatCode: 1)));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcmBytes, info.Channels, info.SampleRate, info.BitsPerSample))
          entries.Add(($"{name}.wav", "Channel", wav));
      }
    } catch (Exception) {
      // Graceful fallback: keep the container/metadata listing only.
    }
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

    // Split the frame-data blob into per-frame blocks using the seek table.
    // The seek table is an array of u32 absolute file offsets, one per compressed
    // frame; each frame runs from its offset to the next (or to the end of the
    // frame-data region for the final frame).
    AddPerFrameBlocks(file, seekStart, seekEnd, frameStart, frameEnd, entries);

    // APEv2 tag (if present) lives in the terminating-data region (after the
    // frame data). Surface its text items as tags.ini.
    AddApeTags(file, termStart, termEnd, entries);
  }

  // The seek table holds u32 absolute offsets into the file, one per frame. We
  // clamp each [start,next) span to the frame-data region so a malformed table
  // can never read into the header or out of bounds.
  private static void AddPerFrameBlocks(
      byte[] file, long seekStart, long seekEnd, long frameStart, long frameEnd,
      List<(string Name, string Kind, byte[] Data)> entries) {
    if (seekEnd <= seekStart || frameEnd <= frameStart) return;
    var clampedSeekEnd = Math.Min(seekEnd, file.Length);
    var entryCount = (int)((clampedSeekEnd - seekStart) / 4);
    if (entryCount <= 0) return;

    var offsets = new long[entryCount];
    for (var i = 0; i < entryCount; ++i)
      offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)(seekStart + 4L * i)));

    var dataEnd = Math.Min(frameEnd, file.Length);
    for (var i = 0; i < entryCount; ++i) {
      var start = offsets[i];
      var end = i + 1 < entryCount ? offsets[i + 1] : dataEnd;
      // A frame must start inside the frame-data region and grow forwards.
      if (start < frameStart || start >= dataEnd) continue;
      if (end < start || end > dataEnd) end = dataEnd;
      var len = (int)(end - start);
      if (len <= 0) continue;
      entries.Add(($"frames/frame_{i:D4}.bin", "Frame", file.AsSpan((int)start, len).ToArray()));
    }
  }

  // Scans the terminating-data region for an APEv2 footer ("APETAGEX") and, if
  // found, decodes the UTF-8 text items into a key=value tags.ini. Binary items
  // (cover art etc.) are listed by name+size only.
  private static void AddApeTags(
      byte[] file, long termStart, long termEnd,
      List<(string Name, string Kind, byte[] Data)> entries) {
    var clampedEnd = Math.Min(termEnd, file.Length);
    if (clampedEnd - termStart < 32) return;

    // The footer is the last 32 bytes of the APEv2 tag. Search the terminating
    // region for the footer magic at a 32-byte-aligned tail position.
    for (var footerPos = clampedEnd - 32; footerPos >= termStart; --footerPos) {
      if (!MatchesMagic(file, (int)footerPos, ApeTagMagic)) continue;
      var tagSize = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)footerPos + 12)); // items + footer
      var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)footerPos + 16));
      if (tagSize < 32 || itemCount == 0 || itemCount > 65535) continue;
      var itemsStart = footerPos + 32 - (long)tagSize; // items begin tagSize-32 before the footer
      if (itemsStart < termStart) continue;

      var ini = TryParseApeItems(file, itemsStart, footerPos, itemCount);
      if (ini != null) {
        entries.Add(("tags.ini", "Tag", Encoding.UTF8.GetBytes(ini)));
        return;
      }
    }
  }

  private static string? TryParseApeItems(byte[] file, long itemsStart, long itemsEnd, uint itemCount) {
    var sb = new StringBuilder();
    sb.AppendLine("; APEv2 tags");
    var pos = (int)itemsStart;
    var end = (int)itemsEnd;
    for (var i = 0; i < itemCount; ++i) {
      if (pos + 8 > end) break;
      var valueLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
      pos += 8;
      // Key is a null-terminated ASCII string.
      var keyStart = pos;
      while (pos < end && file[pos] != 0) ++pos;
      if (pos >= end) break;
      var key = Encoding.ASCII.GetString(file, keyStart, pos - keyStart);
      ++pos; // skip null
      if (valueLen < 0 || pos + valueLen > end) break;
      // Item value type lives in bits 1-2 of the flags: 0 = UTF-8 text.
      var isText = ((flags >> 1) & 0x03) == 0;
      if (isText) {
        var value = Encoding.UTF8.GetString(file, pos, valueLen).Replace("\0", "; ");
        sb.Append(key).Append('=').AppendLine(value);
      } else {
        sb.Append("; ").Append(key).Append(" (binary, ").Append(valueLen.ToString(CultureInfo.InvariantCulture)).AppendLine(" bytes)");
      }
      pos += valueLen;
    }
    return sb.Length > "; APEv2 tags\r\n".Length ? sb.ToString() : null;
  }

  private static bool MatchesMagic(byte[] buffer, int offset, byte[] magic) {
    if (offset < 0 || offset + magic.Length > buffer.Length) return false;
    for (var i = 0; i < magic.Length; ++i)
      if (buffer[offset + i] != magic[i]) return false;
    return true;
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
