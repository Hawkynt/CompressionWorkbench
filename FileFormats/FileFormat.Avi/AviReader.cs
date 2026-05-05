#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Avi;

/// <summary>
/// RIFF/AVI container demuxer. Walks the tree — <c>RIFF</c> → <c>AVI </c> → both
/// <c>LIST/hdrl</c> (<c>avih</c> + one <c>LIST/strl</c> per stream) and
/// <c>LIST/movi</c> (the actual chunk data, 4-char stream-id prefixed). Tracks are
/// returned with their FourCC, BITMAPINFOHEADER / WAVEFORMATEX payload, and a
/// concatenation of sample bytes (one big blob per track; individual frame
/// boundaries are not preserved in the concatenation but indexable via the
/// <c>Chunks</c> list).
/// </summary>
public sealed class AviReader {
  public sealed record Track(
    int Index,
    string StreamType,         // "vids" or "auds" (or raw FourCC)
    uint Handler,              // codec FourCC for video, format tag for audio
    byte[] Format,             // strf body (BITMAPINFOHEADER or WAVEFORMATEX)
    int Width,                 // video only
    int Height,                // video only
    int AudioChannels,         // audio only
    int AudioSampleRate,       // audio only
    int AudioBitsPerSample,    // audio only
    int AudioFormatTag,        // audio only (WAVEFORMATEX wFormatTag)
    int AudioBlockAlign,       // audio only
    byte[] Data);

  public sealed record ParsedAvi(
    int Width,
    int Height,
    uint MicroSecPerFrame,
    uint TotalFrames,
    IReadOnlyList<Track> Tracks);

  public ParsedAvi Read(ReadOnlySpan<byte> data) {
    if (data.Length < 12)
      throw new InvalidDataException("AVI too short for RIFF header.");
    if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
      throw new InvalidDataException("Missing RIFF magic.");
    if (data[8] != 'A' || data[9] != 'V' || data[10] != 'I' || data[11] != ' ')
      throw new InvalidDataException("RIFF payload is not AVI.");

    // Find LIST/hdrl and LIST/movi.
    var strls = new List<(int Off, int Size)>();
    ReadOnlySpan<byte> movi = default;
    var avihBytes = ReadOnlySpan<byte>.Empty;

    var pos = 12;
    while (pos + 8 <= data.Length) {
      var id = Encoding.ASCII.GetString(data.Slice(pos, 4));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
      var bodyStart = pos + 8;
      if (bodyStart + size > data.Length) break;

      if (id == "LIST" && size >= 4) {
        var listType = Encoding.ASCII.GetString(data.Slice(bodyStart, 4));
        var listBody = data.Slice(bodyStart + 4, size - 4);
        if (listType == "hdrl") {
          ParseHdrl(listBody, strls, bodyStart + 4, out avihBytes);
        } else if (listType == "movi") {
          movi = listBody;
        }
      }
      pos = bodyStart + size + (size & 1);
    }

    uint uspf = 0, totalFrames = 0;
    int width = 0, height = 0;
    if (avihBytes.Length >= 40) {
      uspf = BinaryPrimitives.ReadUInt32LittleEndian(avihBytes);
      totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(avihBytes[16..]);
      width = (int)BinaryPrimitives.ReadUInt32LittleEndian(avihBytes[32..]);
      height = (int)BinaryPrimitives.ReadUInt32LittleEndian(avihBytes[36..]);
    }

    // Parse each strl; use absolute offsets we captured.
    var tracks = new List<Track>();
    for (var i = 0; i < strls.Count; ++i) {
      var (off, size) = strls[i];
      var body = data.Slice(off, size);
      tracks.Add(ParseStrl(i, body));
    }

    // Walk movi and append data bytes into matching track buffers (by 2-digit stream id).
    if (!movi.IsEmpty) {
      var mp = 0;
      var buffers = new Dictionary<int, MemoryStream>();
      for (var i = 0; i < tracks.Count; ++i) buffers[i] = new MemoryStream();

      while (mp + 8 <= movi.Length) {
        var cid = Encoding.ASCII.GetString(movi.Slice(mp, 4));
        var csize = (int)BinaryPrimitives.ReadUInt32LittleEndian(movi[(mp + 4)..]);
        var cbodyStart = mp + 8;
        if (cbodyStart + csize > movi.Length) break;

        if (cid == "LIST" && csize >= 4) {
          // rec-list: recurse into it as if it were movi.
          var inner = movi.Slice(cbodyStart + 4, csize - 4);
          AppendChunks(inner, tracks.Count, buffers);
        } else if (cid.Length == 4 && char.IsDigit(cid[0]) && char.IsDigit(cid[1])) {
          var streamIdx = (cid[0] - '0') * 10 + (cid[1] - '0');
          if (buffers.TryGetValue(streamIdx, out var buf))
            buf.Write(movi.Slice(cbodyStart, csize));
        }
        mp = cbodyStart + csize + (csize & 1);
      }

      for (var i = 0; i < tracks.Count; ++i)
        tracks[i] = tracks[i] with { Data = buffers[i].ToArray() };
    }

    return new ParsedAvi(width, height, uspf, totalFrames, tracks);
  }

