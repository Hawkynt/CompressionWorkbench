#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Avi;

/// <summary>
/// RIFF/AVI container demuxer. Walks the tree — <c>RIFF</c> → <c>AVI </c> → both
/// <c>LIST/hdrl</c> (<c>avih</c> + one <c>LIST/strl</c> per stream) and
/// <c>LIST/movi</c> (the actual chunk data, 4-char stream-id prefixed). Tracks are
/// returned with their FourCC, BITMAPINFOHEADER / WAVEFORMATEX payload, and a
/// concatenation of sample bytes plus individual frame chunks in <c>Chunks</c>.
/// </summary>
public sealed class AviReader {
  /// <summary>One movi chunk belonging to a track (a single video frame or audio packet).</summary>
  public sealed record ChunkEntry(string ChunkId, byte[] Data);

  /// <summary>
  /// One movi chunk in file order. <paramref name="IndexFlags"/> is populated from
  /// a matching legacy <c>idx1</c> entry when present.
  /// </summary>
  public sealed record InterleavedChunk(int StreamIndex, string ChunkId, byte[] Data, uint? IndexFlags = null);

  /// <summary>
  /// Represents a track.
  /// </summary>
  public sealed record Track(
    int Index,
    string StreamType,
    uint Handler,
    byte[] Format,
    int Width,
    int Height,
    int AudioChannels,
    int AudioSampleRate,
    int AudioBitsPerSample,
    int AudioFormatTag,
    int AudioBlockAlign,
    byte[] Data,
    IReadOnlyList<ChunkEntry> Chunks) {
    /// <summary>Raw <c>strh</c> body, retained so a remux can preserve stream timing and flags.</summary>
    public byte[] StreamHeader { get; init; } = [];
  }

