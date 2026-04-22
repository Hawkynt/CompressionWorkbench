#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Mp4;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Alac;

/// <summary>
/// Surfaces an ALAC (Apple Lossless) audio file — usually wrapped in an M4A
/// (ISOBMFF) container — as a read-only archive of the container passthrough,
/// the ALAC codec-specific "magic cookie", the raw ALAC frame bytes extracted
/// via stsz/stsc/stco, and a metadata.ini describing the cookie fields.
/// </summary>
public sealed class AlacFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Alac";
  public string DisplayName => "ALAC (Apple Lossless)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".m4a";
  public IReadOnlyList<string> Extensions => [".m4a", ".alac", ".caf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Detection by ftyp brand is weak (m4a overlaps with plain AAC); we piggyback on the
  // generic MP4 detector and expose ALAC-specific surface only when an alac sample entry
  // is present. Leaving MagicSignatures empty here avoids clobbering Mp4's detection.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored"), new("alac", "ALAC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description => "Apple Lossless codec in ISOBMFF; extracts cookie + raw frames.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Kind == "Track" ? "alac" : "stored",
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
      ("FULL.m4a", "Container", file),
    };

    var parser = new BoxParser();
    var boxes = parser.Parse(file);
    var moov = BoxParser.Find(boxes, "moov");
    if (moov == null || moov.Children == null)
      return entries; // Not a valid MP4/M4A; just keep passthrough.

    var trackIndex = 0;
    foreach (var trak in moov.Children.Where(b => b.Type == "trak")) {
      var mdia = trak.Children?.FirstOrDefault(b => b.Type == "mdia");
      var minf = mdia?.Children?.FirstOrDefault(b => b.Type == "minf");
      var stbl = minf?.Children?.FirstOrDefault(b => b.Type == "stbl");
      if (stbl == null) continue;

      var stsd = stbl.Children!.FirstOrDefault(b => b.Type == "stsd");
      if (stsd == null) continue;

      // Locate the "alac" sample entry inside stsd and its embedded "alac"
      // codec-specific atom (the magic cookie).
      if (!TryFindAlacSampleEntry(file, stsd, out var sampleEntryOffset, out var sampleEntrySize))
        continue;

      var cookie = ExtractAlacCookie(file, sampleEntryOffset, sampleEntrySize);
      if (cookie.Length == 0) continue;

      entries.Add(("alac_magic_cookie.bin", "CodecConfig", cookie));

      var meta = BuildMetadata(cookie);
      entries.Add(("metadata.ini", "Metadata", Encoding.UTF8.GetBytes(meta)));

      // Pull raw ALAC frames via stsz / stsc / stco/co64.
      var stsz = stbl.Children!.FirstOrDefault(b => b.Type == "stsz");
      var stco = stbl.Children!.FirstOrDefault(b => b.Type == "stco");
      var co64 = stbl.Children!.FirstOrDefault(b => b.Type == "co64");
      var stsc = stbl.Children!.FirstOrDefault(b => b.Type == "stsc");
      if (stsz != null && stsc != null && (stco != null || co64 != null)) {
        var frames = ExtractFrames(file, stsz, stsc, stco, co64);
        var name = $"track_{trackIndex:D2}_alac.bin";
        entries.Add((name, "Track", frames));
      }

      ++trackIndex;
    }

    return entries;
  }

  // Walks the stsd body looking for a sample entry with fourcc "alac".
  private static bool TryFindAlacSampleEntry(byte[] file, BoxParser.Box stsd, out int entryOffset, out int entrySize) {
    entryOffset = 0;
    entrySize = 0;
    var pos = (int)stsd.BodyOffset + 8; // 4 version/flags + 4 entry_count
    var end = (int)(stsd.BodyOffset + stsd.BodyLength);
    while (pos + 8 <= end) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
      if (size <= 0 || pos + size > end) break;
      var type = Encoding.ASCII.GetString(file, pos + 4, 4);
      if (type == "alac") {
        entryOffset = pos;
        entrySize = size;
        return true;
      }
      pos += size;
    }
    return false;
  }

  // The "alac" sample entry (SoundDescriptionBox variant) contains the codec-specific
  // "alac" atom (magic cookie). The audio sample entry prelude is 8 (size+type) + 8
  // (reserved) + 8 (data_reference_index + 6 bytes) + 20 (sound description v0 body).
  // We just scan children for an inner "alac" box; layouts vary across QuickTime
  // versions, so a scan is safer than trusting a fixed offset.
  private static byte[] ExtractAlacCookie(byte[] file, int sampleEntryOffset, int sampleEntrySize) {
    var entryEnd = sampleEntryOffset + sampleEntrySize;
    // Minimum audio sample entry prelude is 8 bytes (size+type) + 28 bytes (reserved/channelcount/samplesize/pre_defined/reserved/samplerate).
    var scanStart = sampleEntryOffset + 8 + 28;
    for (var p = scanStart; p + 8 <= entryEnd; ) {
      // The prelude length varies with version; as a robustness measure, scan byte-by-byte for
      // a well-formed inner "alac" box rather than trusting a fixed position.
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p));
      if (size >= 8 && p + size <= entryEnd) {
        var type = Encoding.ASCII.GetString(file, p + 4, 4);
        if (type == "alac") {
          // Body = everything after size+type header.
          var bodyStart = p + 8;
          var bodyLen = size - 8;
          // Some QuickTime variants add a 4-byte version/flags before the cookie proper;
          // leave that detail to consumers — we surface the raw child-box body verbatim.
          if (bodyLen <= 0 || bodyStart + bodyLen > entryEnd) break;
          return file.AsSpan(bodyStart, bodyLen).ToArray();
        }
        p += size;
      } else {
        ++p;
      }
    }
    return [];
  }

  // ALAC magic cookie (AudioSpecificConfig): 24 bytes after the optional 4-byte
  // version/flags prefix. Layout:
  //   u32 frameLength, u8 compatVersion, u8 bitDepth, u8 pb, u8 mb, u8 kb,
  //   u8 numChannels, u16 maxRun, u32 maxFrameBytes, u32 avgBitRate, u32 sampleRate
  private static string BuildMetadata(byte[] cookie) {
    var off = 0;
    // Many cookies begin with a 4-byte full-box version/flags. Detect by checking
    // whether the implied frameLength looks sane (>=64).
    if (cookie.Length >= 4 + 24) {
      var probe = BinaryPrimitives.ReadUInt32BigEndian(cookie.AsSpan(4));
      if (probe >= 64 && probe <= 1u << 20)
        off = 4;
    }
    var sb = new StringBuilder();
    sb.AppendLine("[alac]");
    if (cookie.Length >= off + 24) {
      var frameLength = BinaryPrimitives.ReadUInt32BigEndian(cookie.AsSpan(off));
      var bitDepth = cookie[off + 5];
      var numChannels = cookie[off + 9];
      var sampleRate = BinaryPrimitives.ReadUInt32BigEndian(cookie.AsSpan(off + 20));
      sb.Append("frame_length=").AppendLine(frameLength.ToString(CultureInfo.InvariantCulture));
      sb.Append("bit_depth=").AppendLine(bitDepth.ToString(CultureInfo.InvariantCulture));
      sb.Append("channels=").AppendLine(numChannels.ToString(CultureInfo.InvariantCulture));
      sb.Append("sample_rate=").AppendLine(sampleRate.ToString(CultureInfo.InvariantCulture));
    } else {
      sb.AppendLine("; cookie shorter than 24 bytes — cannot parse");
    }
    return sb.ToString();
  }

  private static byte[] ExtractFrames(
      byte[] file, BoxParser.Box stsz, BoxParser.Box stsc, BoxParser.Box? stco, BoxParser.Box? co64) {
    var sampleSizes = ReadSampleSizes(file, stsz);
    var chunkOffsets = stco != null
      ? ReadChunkOffsets32(file, stco)
      : ReadChunkOffsets64(file, co64!);
    var samplesPerChunk = ReadSampleToChunk(file, stsc, chunkOffsets.Count);

    using var body = new MemoryStream();
    var sampleIdx = 0;
    for (var chunk = 0; chunk < chunkOffsets.Count; ++chunk) {
      var count = samplesPerChunk[chunk];
      var offset = chunkOffsets[chunk];
      for (var s = 0; s < count && sampleIdx < sampleSizes.Count; ++s, ++sampleIdx) {
        var size = sampleSizes[sampleIdx];
        if (offset < 0 || offset + size > file.Length) break;
        body.Write(file, (int)offset, size);
        offset += size;
      }
    }
    return body.ToArray();
  }

  private static List<int> ReadSampleSizes(byte[] file, BoxParser.Box stsz) {
    var body = file.AsSpan((int)stsz.BodyOffset, (int)stsz.BodyLength);
    var fixedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
    var sizes = new List<int>(count);
    if (fixedSize != 0) {
      for (var i = 0; i < count; ++i) sizes.Add(fixedSize);
    } else {
      for (var i = 0; i < count && 12 + 4 * i + 4 <= body.Length; ++i)
        sizes.Add((int)BinaryPrimitives.ReadUInt32BigEndian(body[(12 + 4 * i)..]));
    }
    return sizes;
  }

  private static List<long> ReadChunkOffsets32(byte[] file, BoxParser.Box stco) {
    var body = file.AsSpan((int)stco.BodyOffset, (int)stco.BodyLength);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var result = new List<long>(count);
    for (var i = 0; i < count && 8 + 4 * i + 4 <= body.Length; ++i)
      result.Add(BinaryPrimitives.ReadUInt32BigEndian(body[(8 + 4 * i)..]));
    return result;
  }

  private static List<long> ReadChunkOffsets64(byte[] file, BoxParser.Box co64) {
    var body = file.AsSpan((int)co64.BodyOffset, (int)co64.BodyLength);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var result = new List<long>(count);
    for (var i = 0; i < count && 8 + 8 * i + 8 <= body.Length; ++i)
      result.Add((long)BinaryPrimitives.ReadUInt64BigEndian(body[(8 + 8 * i)..]));
    return result;
  }

  private static List<int> ReadSampleToChunk(byte[] file, BoxParser.Box stsc, int chunkCount) {
    var body = file.AsSpan((int)stsc.BodyOffset, (int)stsc.BodyLength);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var records = new List<(int FirstChunk, int SamplesPerChunk)>(count);
    for (var i = 0; i < count && 8 + 12 * i + 12 <= body.Length; ++i) {
      var fc = (int)BinaryPrimitives.ReadUInt32BigEndian(body[(8 + 12 * i)..]);
      var spc = (int)BinaryPrimitives.ReadUInt32BigEndian(body[(12 + 12 * i)..]);
      records.Add((fc, spc));
    }
    var perChunk = new List<int>(chunkCount);
    for (var c = 1; c <= chunkCount; ++c) {
      var spc = 0;
      for (var i = 0; i < records.Count; ++i) {
        if (records[i].FirstChunk <= c &&
            (i + 1 == records.Count || records[i + 1].FirstChunk > c))
          spc = records[i].SamplesPerChunk;
      }
      perChunk.Add(spc);
    }
    return perChunk;
  }
}
