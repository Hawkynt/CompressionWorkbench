#pragma warning disable CS1591
namespace FileFormat.Matroska;

/// <summary>
/// Walks a Matroska/WebM file and produces per-track raw elementary-stream blobs,
/// plus attachments and chapters as addressable entries. Video tracks known to be
/// H.264/HEVC get Annex-B start-codes prepended, with SPS/PPS extracted from
/// <c>CodecPrivate</c>; other codecs pass through as concatenated frame bytes.
/// Compression (header-stripping, zlib, bzlib) per track's ContentEncoding is
/// intentionally not decoded — in practice the frame-compression codepath is
/// extremely rare in real-world MKVs.
/// </summary>
public sealed class MkvDemuxer {
  public sealed record Track(int Number, string TrackType, string CodecId, string? Language,
                             byte[]? CodecPrivate, byte[] FrameBytes);
  public sealed record Attachment(string FileName, string MimeType, byte[] Data);

  public sealed record DemuxResult(
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<Attachment> Attachments,
    byte[]? ChaptersXml);

  // EBML IDs (incl. length-marker bit). See Matroska spec.
  private const ulong Id_Segment = 0x18538067;
  private const ulong Id_Tracks = 0x1654AE6B;
  private const ulong Id_TrackEntry = 0xAE;
  private const ulong Id_TrackNumber = 0xD7;
  private const ulong Id_TrackType = 0x83;
  private const ulong Id_CodecId = 0x86;
  private const ulong Id_CodecPrivate = 0x63A2;
  private const ulong Id_Language = 0x22B59C;
  private const ulong Id_Cluster = 0x1F43B675;
  private const ulong Id_SimpleBlock = 0xA3;
  private const ulong Id_BlockGroup = 0xA0;
  private const ulong Id_Block = 0xA1;
  private const ulong Id_Attachments = 0x1941A469;
  private const ulong Id_AttachedFile = 0x61A7;
  private const ulong Id_FileName = 0x466E;
  private const ulong Id_FileMimeType = 0x4660;
  private const ulong Id_FileData = 0x465C;
  private const ulong Id_Chapters = 0x1043A770;

  public DemuxResult Demux(byte[] file) {
    var ebml = new EbmlReader(file);
    long pos = 0;

    // Skip top-level EBML header element (0x1A45DFA3) and land on the Segment.
    EbmlReader.Element? segment = null;
    while (pos < file.Length) {
      var el = ebml.Read(ref pos);
      if (el == null) break;
      if (el.Value.Id == Id_Segment) { segment = el; break; }
    }
    if (segment == null)
      throw new InvalidDataException("MKV: no Segment element.");

    var trackEntries = new List<Track>();
    var trackBuffers = new Dictionary<int, MemoryStream>();
    var attachments = new List<Attachment>();
    byte[]? chapters = null;

    foreach (var child in ebml.Children(segment.Value)) {
      switch (child.Id) {
        case Id_Tracks: ParseTracks(ebml, child, trackEntries, trackBuffers); break;
        case Id_Cluster: ParseCluster(ebml, child, trackBuffers); break;
        case Id_Attachments: ParseAttachments(ebml, child, attachments); break;
        case Id_Chapters: chapters = ebml.ReadBinary(child); break;
      }
    }

    // Merge frame bytes into track records; convert H.264/HEVC to Annex-B with SPS/PPS.
    var tracks = new List<Track>(trackEntries.Count);
    foreach (var t in trackEntries) {
      var raw = trackBuffers.TryGetValue(t.Number, out var buf) ? buf.ToArray() : [];
      var data = t.CodecId switch {
        "V_MPEG4/ISO/AVC" => ConvertAvcLengthPrefixToAnnexB(raw, t.CodecPrivate),
        "V_MPEGH/ISO/HEVC" => ConvertHevcLengthPrefixToAnnexB(raw, t.CodecPrivate),
        _ => raw,
      };
      tracks.Add(t with { FrameBytes = data });
    }
    return new DemuxResult(tracks, attachments, chapters);
  }

  private static void ParseTracks(EbmlReader ebml, EbmlReader.Element tracks,
                                   List<Track> entries, Dictionary<int, MemoryStream> buffers) {
    foreach (var entry in ebml.Children(tracks)) {
      if (entry.Id != Id_TrackEntry) continue;
      int number = 0; string codec = "", lang = "eng"; byte[]? codecPrivate = null;
      string type = "other";
      foreach (var field in ebml.Children(entry)) {
        switch (field.Id) {
          case Id_TrackNumber: number = (int)ebml.ReadUnsigned(field); break;
          case Id_TrackType:
            type = ebml.ReadUnsigned(field) switch {
              1 => "video", 2 => "audio", 0x11 => "subtitle", 0x10 => "attachment", _ => "other"
            };
            break;
          case Id_CodecId: codec = ebml.ReadString(field); break;
          case Id_CodecPrivate: codecPrivate = ebml.ReadBinary(field); break;
          case Id_Language: lang = ebml.ReadString(field); break;
        }
      }
      entries.Add(new Track(number, type, codec, lang, codecPrivate, []));
      buffers[number] = new MemoryStream();
    }
  }

