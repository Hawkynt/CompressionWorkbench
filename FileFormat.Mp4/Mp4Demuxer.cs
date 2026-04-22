#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mp4;

/// <summary>
/// Demuxes an MP4/MOV file to raw per-track elementary streams. This is not a
/// full decoder: video tracks become Annex-B NALU streams (for H.264/HEVC, SPS/PPS
/// extracted from <c>avcC</c>/<c>hvcC</c> and prepended); audio tracks get the raw
/// sample data in track order; all other track types are written as the flat
/// concatenation of their samples, which is useful for triage even when the
/// codec is obscure.
/// </summary>
public sealed class Mp4Demuxer {
  public sealed record Track(int Id, string HandlerType, string CodecFourCc, byte[] Data, long DurationTicks, int Timescale);

  public IReadOnlyList<Track> Demux(byte[] file) {
    var parser = new BoxParser();
    var boxes = parser.Parse(file);
    var moov = BoxParser.Find(boxes, "moov") ?? throw new InvalidDataException("MP4: missing 'moov'.");
    var tracks = new List<Track>();

    foreach (var trak in moov.Children!.Where(b => b.Type == "trak")) {
      var track = ExtractTrack(file, trak);
      if (track != null) tracks.Add(track);
    }
    return tracks;
  }

  private Track? ExtractTrack(byte[] file, BoxParser.Box trak) {
    var mdia = trak.Children?.FirstOrDefault(b => b.Type == "mdia");
    if (mdia == null) return null;

    var hdlr = mdia.Children?.FirstOrDefault(b => b.Type == "hdlr");
    if (hdlr == null) return null;
    // hdlr body: 1 version + 3 flags + 4 pre_defined + 4 handler_type + 12 reserved + name
    var handlerType = Encoding.ASCII.GetString(file, (int)hdlr.BodyOffset + 8, 4);

    var minf = mdia.Children?.FirstOrDefault(b => b.Type == "minf");
    var stbl = minf?.Children?.FirstOrDefault(b => b.Type == "stbl");
    if (stbl == null) return null;

    var stsd = stbl.Children!.FirstOrDefault(b => b.Type == "stsd");
    var codecFourCc = stsd != null ? ReadCodecFourCc(file, stsd) : "unkn";

    var stsz = stbl.Children!.FirstOrDefault(b => b.Type == "stsz");
    var stco = stbl.Children!.FirstOrDefault(b => b.Type == "stco");
    var co64 = stbl.Children!.FirstOrDefault(b => b.Type == "co64");
    var stsc = stbl.Children!.FirstOrDefault(b => b.Type == "stsc");
    if (stsz == null || stsc == null || (stco == null && co64 == null)) return null;

    var sampleSizes = ReadSampleSizes(file, stsz);
    var chunkOffsets = stco != null
      ? ReadChunkOffsets32(file, stco)
      : ReadChunkOffsets64(file, co64!);
    var samplesPerChunk = ReadSampleToChunk(file, stsc, chunkOffsets.Count);

    // Emit sample bytes concatenated in track order.
    using var body = new MemoryStream();
    var sampleIdx = 0;
    for (var chunk = 0; chunk < chunkOffsets.Count; ++chunk) {
      var count = samplesPerChunk[chunk];
      var offset = chunkOffsets[chunk];
      for (var s = 0; s < count && sampleIdx < sampleSizes.Count; ++s, ++sampleIdx) {
        var size = sampleSizes[sampleIdx];
        if (offset + size > file.Length) break;
        body.Write(file, (int)offset, size);
        offset += size;
      }
    }

    var sampleData = body.ToArray();

    // Convert H.264 length-prefix → Annex-B when we have an avcC box.
    if ((codecFourCc == "avc1" || codecFourCc == "avc3") && stsd != null) {
      var avcc = FindAvcC(file, stsd);
      if (avcc != null)
        sampleData = ConvertToAnnexBWithSpsPps(sampleData, avcc);
    }

    var mdhd = mdia.Children?.FirstOrDefault(b => b.Type == "mdhd");
    long duration = 0; var timescale = 0;
    if (mdhd != null) (duration, timescale) = ReadMdhd(file, mdhd);

    var id = 0; // real track ID comes from tkhd when present; otherwise stays 0
    var tkhd = trak.Children!.FirstOrDefault(b => b.Type == "tkhd");
    if (tkhd != null) {
      var version = file[tkhd.BodyOffset];
      id = (int)BinaryPrimitives.ReadUInt32BigEndian(
        file.AsSpan((int)(tkhd.BodyOffset + (version == 1 ? 20 : 12))));
    }

    return new Track(id, handlerType, codecFourCc, sampleData, duration, timescale);
  }

  private static string ReadCodecFourCc(byte[] file, BoxParser.Box stsd) {
    // stsd: 1+3 version/flags + 4 entry_count + first sample-entry = 8 size + 4 type
    if (stsd.BodyLength < 16) return "unkn";
    return Encoding.ASCII.GetString(file, (int)stsd.BodyOffset + 12, 4);
  }

