#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Avi;

/// <summary>
/// AVI 1.0 RIFF muxer. Writes a canonical hdrl/movi/idx1 layout while preserving
/// encoded packet bytes, stream format blocks, stream timing headers, interleave
/// order, and legacy index flags whenever <see cref="AviReader"/> exposed them.
/// </summary>
public static class AviWriter {
  private const uint AvifHasIndex = 0x00000010;
  private const uint AvifIsInterleaved = 0x00000100;
  private const uint AviifKeyFrame = 0x00000010;
  private const uint AviifNoTime = 0x00000100;

  private sealed record PreparedChunk(int StreamIndex, string ChunkId, byte[] Data, uint Flags);

  /// <summary>Writes <paramref name="avi"/> to <paramref name="output"/> as an AVI 1.0 RIFF file.</summary>
  /// <remarks>OpenDML/AVIX segmentation is deliberately not emitted; files exceeding the RIFF 32-bit size limit are refused.</remarks>
  public static void Write(Stream output, AviReader.ParsedAvi avi) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(avi);
    if (!output.CanWrite)
      throw new ArgumentException("Output stream must be writable.", nameof(output));
    if (avi.Tracks.Count is 0 or > 99)
      throw new NotSupportedException("AVI muxing requires between 1 and 99 streams.");

    var chunks = PrepareChunks(avi);
    var stats = BuildTrackStats(avi.Tracks.Count, chunks);
    var hdrl = BuildHeaderList(avi, stats);

    long moviChildrenSize = 0;
    foreach (var chunk in chunks)
      moviChildrenSize = checked(moviChildrenSize + ChunkStorageSize(chunk.Data.Length));
    var moviBodySize = checked(4L + moviChildrenSize);
    var moviStorageSize = checked(8L + moviBodySize + (moviBodySize & 1));

    var idx1BodySize = checked((long)chunks.Count * 16);
    var idx1StorageSize = checked(8L + idx1BodySize + (idx1BodySize & 1));

    var riffBodySize = checked(4L + hdrl.LongLength + moviStorageSize + idx1StorageSize);
    if (riffBodySize > uint.MaxValue || moviBodySize > uint.MaxValue || idx1BodySize > uint.MaxValue)
      throw new NotSupportedException("AVI 1.0 is limited to 32-bit RIFF sizes; OpenDML/AVIX output is not implemented.");

    WriteFourCc(output, "RIFF");
    WriteUInt32(output, (uint)riffBodySize);
    WriteFourCc(output, "AVI ");
    output.Write(hdrl);

    WriteFourCc(output, "LIST");
    WriteUInt32(output, (uint)moviBodySize);
    WriteFourCc(output, "movi");

    var offsets = new uint[chunks.Count];
    long relativeOffset = 4; // relative to the start of the movi list payload, including its list type.
    for (var i = 0; i < chunks.Count; ++i) {
      var chunk = chunks[i];
      offsets[i] = checked((uint)relativeOffset);
      WriteFourCc(output, chunk.ChunkId);
      WriteUInt32(output, checked((uint)chunk.Data.Length));
      output.Write(chunk.Data);
      if ((chunk.Data.Length & 1) != 0)
        output.WriteByte(0);
      relativeOffset = checked(relativeOffset + ChunkStorageSize(chunk.Data.Length));
    }