  /// <summary>
  /// Represents a parsed avi.
  /// </summary>
  public sealed record ParsedAvi(
    int Width,
    int Height,
    uint MicroSecPerFrame,
    uint TotalFrames,
    IReadOnlyList<Track> Tracks) {
    /// <summary>Raw <c>avih</c> body, retained so a remux can preserve non-derived header fields.</summary>
    public byte[] MainHeader { get; init; } = [];

    /// <summary>Recognised movi chunks in their original global interleave order.</summary>
    public IReadOnlyList<InterleavedChunk> MoviChunks { get; init; } = [];
  }

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedAvi Read(ReadOnlySpan<byte> data) {
    if (data.Length < 12)
      throw new InvalidDataException("AVI too short for RIFF header.");
    if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
      throw new InvalidDataException("Missing RIFF magic.");
    if (data[8] != 'A' || data[9] != 'V' || data[10] != 'I' || data[11] != ' ')
      throw new InvalidDataException("RIFF payload is not AVI.");

    var strls = new List<(int Off, int Size)>();
    ReadOnlySpan<byte> movi = default;
    ReadOnlySpan<byte> idx1 = default;
    var avihBytes = ReadOnlySpan<byte>.Empty;

    var pos = 12;
    while (pos + 8 <= data.Length) {
      var id = Encoding.ASCII.GetString(data.Slice(pos, 4));
      var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
      if (rawSize > int.MaxValue) break;
      var size = (int)rawSize;
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
      } else if (id == "idx1") {
        idx1 = data.Slice(bodyStart, size);
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

    var tracks = new List<Track>();
    for (var i = 0; i < strls.Count; ++i) {
      var (off, size) = strls[i];
      var body = data.Slice(off, size);
      tracks.Add(ParseStrl(i, body));
    }

    var interleaved = new List<InterleavedChunk>();
    if (!movi.IsEmpty) {
      var buffers = new Dictionary<int, MemoryStream>();
      var chunkLists = new Dictionary<int, List<ChunkEntry>>();
      for (var i = 0; i < tracks.Count; ++i) {
        buffers[i] = new MemoryStream();
        chunkLists[i] = new List<ChunkEntry>();
      }

      AppendChunks(movi, tracks.Count, buffers, chunkLists, interleaved);

      for (var i = 0; i < tracks.Count; ++i)
        tracks[i] = tracks[i] with { Data = buffers[i].ToArray(), Chunks = chunkLists[i] };
    }

    ApplyLegacyIndexFlags(idx1, interleaved);

    return new ParsedAvi(width, height, uspf, totalFrames, tracks) {
      MainHeader = avihBytes.ToArray(),
      MoviChunks = interleaved,
    };
  }

  private static void ParseHdrl(ReadOnlySpan<byte> hdrl, List<(int, int)> strls,
                                 int hdrlAbsoluteOffset, out ReadOnlySpan<byte> avih) {
    avih = ReadOnlySpan<byte>.Empty;
    var p = 0;
    while (p + 8 <= hdrl.Length) {
      var id = Encoding.ASCII.GetString(hdrl.Slice(p, 4));
      var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(hdrl[(p + 4)..]);
      if (rawSize > int.MaxValue) break;
      var size = (int)rawSize;
      var bodyStart = p + 8;
      if (bodyStart + size > hdrl.Length) break;
      if (id == "avih") {
        avih = hdrl.Slice(bodyStart, size);
      } else if (id == "LIST" && size >= 4) {
        var listType = Encoding.ASCII.GetString(hdrl.Slice(bodyStart, 4));
        if (listType == "strl")
          strls.Add((hdrlAbsoluteOffset + bodyStart + 4, size - 4));
      }
      p = bodyStart + size + (size & 1);
    }
  }

  private static Track ParseStrl(int index, ReadOnlySpan<byte> strl) {
    var streamType = "unk";
    uint handler = 0;
    byte[] streamHeader = [];
    byte[] format = [];
    int w = 0, h = 0, ch = 0, sr = 0, bps = 0, fmtTag = 0, blockAlign = 0;

    var p = 0;
    while (p + 8 <= strl.Length) {
      var id = Encoding.ASCII.GetString(strl.Slice(p, 4));
      var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(strl[(p + 4)..]);
      if (rawSize > int.MaxValue) break;
      var size = (int)rawSize;
      var bodyStart = p + 8;
      if (bodyStart + size > strl.Length) break;

      if (id == "strh" && size >= 56) {
        streamHeader = strl.Slice(bodyStart, size).ToArray();
        streamType = Encoding.ASCII.GetString(strl.Slice(bodyStart, 4));
        handler = BinaryPrimitives.ReadUInt32LittleEndian(strl[(bodyStart + 4)..]);
      } else if (id == "strf") {
        format = strl.Slice(bodyStart, size).ToArray();
        if (streamType == "vids" && format.Length >= 40) {
          w = BinaryPrimitives.ReadInt32LittleEndian(format.AsSpan(4));
          h = BinaryPrimitives.ReadInt32LittleEndian(format.AsSpan(8));
        } else if (streamType == "auds" && format.Length >= 16) {
          fmtTag = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(0));
          ch = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
          var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
          sr = sampleRate > int.MaxValue ? int.MaxValue : (int)sampleRate;
          blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12));
          bps = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
        }
      }
      p = bodyStart + size + (size & 1);
    }

    return new Track(index, streamType, handler, format, w, h, ch, sr, bps, fmtTag, blockAlign, [], []) {
      StreamHeader = streamHeader,
    };
  }

  private static void AppendChunks(ReadOnlySpan<byte> area, int trackCount,
                                    Dictionary<int, MemoryStream> buffers,
                                    Dictionary<int, List<ChunkEntry>> chunkLists,
                                    List<InterleavedChunk> interleaved) {
    var mp = 0;
    while (mp + 8 <= area.Length) {
      var cid = Encoding.ASCII.GetString(area.Slice(mp, 4));
      var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(area[(mp + 4)..]);
      if (rawSize > int.MaxValue) break;
      var csize = (int)rawSize;
      var cbodyStart = mp + 8;
      if (cbodyStart + csize > area.Length) break;

      if (cid == "LIST" && csize >= 4) {
        var listType = Encoding.ASCII.GetString(area.Slice(cbodyStart, 4));
        if (listType == "rec ")
          AppendChunks(area.Slice(cbodyStart + 4, csize - 4), trackCount, buffers, chunkLists, interleaved);
      } else if (TryGetStreamIndex(cid, trackCount, out var streamIdx)
                 && buffers.TryGetValue(streamIdx, out var buf)) {
        var chunkData = area.Slice(cbodyStart, csize).ToArray();
        buf.Write(chunkData);
        chunkLists[streamIdx].Add(new ChunkEntry(cid, chunkData));
        interleaved.Add(new InterleavedChunk(streamIdx, cid, chunkData));
      }

      mp = cbodyStart + csize + (csize & 1);
    }
  }

  private static bool TryGetStreamIndex(string chunkId, int trackCount, out int streamIndex) {
    streamIndex = -1;
    if (chunkId.Length != 4 || !char.IsAsciiDigit(chunkId[0]) || !char.IsAsciiDigit(chunkId[1]))
      return false;
    streamIndex = (chunkId[0] - '0') * 10 + chunkId[1] - '0';
    return streamIndex < trackCount;
  }

  private static void ApplyLegacyIndexFlags(ReadOnlySpan<byte> idx1, List<InterleavedChunk> chunks) {
    if (idx1.IsEmpty || chunks.Count == 0)
      return;

    var chunkIndex = 0;
    for (var p = 0; p + 16 <= idx1.Length && chunkIndex < chunks.Count; p += 16) {
      var chunkId = Encoding.ASCII.GetString(idx1.Slice(p, 4));
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(idx1[(p + 4)..]);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(idx1[(p + 12)..]);
      if ((flags & 0x00000001) != 0)
        continue;

      for (; chunkIndex < chunks.Count; ++chunkIndex) {
        var chunk = chunks[chunkIndex];
        if (!chunk.ChunkId.Equals(chunkId, StringComparison.Ordinal)
            || size != chunk.Data.LongLength)
          continue;
        chunks[chunkIndex] = chunk with { IndexFlags = flags };
        ++chunkIndex;
        break;
      }
    }
  }
}