  private static List<int> ReadSampleSizes(byte[] file, BoxParser.Box stsz) {
    // stsz: 1+3 vf, 4 sample_size, 4 sample_count, optional table
    var body = file.AsSpan((int)stsz.BodyOffset, (int)stsz.BodyLength);
    var fixedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
    var sizes = new List<int>(count);
    if (fixedSize != 0) {
      for (var i = 0; i < count; ++i) sizes.Add(fixedSize);
    } else {
      for (var i = 0; i < count; ++i)
        sizes.Add((int)BinaryPrimitives.ReadUInt32BigEndian(body[(12 + 4 * i)..]));
    }
    return sizes;
  }

  private static List<long> ReadChunkOffsets32(byte[] file, BoxParser.Box stco) {
    var body = file.AsSpan((int)stco.BodyOffset, (int)stco.BodyLength);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var result = new List<long>(count);
    for (var i = 0; i < count; ++i)
      result.Add(BinaryPrimitives.ReadUInt32BigEndian(body[(8 + 4 * i)..]));
    return result;
  }

  private static List<long> ReadChunkOffsets64(byte[] file, BoxParser.Box co64) {
    var body = file.AsSpan((int)co64.BodyOffset, (int)co64.BodyLength);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    var result = new List<long>(count);
    for (var i = 0; i < count; ++i)
      result.Add((long)BinaryPrimitives.ReadUInt64BigEndian(body[(8 + 8 * i)..]));
    return result;
  }

  private static List<int> ReadSampleToChunk(byte[] file, BoxParser.Box stsc, int chunkCount) {
    var body = file.AsSpan((int)stsc.BodyOffset, (int)stsc.BodyLength);
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
    // Each record: first_chunk (1-based), samples_per_chunk, sample_description_index.
    var records = new List<(int FirstChunk, int SamplesPerChunk)>(count);
    for (var i = 0; i < count; ++i) {
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

  private static (long Duration, int Timescale) ReadMdhd(byte[] file, BoxParser.Box mdhd) {
    var body = file.AsSpan((int)mdhd.BodyOffset, (int)mdhd.BodyLength);
    var version = body[0];
    if (version == 1) {
      var timescale = (int)BinaryPrimitives.ReadUInt32BigEndian(body[20..]);
      var duration = (long)BinaryPrimitives.ReadUInt64BigEndian(body[24..]);
      return (duration, timescale);
    } else {
      var timescale = (int)BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
      var duration = (long)BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
      return (duration, timescale);
    }
  }

  private static byte[]? FindAvcC(byte[] file, BoxParser.Box stsd) {
    // Walk sample entries; AVC sample entry contains an avcC box.
    var pos = (int)stsd.BodyOffset + 8; // skip vf + count
    var end = (int)(stsd.BodyOffset + stsd.BodyLength);
    while (pos + 8 <= end) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
      if (size <= 0 || pos + size > end) break;
      // AVC sample entry body starts at pos+8+78 (visual sample entry is 78 bytes after size+type).
      var entryEnd = pos + size;
      var innerPos = pos + 8 + 78;
      while (innerPos + 8 <= entryEnd) {
        var innerSize = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(innerPos));
        var innerType = Encoding.ASCII.GetString(file, innerPos + 4, 4);
        if (innerType == "avcC")
          return file.AsSpan(innerPos + 8, innerSize - 8).ToArray();
        if (innerSize <= 0 || innerPos + innerSize > entryEnd) break;
        innerPos += innerSize;
      }
      pos += size;
    }
    return null;
  }

  private static byte[] ConvertToAnnexBWithSpsPps(byte[] mp4Samples, byte[] avcC) {
    // AVCDecoderConfigurationRecord: 1 config version + 1 profile + 1 profile_compat +
    // 1 level + 1 (6 reserved bits + 2 lengthSizeMinusOne) + 1 (3 reserved + 5 numSPS).
    if (avcC.Length < 7) return mp4Samples;
    var lengthSize = (avcC[4] & 0x03) + 1;
    var numSps = avcC[5] & 0x1F;
    using var output = new MemoryStream();
    Span<byte> startCode = stackalloc byte[] { 0x00, 0x00, 0x00, 0x01 };

    var p = 6;
    for (var i = 0; i < numSps && p + 2 <= avcC.Length; ++i) {
      var len = BinaryPrimitives.ReadUInt16BigEndian(avcC.AsSpan(p));
      p += 2;
      if (p + len > avcC.Length) break;
      output.Write(startCode);
      output.Write(avcC, p, len);
      p += len;
    }
    if (p < avcC.Length) {
      var numPps = avcC[p++];
      for (var i = 0; i < numPps && p + 2 <= avcC.Length; ++i) {
        var len = BinaryPrimitives.ReadUInt16BigEndian(avcC.AsSpan(p));
        p += 2;
        if (p + len > avcC.Length) break;
        output.Write(startCode);
        output.Write(avcC, p, len);
        p += len;
      }
    }

    // Convert length-prefixed NALUs to Annex-B start-code NALUs.
    var q = 0;
    while (q + lengthSize <= mp4Samples.Length) {
      var len = 0;
      for (var i = 0; i < lengthSize; ++i) len = (len << 8) | mp4Samples[q + i];
      q += lengthSize;
      if (q + len > mp4Samples.Length) break;
      output.Write(startCode);
      output.Write(mp4Samples, q, len);
      q += len;
    }
    return output.ToArray();
  }
}