    WriteFourCc(output, "idx1");
    WriteUInt32(output, checked((uint)idx1BodySize));
    for (var i = 0; i < chunks.Count; ++i) {
      var chunk = chunks[i];
      WriteFourCc(output, chunk.ChunkId);
      WriteUInt32(output, chunk.Flags);
      WriteUInt32(output, offsets[i]);
      WriteUInt32(output, checked((uint)chunk.Data.Length));
    }
    if ((idx1BodySize & 1) != 0)
      output.WriteByte(0);
  }

  private static List<PreparedChunk> PrepareChunks(AviReader.ParsedAvi avi) {
    var result = new List<PreparedChunk>();

    if (avi.MoviChunks.Count > 0) {
      foreach (var chunk in avi.MoviChunks) {
        if ((uint)chunk.StreamIndex >= (uint)avi.Tracks.Count)
          continue;
        var track = avi.Tracks[chunk.StreamIndex];
        result.Add(new PreparedChunk(
          chunk.StreamIndex,
          NormalizeChunkId(chunk.StreamIndex, chunk.ChunkId, track),
          chunk.Data,
          chunk.IndexFlags ?? InferIndexFlags(track, chunk.ChunkId)));
      }
      return result;
    }

    var maxChunks = avi.Tracks.Max(static track => track.Chunks.Count);
    for (var packet = 0; packet < maxChunks; ++packet) {
      for (var streamIndex = 0; streamIndex < avi.Tracks.Count; ++streamIndex) {
        var track = avi.Tracks[streamIndex];
        if (packet >= track.Chunks.Count) continue;
        var chunk = track.Chunks[packet];
        result.Add(new PreparedChunk(
          streamIndex,
          NormalizeChunkId(streamIndex, chunk.ChunkId, track),
          chunk.Data,
          InferIndexFlags(track, chunk.ChunkId)));
      }
    }

    for (var streamIndex = 0; streamIndex < avi.Tracks.Count; ++streamIndex) {
      var track = avi.Tracks[streamIndex];
      if (track.Chunks.Count != 0 || track.Data.Length == 0) continue;
      var chunkId = DefaultChunkId(streamIndex, track);
      result.Add(new PreparedChunk(streamIndex, chunkId, track.Data, InferIndexFlags(track, chunkId)));
    }

    return result;
  }

  private sealed record TrackStats(int ChunkCount, long DataBytes, int MaxChunkSize);

  private static TrackStats[] BuildTrackStats(int trackCount, IReadOnlyList<PreparedChunk> chunks) {
    var counts = new int[trackCount];
    var bytes = new long[trackCount];
    var maxima = new int[trackCount];
    foreach (var chunk in chunks) {
      counts[chunk.StreamIndex] = checked(counts[chunk.StreamIndex] + 1);
      bytes[chunk.StreamIndex] = checked(bytes[chunk.StreamIndex] + chunk.Data.LongLength);
      maxima[chunk.StreamIndex] = Math.Max(maxima[chunk.StreamIndex], chunk.Data.Length);
    }

    var result = new TrackStats[trackCount];
    for (var i = 0; i < trackCount; ++i)
      result[i] = new TrackStats(counts[i], bytes[i], maxima[i]);
    return result;
  }

  private static byte[] BuildHeaderList(AviReader.ParsedAvi avi, IReadOnlyList<TrackStats> stats) {
    var children = new List<byte[]>(avi.Tracks.Count + 1) {
      BuildChunk("avih", BuildMainHeader(avi, stats)),
    };
    for (var i = 0; i < avi.Tracks.Count; ++i) {
      var track = avi.Tracks[i];
      children.Add(BuildList("strl",
        BuildChunk("strh", BuildStreamHeader(avi, track, stats[i])),
        BuildChunk("strf", track.Format)));
    }
    return BuildList("hdrl", [.. children]);
  }

  private static byte[] BuildMainHeader(AviReader.ParsedAvi avi, IReadOnlyList<TrackStats> stats) {
    var header = avi.MainHeader.Length >= 56 ? avi.MainHeader[..56].ToArray() : new byte[56];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), avi.MicroSecPerFrame);

    var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
    flags |= AvifHasIndex;
    flags = avi.Tracks.Count > 1 ? flags | AvifIsInterleaved : flags & ~AvifIsInterleaved;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), flags);

    var firstVideo = -1;
    for (var i = 0; i < avi.Tracks.Count; ++i)
      if (avi.Tracks[i].StreamType == "vids") { firstVideo = i; break; }
    var totalFrames = firstVideo >= 0 && stats[firstVideo].ChunkCount > 0
      ? checked((uint)stats[firstVideo].ChunkCount)
      : avi.TotalFrames;

    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), totalFrames);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), checked((uint)avi.Tracks.Count));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), checked((uint)stats.Max(static stat => stat.MaxChunkSize)));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), checked((uint)Math.Max(avi.Width, 0)));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), checked((uint)Math.Max(avi.Height, 0)));
    return header;
  }

  private static byte[] BuildStreamHeader(AviReader.ParsedAvi avi, AviReader.Track track, TrackStats stats) {
    var header = track.StreamHeader.Length >= 56 ? track.StreamHeader[..56].ToArray() : new byte[56];
    WriteFourCc(header.AsSpan(0, 4), NormalizeStreamType(track.StreamType));
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), track.Handler);

    if (track.StreamHeader.Length < 56) {
      switch (track.StreamType) {
        case "vids":
          var scale = avi.MicroSecPerFrame == 0 ? 1u : avi.MicroSecPerFrame;
          var rate = avi.MicroSecPerFrame == 0 ? 25u : 1_000_000u;
          var gcd = GreatestCommonDivisor(scale, rate);
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), scale / gcd);
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), rate / gcd);
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), uint.MaxValue);
          BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(52), checked((short)Math.Clamp(track.Width, 0, short.MaxValue)));
          BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(54), checked((short)Math.Clamp(track.Height, 0, short.MaxValue)));
          break;

        case "auds":
          var blockAlign = track.AudioBlockAlign > 0 ? track.AudioBlockAlign
            : track.Format.Length >= 14 ? BinaryPrimitives.ReadUInt16LittleEndian(track.Format.AsSpan(12))
            : 1;
          var averageBytesPerSecond = track.Format.Length >= 12
            ? BinaryPrimitives.ReadUInt32LittleEndian(track.Format.AsSpan(8))
            : checked((uint)Math.Max(1L, (long)Math.Max(track.AudioSampleRate, 1) * blockAlign));
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), checked((uint)Math.Max(blockAlign, 1)));
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), Math.Max(averageBytesPerSecond, 1));
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), uint.MaxValue);
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), checked((uint)Math.Max(blockAlign, 1)));
          break;

        default:
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 1);
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 1);
          BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), uint.MaxValue);
          break;
      }
    }

    var sampleSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44));
    uint length;
    if (track.StreamType == "vids")
      length = checked((uint)stats.ChunkCount);
    else if (sampleSize > 0)
      length = checked((uint)((stats.DataBytes + sampleSize - 1) / sampleSize));
    else {
      var preserved = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
      length = preserved != 0 ? preserved : checked((uint)stats.ChunkCount);
    }

    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), checked((uint)stats.MaxChunkSize));
    return header;
  }

  private static byte[] BuildChunk(string id, byte[] body) {
    var paddedBodyLength = checked(body.Length + (body.Length & 1));
    var result = new byte[checked(8 + paddedBodyLength)];
    WriteFourCc(result.AsSpan(0, 4), id);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)body.Length));
    body.CopyTo(result.AsSpan(8));
    return result;
  }

  private static byte[] BuildList(string listType, params byte[][] children) {
    long childrenSize = 0;
    foreach (var child in children)
      childrenSize = checked(childrenSize + child.LongLength);
    var bodyLength = checked(4L + childrenSize);
    if (bodyLength > uint.MaxValue || bodyLength > int.MaxValue - 8L)
      throw new NotSupportedException("AVI header list exceeds the RIFF 32-bit chunk limit.");

    var result = new byte[checked((int)(8 + bodyLength + (bodyLength & 1)))];
    WriteFourCc(result.AsSpan(0, 4), "LIST");
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)bodyLength);
    WriteFourCc(result.AsSpan(8, 4), listType);
    var offset = 12;
    foreach (var child in children) {
      child.CopyTo(result.AsSpan(offset));
      offset += child.Length;
    }
    return result;
  }

  private static long ChunkStorageSize(int bodyLength) => checked(8L + bodyLength + (bodyLength & 1));

  private static string NormalizeChunkId(int streamIndex, string chunkId, AviReader.Track track) {
    var suffix = chunkId.Length == 4 ? chunkId[2..] : DefaultSuffix(track);
    if (suffix.Length != 2 || suffix.Any(static c => c is < ' ' or > '~'))
      suffix = DefaultSuffix(track);
    return $"{streamIndex:D2}{suffix}";
  }

  private static string DefaultChunkId(int streamIndex, AviReader.Track track) => $"{streamIndex:D2}{DefaultSuffix(track)}";

  private static string DefaultSuffix(AviReader.Track track) => track.StreamType switch {
    "auds" => "wb",
    "vids" when IsUncompressedVideo(track.Handler) => "db",
    "vids" => "dc",
    _ => "db",
  };

  private static uint InferIndexFlags(AviReader.Track track, string chunkId) {
    if (chunkId.EndsWith("pc", StringComparison.Ordinal))
      return AviifNoTime;
    if (track.StreamType == "auds" || IsUncompressedVideo(track.Handler) || FourCcEquals(track.Handler, "MJPG"))
      return AviifKeyFrame;
    return 0;
  }

  private static string NormalizeStreamType(string streamType)
    => streamType.Length == 4 && streamType.All(static c => c is >= ' ' and <= '~') ? streamType : "data";

  private static bool IsUncompressedVideo(uint handler)
    => handler == 0
       || FourCcEquals(handler, "DIB ")
       || FourCcEquals(handler, "RGB ")
       || FourCcEquals(handler, "RAW ")
       || FourCcEquals(handler, "NONE")
       || FourCcEquals(handler, "    ");

  private static bool FourCcEquals(uint value, string text)
    => text.Length == 4
       && (byte)value == (byte)text[0]
       && (byte)(value >> 8) == (byte)text[1]
       && (byte)(value >> 16) == (byte)text[2]
       && (byte)(value >> 24) == (byte)text[3];

  private static uint GreatestCommonDivisor(uint left, uint right) {
    while (right != 0)
      (left, right) = (right, left % right);
    return left == 0 ? 1 : left;
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteFourCc(Stream output, string value) {
    Span<byte> bytes = stackalloc byte[4];
    WriteFourCc(bytes, value);
    output.Write(bytes);
  }

  private static void WriteFourCc(Span<byte> destination, string value) {
    if (destination.Length < 4)
      throw new ArgumentException("FourCC destination must be at least four bytes.", nameof(destination));
    if (value.Length != 4 || value.Any(static c => c is < ' ' or > '~'))
      throw new InvalidDataException($"'{value}' is not a printable four-character code.");
    for (var i = 0; i < 4; ++i)
      destination[i] = checked((byte)value[i]);
  }
}