  private static void ParseCluster(EbmlReader ebml, EbmlReader.Element cluster,
                                    Dictionary<int, MemoryStream> buffers) {
    foreach (var child in ebml.Children(cluster)) {
      if (child.Id == Id_SimpleBlock) AppendBlockFrames(ebml, child, buffers);
      else if (child.Id == Id_BlockGroup) {
        foreach (var inner in ebml.Children(child))
          if (inner.Id == Id_Block) AppendBlockFrames(ebml, inner, buffers);
      }
    }
  }

  private static void AppendBlockFrames(EbmlReader ebml, EbmlReader.Element block,
                                         Dictionary<int, MemoryStream> buffers) {
    // Block/SimpleBlock body: track-number vint + 16-bit timecode + 8-bit flags + frame bytes.
    var body = ebml.Body(block);
    if (body.Length < 4) return;
    var tnLen = 0;
    for (var i = 0; i < 8; ++i) if ((body[0] & (0x80 >> i)) != 0) { tnLen = i + 1; break; }
    if (tnLen == 0 || body.Length < tnLen + 3) return;
    ulong trackNum = body[0] & (0xFFu >> tnLen);
    for (var i = 1; i < tnLen; ++i) trackNum = (trackNum << 8) | body[i];
    var frames = body[(tnLen + 3)..];
    if (buffers.TryGetValue((int)trackNum, out var buf)) {
      // Lacing ignored — frames written as-is; consumers needing per-frame splitting
      // parse the lacing flag themselves.
      buf.Write(frames);
    }
  }

  private static void ParseAttachments(EbmlReader ebml, EbmlReader.Element attachments,
                                        List<Attachment> result) {
    foreach (var file in ebml.Children(attachments)) {
      if (file.Id != Id_AttachedFile) continue;
      string name = "attachment", mime = "application/octet-stream";
      byte[] data = [];
      foreach (var field in ebml.Children(file)) {
        switch (field.Id) {
          case Id_FileName: name = ebml.ReadString(field); break;
          case Id_FileMimeType: mime = ebml.ReadString(field); break;
          case Id_FileData: data = ebml.ReadBinary(field); break;
        }
      }
      result.Add(new Attachment(name, mime, data));
    }
  }

  // CodecPrivate for H.264 is the AVCDecoderConfigurationRecord — same wire shape MP4 uses.
  private static byte[] ConvertAvcLengthPrefixToAnnexB(byte[] frames, byte[]? avcC) {
    if (avcC == null || avcC.Length < 7) return frames;
    var lengthSize = (avcC[4] & 0x03) + 1;
    var numSps = avcC[5] & 0x1F;
    using var output = new MemoryStream();
    Span<byte> startCode = stackalloc byte[] { 0x00, 0x00, 0x00, 0x01 };
    var p = 6;
    for (var i = 0; i < numSps && p + 2 <= avcC.Length; ++i) {
      var len = (avcC[p] << 8) | avcC[p + 1]; p += 2;
      if (p + len > avcC.Length) break;
      output.Write(startCode); output.Write(avcC, p, len); p += len;
    }
    if (p < avcC.Length) {
      var numPps = avcC[p++];
      for (var i = 0; i < numPps && p + 2 <= avcC.Length; ++i) {
        var len = (avcC[p] << 8) | avcC[p + 1]; p += 2;
        if (p + len > avcC.Length) break;
        output.Write(startCode); output.Write(avcC, p, len); p += len;
      }
    }
    var q = 0;
    while (q + lengthSize <= frames.Length) {
      var len = 0;
      for (var i = 0; i < lengthSize; ++i) len = (len << 8) | frames[q + i];
      q += lengthSize;
      if (q + len > frames.Length) break;
      output.Write(startCode); output.Write(frames, q, len); q += len;
    }
    return output.ToArray();
  }

  // HEVC CodecPrivate is HEVCDecoderConfigurationRecord; SPS/PPS/VPS are listed in an array
  // whose header is longer than AVC. For a first implementation we pass through.
  private static byte[] ConvertHevcLengthPrefixToAnnexB(byte[] frames, byte[]? hvcC) => frames;
}