  private static void ParseHdrl(ReadOnlySpan<byte> hdrl, List<(int, int)> strls,
                                 int hdrlAbsoluteOffset, out ReadOnlySpan<byte> avih) {
    avih = ReadOnlySpan<byte>.Empty;
    var p = 0;
    while (p + 8 <= hdrl.Length) {
      var id = Encoding.ASCII.GetString(hdrl.Slice(p, 4));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdrl[(p + 4)..]);
      var bodyStart = p + 8;
      if (bodyStart + size > hdrl.Length) break;
      if (id == "avih") {
        avih = hdrl.Slice(bodyStart, size);
      } else if (id == "LIST" && size >= 4) {
        var listType = Encoding.ASCII.GetString(hdrl.Slice(bodyStart, 4));
        if (listType == "strl") {
          // Record absolute slice of strl body (4 bytes past "strl" tag, size-4 long).
          strls.Add((hdrlAbsoluteOffset + bodyStart + 4, size - 4));
        }
      }
      p = bodyStart + size + (size & 1);
    }
  }

  private static Track ParseStrl(int index, ReadOnlySpan<byte> strl) {
    var streamType = "unk";
    uint handler = 0;
    byte[] format = [];
    int w = 0, h = 0, ch = 0, sr = 0, bps = 0, fmtTag = 0, blockAlign = 0;

    var p = 0;
    while (p + 8 <= strl.Length) {
      var id = Encoding.ASCII.GetString(strl.Slice(p, 4));
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(strl[(p + 4)..]);
      var bodyStart = p + 8;
      if (bodyStart + size > strl.Length) break;

      if (id == "strh" && size >= 56) {
        streamType = Encoding.ASCII.GetString(strl.Slice(bodyStart, 4));
        handler = BinaryPrimitives.ReadUInt32LittleEndian(strl[(bodyStart + 4)..]);
      } else if (id == "strf") {
        format = strl.Slice(bodyStart, size).ToArray();
        if (streamType == "vids" && format.Length >= 40) {
          // BITMAPINFOHEADER: biSize(4) biWidth(4) biHeight(4) …
          w = (int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
          h = (int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8));
        } else if (streamType == "auds" && format.Length >= 16) {
          fmtTag = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(0));
          ch = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
          sr = (int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
          blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12));
          bps = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
        }
      }
      p = bodyStart + size + (size & 1);
    }

    return new Track(index, streamType, handler, format, w, h, ch, sr, bps, fmtTag, blockAlign, []);
  }

  private static void AppendChunks(ReadOnlySpan<byte> area, int trackCount,
                                    Dictionary<int, MemoryStream> buffers) {
    var mp = 0;
    while (mp + 8 <= area.Length) {
      var cid = Encoding.ASCII.GetString(area.Slice(mp, 4));
      var csize = (int)BinaryPrimitives.ReadUInt32LittleEndian(area[(mp + 4)..]);
      var cbodyStart = mp + 8;
      if (cbodyStart + csize > area.Length) break;
      if (cid.Length == 4 && char.IsDigit(cid[0]) && char.IsDigit(cid[1])) {
        var idx = (cid[0] - '0') * 10 + (cid[1] - '0');
        if (idx < trackCount && buffers.TryGetValue(idx, out var buf))
          buf.Write(area.Slice(cbodyStart, csize));
      }
      mp = cbodyStart + csize + (csize & 1);
    }
  }
}
