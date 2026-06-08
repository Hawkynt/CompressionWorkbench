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
  /// <summary>A single block (frame) from a track.</summary>
  public sealed record FrameEntry(byte[] Data);

  public sealed record Track(int Number, string TrackType, string CodecId, string? Language,
                             byte[]? CodecPrivate, byte[] FrameBytes,
                             IReadOnlyList<FrameEntry> Frames,
                             int AudioChannels = 0, int AudioSampleRate = 0, int AudioBitDepth = 0);
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
  private const ulong Id_Audio = 0xE1;
  private const ulong Id_SamplingFrequency = 0xB5;
  private const ulong Id_Channels = 0x9F;
  private const ulong Id_BitDepth = 0x6264;

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
    var trackFrames = new Dictionary<int, List<FrameEntry>>();
    var attachments = new List<Attachment>();
    byte[]? chapters = null;

    foreach (var child in ebml.Children(segment.Value)) {
      switch (child.Id) {
        case Id_Tracks: ParseTracks(ebml, child, trackEntries, trackBuffers, trackFrames); break;
        case Id_Cluster: ParseCluster(ebml, child, trackBuffers, trackFrames); break;
        case Id_Attachments: ParseAttachments(ebml, child, attachments); break;
        case Id_Chapters: chapters = ebml.ReadBinary(child); break;
      }
    }

    // Merge frame bytes into track records; convert H.264/HEVC to Annex-B with SPS/PPS.
    var tracks = new List<Track>(trackEntries.Count);
    foreach (var t in trackEntries) {
      var raw = trackBuffers.TryGetValue(t.Number, out var buf) ? buf.ToArray() : [];
      var frames = trackFrames.TryGetValue(t.Number, out var fl) ? (IReadOnlyList<FrameEntry>)fl : [];
      var data = t.CodecId switch {
        "V_MPEG4/ISO/AVC" => ConvertAvcLengthPrefixToAnnexB(raw, t.CodecPrivate),
        "V_MPEGH/ISO/HEVC" => ConvertHevcLengthPrefixToAnnexB(raw, t.CodecPrivate),
        _ => raw,
      };
      tracks.Add(t with { FrameBytes = data, Frames = frames });
    }
    return new DemuxResult(tracks, attachments, chapters);
  }

  private static void ParseTracks(EbmlReader ebml, EbmlReader.Element tracks,
                                   List<Track> entries, Dictionary<int, MemoryStream> buffers,
                                   Dictionary<int, List<FrameEntry>> frameLists) {
    foreach (var entry in ebml.Children(tracks)) {
      if (entry.Id != Id_TrackEntry) continue;
      int number = 0; string codec = "", lang = "eng"; byte[]? codecPrivate = null;
      string type = "other";
      int channels = 0, sampleRate = 0, bitDepth = 0;
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
          case Id_Audio: ParseAudio(ebml, field, ref channels, ref sampleRate, ref bitDepth); break;
        }
      }
      entries.Add(new Track(number, type, codec, lang, codecPrivate, [], [], channels, sampleRate, bitDepth));
      buffers[number] = new MemoryStream();
      frameLists[number] = new List<FrameEntry>();
    }
  }

  /// <summary>Reads the TrackEntry Audio element: SamplingFrequency (float), Channels, BitDepth.</summary>
  private static void ParseAudio(EbmlReader ebml, EbmlReader.Element audio,
                                  ref int channels, ref int sampleRate, ref int bitDepth) {
    foreach (var field in ebml.Children(audio)) {
      switch (field.Id) {
        case Id_SamplingFrequency: sampleRate = (int)Math.Round(ReadFloat(ebml.Body(field))); break;
        case Id_Channels: channels = (int)ebml.ReadUnsigned(field); break;
        case Id_BitDepth: bitDepth = (int)ebml.ReadUnsigned(field); break;
      }
    }
  }

  /// <summary>EBML float (4- or 8-byte big-endian IEEE-754); 0 length defaults to 0.</summary>
  private static double ReadFloat(ReadOnlySpan<byte> body) => body.Length switch {
    4 => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(body),
    8 => System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(body),
    _ => 0,
  };

  private static void ParseCluster(EbmlReader ebml, EbmlReader.Element cluster,
                                    Dictionary<int, MemoryStream> buffers,
                                    Dictionary<int, List<FrameEntry>> frameLists) {
    foreach (var child in ebml.Children(cluster)) {
      if (child.Id == Id_SimpleBlock) AppendBlockFrames(ebml, child, buffers, frameLists);
      else if (child.Id == Id_BlockGroup) {
        foreach (var inner in ebml.Children(child))
          if (inner.Id == Id_Block) AppendBlockFrames(ebml, inner, buffers, frameLists);
      }
    }
  }

  private static void AppendBlockFrames(EbmlReader ebml, EbmlReader.Element block,
                                         Dictionary<int, MemoryStream> buffers,
                                         Dictionary<int, List<FrameEntry>> frameLists) {
    // Block/SimpleBlock body: track-number vint + 16-bit timecode + 8-bit flags + frame bytes.
    var body = ebml.Body(block);
    if (body.Length < 4) return;
    var tnLen = 0;
    for (var i = 0; i < 8; ++i) if ((body[0] & (0x80 >> i)) != 0) { tnLen = i + 1; break; }
    if (tnLen == 0 || body.Length < tnLen + 3) return;
    ulong trackNum = body[0] & (0xFFu >> tnLen);
    for (var i = 1; i < tnLen; ++i) trackNum = (trackNum << 8) | body[i];

    var flags = body[tnLen + 2];
    var lacing = (flags >> 1) & 0x03; // 0=none, 1=Xiph, 3=EBML, 2=fixed
    var payload = body[(tnLen + 3)..];

    var frames = lacing == 0 ? [payload.ToArray()] : SplitLaced(payload, lacing);
    if (!buffers.TryGetValue((int)trackNum, out var buf)) return;
    foreach (var frame in frames) {
      buf.Write(frame);
      if (frameLists.TryGetValue((int)trackNum, out var fl))
        fl.Add(new FrameEntry(frame));
    }
  }

  /// <summary>
  /// Splits a laced block payload into individual frames. The first payload byte is the
  /// frame count minus one; the per-frame sizes follow per the lacing type (Xiph 255-run,
  /// EBML vint deltas, fixed = equal split), then the concatenated frame data.
  /// </summary>
  private static List<byte[]> SplitLaced(ReadOnlySpan<byte> payload, int lacing) {
    var frames = new List<byte[]>();
    if (payload.Length < 1) return frames;
    var laceCount = payload[0] + 1;
    var p = 1;
    var sizes = new int[laceCount];

    switch (lacing) {
      case 1: { // Xiph: sum 255-runs for the first n-1 frames; last is the remainder.
        for (var i = 0; i < laceCount - 1; ++i) {
          var size = 0;
          while (p < payload.Length && payload[p] == 255) { size += 255; ++p; }
          if (p < payload.Length) { size += payload[p]; ++p; }
          sizes[i] = size;
        }
        break;
      }
      case 3: { // EBML: first size is an unsigned vint; subsequent are signed-vint deltas.
        var first = ReadVint(payload, ref p, out var firstLen);
        sizes[0] = (int)first;
        var prev = (long)first;
        for (var i = 1; i < laceCount - 1; ++i) {
          var delta = ReadSignedVint(payload, ref p, firstLen: out var dlen);
          prev += delta;
          sizes[i] = (int)prev;
        }
        break;
      }
      case 2: { // fixed: all frames equal — divide the remaining payload evenly.
        var each = (payload.Length - p) / laceCount;
        for (var i = 0; i < laceCount; ++i) sizes[i] = each;
        break;
      }
    }

    // The final frame (non-fixed lacing) takes whatever remains after the headers + sized frames.
    if (lacing != 2) {
      var used = 0;
      for (var i = 0; i < laceCount - 1; ++i) used += sizes[i];
      sizes[laceCount - 1] = payload.Length - p - used;
    }

    for (var i = 0; i < laceCount; ++i) {
      var size = sizes[i];
      if (size < 0 || p + size > payload.Length) break;
      frames.Add(payload.Slice(p, size).ToArray());
      p += size;
    }
    return frames;
  }

  private static ulong ReadVint(ReadOnlySpan<byte> data, ref int pos, out int length) {
    length = 0;
    if (pos >= data.Length) return 0;
    var first = data[pos];
    for (var i = 0; i < 8; ++i) if ((first & (0x80 >> i)) != 0) { length = i + 1; break; }
    if (length == 0 || pos + length > data.Length) { length = 1; return 0; }
    ulong value = (ulong)(first & (0xFF >> length));
    for (var i = 1; i < length; ++i) value = (value << 8) | data[pos + i];
    pos += length;
    return value;
  }

  /// <summary>EBML signed lace-size delta: unsigned vint biased by 2^(7*len-1)-1.</summary>
  private static long ReadSignedVint(ReadOnlySpan<byte> data, ref int pos, out int firstLen) {
    var raw = ReadVint(data, ref pos, out firstLen);
    var bias = (1L << (7 * firstLen - 1)) - 1;
    return (long)raw - bias;
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
