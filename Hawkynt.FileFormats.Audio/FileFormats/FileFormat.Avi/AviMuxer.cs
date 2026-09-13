#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.Avi;

/// <summary>
/// Bridges the archive-style extracted AVI surface back into <see cref="AviWriter"/>.
/// Existing AVI inputs are parsed and genuinely remuxed; elementary creation accepts
/// per-frame video plus PCM audio using the descriptor's metadata.ini schema.
/// </summary>
internal static class AviMuxer {
  public static void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var files = inputs.Where(static input => !input.IsDirectory).ToArray();
    var aviInput = files.FirstOrDefault(static input =>
      Path.GetExtension(input.ArchiveName).Equals(".avi", StringComparison.OrdinalIgnoreCase));
    if (aviInput != null) {
      var parsed = new AviReader().Read(aviInput.ReadContent());
      AviWriter.Write(output, parsed);
      return;
    }

    var metadataInput = files.FirstOrDefault(static input =>
      Path.GetFileName(input.ArchiveName).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase));
    if (metadataInput == null)
      throw new InvalidOperationException("AVI Create requires an AVI input to remux or metadata.ini plus elementary track inputs.");

    var metadata = ParseMetadata(Encoding.UTF8.GetString(metadataInput.ReadContent()));
    var trackCount = GetInt(metadata, "track_count", 0);
    if (trackCount is <= 0 or > 99)
      throw new InvalidOperationException("metadata.ini must declare track_count between 1 and 99.");

    var width = GetInt(metadata, "width", 0);
    var height = GetInt(metadata, "height", 0);
    var microsecondsPerFrame = GetUInt(metadata, "microseconds_per_frame", 0);
    var totalFrames = GetUInt(metadata, "total_frames", 0);

    var videoPacketTarget = 1;
    for (var i = 0; i < trackCount; ++i) {
      if (!Get(metadata, $"track_{i}.type").Equals("vids", StringComparison.Ordinal))
        continue;
      var frameInputs = FindFrameInputs(files, i);
      videoPacketTarget = Math.Max(videoPacketTarget,
        frameInputs.Length > 0 ? frameInputs.Length : GetInt(metadata, $"track_{i}.frame_count", 1));
    }

    var tracks = new List<AviReader.Track>(trackCount);
    for (var i = 0; i < trackCount; ++i) {
      var streamType = Get(metadata, $"track_{i}.type");
      if (streamType.Length != 4)
        throw new InvalidOperationException($"metadata.ini is missing a four-character track_{i}.type.");

      tracks.Add(streamType switch {
        "vids" => BuildVideoTrack(files, metadata, i, width, height),
        "auds" => BuildAudioTrack(files, metadata, i, videoPacketTarget),
        _ => throw new NotSupportedException($"AVI elementary muxing does not yet support stream type '{streamType}'."),
      });
    }

    var parsedAvi = new AviReader.ParsedAvi(width, height, microsecondsPerFrame, totalFrames, tracks);
    AviWriter.Write(output, parsedAvi);
  }

  private static AviReader.Track BuildVideoTrack(
      IReadOnlyList<ArchiveInputInfo> files,
      IReadOnlyDictionary<string, string> metadata,
      int index,
      int globalWidth,
      int globalHeight) {
    var width = GetInt(metadata, $"track_{index}.width", globalWidth);
    var height = GetInt(metadata, $"track_{index}.height", globalHeight);
    if (width <= 0 || height == 0)
      throw new InvalidOperationException($"Video track {index} requires non-zero width and height.");

    var handler = ParseFourCc(Get(metadata, $"track_{index}.fourcc"));
    var frameInputs = FindFrameInputs(files, index);
    var chunks = new List<AviReader.ChunkEntry>();

    if (frameInputs.Length > 0) {
      foreach (var input in frameInputs) {
        var data = input.ReadContent();
        if (IsUncompressedVideo(handler))
          data = UnwrapBmp(data);
        chunks.Add(new AviReader.ChunkEntry($"{index:D2}{(IsUncompressedVideo(handler) ? "db" : "dc")}", data));
      }
    } else {
      var trackInput = FindTrackInput(files, $"track_{index:D2}_video");
      if (trackInput == null)
        throw new InvalidOperationException($"Video track {index} requires frames/track_{index:D2}/frame_* entries or a track_{index:D2}_video payload.");
      var declaredFrames = GetInt(metadata, $"track_{index}.frame_count", 1);
      if (declaredFrames > 1)
        throw new InvalidOperationException(
          $"Video track {index} has {declaredFrames} frames but only a concatenated track payload; frame entries are required to recover AVI chunk boundaries.");
      var data = trackInput.ReadContent();
      chunks.Add(new AviReader.ChunkEntry($"{index:D2}{(IsUncompressedVideo(handler) ? "db" : "dc")}", data));
    }

    var bitCount = 24;
    if (frameInputs.Length > 0 && IsUncompressedVideo(handler))
      bitCount = ReadBmpBitCount(frameInputs[0].ReadContent());
    var format = BuildBitmapInfoHeader(width, height, bitCount, handler);
    var dataBlob = Concatenate(chunks);

    return new AviReader.Track(
      index, "vids", handler, format, width, height,
      0, 0, 0, 0, 0, dataBlob, chunks);
  }

  private static AviReader.Track BuildAudioTrack(
      IReadOnlyList<ArchiveInputInfo> files,
      IReadOnlyDictionary<string, string> metadata,
      int index,
      int packetTarget) {
    var formatTag = GetInt(metadata, $"track_{index}.format_tag", 0);
    if (formatTag != 1)
      throw new NotSupportedException(
        $"AVI elementary audio muxing currently supports PCM (format tag 0x0001); track {index} declares 0x{formatTag:X4}. Remux an existing AVI to preserve compressed audio.");

    var channels = GetInt(metadata, $"track_{index}.channels", 0);
    var sampleRate = GetInt(metadata, $"track_{index}.sample_rate", 0);
    var bitsPerSample = GetInt(metadata, $"track_{index}.bits_per_sample", 0);
    if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0)
      throw new InvalidOperationException($"PCM track {index} requires channels, sample_rate and bits_per_sample.");

    var bytesPerSample = checked((bitsPerSample + 7) / 8);
    var blockAlign = checked(channels * bytesPerSample);
    if (blockAlign > ushort.MaxValue)
      throw new NotSupportedException($"PCM track {index} block alignment exceeds WAVEFORMATEX.");

    var input = FindTrackInput(files, $"track_{index:D2}_audio");
    if (input == null)
      throw new InvalidOperationException($"PCM track {index} requires a track_{index:D2}_audio payload.");

    var pcm = ExtractWaveData(input.ReadContent());
    if (pcm.Length % blockAlign != 0)
      throw new InvalidDataException($"PCM track {index} data length is not aligned to {blockAlign} bytes.");

    var chunks = SplitPcm(index, pcm, blockAlign, packetTarget);
    var averageBytesPerSecond = checked((uint)((long)sampleRate * blockAlign));
    var format = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(0), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2), checked((ushort)channels));
    BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4), checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(8), averageBytesPerSecond);
    BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12), checked((ushort)blockAlign));
    BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14), checked((ushort)bitsPerSample));

    return new AviReader.Track(
      index, "auds", 0, format, 0, 0,
      channels, sampleRate, bitsPerSample, 1, blockAlign, pcm, chunks);
  }

  private static IReadOnlyList<AviReader.ChunkEntry> SplitPcm(int streamIndex, byte[] pcm, int blockAlign, int packetTarget) {
    if (pcm.Length == 0)
      return [];

    var blocks = pcm.Length / blockAlign;
    var chunkCount = Math.Min(Math.Max(packetTarget, 1), blocks);
    var baseBlocks = blocks / chunkCount;
    var extraBlocks = blocks % chunkCount;
    var chunks = new List<AviReader.ChunkEntry>(chunkCount);
    var offset = 0;

    for (var i = 0; i < chunkCount; ++i) {
      var chunkBlocks = baseBlocks + (i < extraBlocks ? 1 : 0);
      var length = checked(chunkBlocks * blockAlign);
      chunks.Add(new AviReader.ChunkEntry($"{streamIndex:D2}wb", pcm.AsSpan(offset, length).ToArray()));
      offset += length;
    }
    return chunks;
  }

  private static ArchiveInputInfo[] FindFrameInputs(IReadOnlyList<ArchiveInputInfo> files, int trackIndex) {
    var prefix = $"frames/track_{trackIndex:D2}/frame_";
    return files
      .Where(input => NormalizePath(input.ArchiveName).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
      .OrderBy(static input => NormalizePath(input.ArchiveName), StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  private static ArchiveInputInfo? FindTrackInput(IReadOnlyList<ArchiveInputInfo> files, string prefix)
    => files.FirstOrDefault(input =>
      Path.GetFileName(NormalizePath(input.ArchiveName)).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

  private static string NormalizePath(string path) => path.Replace('\\', '/');

  private static byte[] Concatenate(IReadOnlyList<AviReader.ChunkEntry> chunks) {
    var total = 0;
    foreach (var chunk in chunks)
      total = checked(total + chunk.Data.Length);
    var result = new byte[total];
    var offset = 0;
    foreach (var chunk in chunks) {
      chunk.Data.CopyTo(result.AsSpan(offset));
      offset += chunk.Data.Length;
    }
    return result;
  }

  private static byte[] BuildBitmapInfoHeader(int width, int height, int bitCount, uint compression) {
    var result = new byte[40];
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), 40);
    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), width);
    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), height);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), checked((ushort)Math.Clamp(bitCount, 1, ushort.MaxValue)));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), compression);
    return result;
  }

  private static int ReadBmpBitCount(ReadOnlySpan<byte> bmp)
    => bmp.Length >= 30 && bmp[0] == 'B' && bmp[1] == 'M'
      ? BinaryPrimitives.ReadUInt16LittleEndian(bmp[28..])
      : 24;

  private static byte[] UnwrapBmp(byte[] data) {
    if (data.Length < 14 || data[0] != 'B' || data[1] != 'M')
      return data;
    var offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10));
    if (offset > data.Length)
      throw new InvalidDataException("BMP frame pixel offset exceeds the frame length.");
    return data.AsSpan((int)offset).ToArray();
  }

  private static byte[] ExtractWaveData(byte[] data) {
    if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
      return data;

    var offset = 12;
    while (offset + 8 <= data.Length) {
      var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
      var body = offset + 8;
      if (size > int.MaxValue || body + (long)size > data.Length)
        throw new InvalidDataException("WAV chunk exceeds the audio track payload.");
      if (data.AsSpan(offset, 4).SequenceEqual("data"u8))
        return data.AsSpan(body, (int)size).ToArray();
      offset = checked(body + (int)size + ((int)size & 1));
    }
    throw new InvalidDataException("PCM WAV track has no data chunk.");
  }

  private static Dictionary<string, string> ParseMetadata(string text) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0 || line[0] is '#' or ';' or '[')
        continue;
      var equals = line.IndexOf('=');
      if (equals <= 0)
        continue;
      result[line[..equals].Trim()] = line[(equals + 1)..].Trim();
    }
    return result;
  }

  private static string Get(IReadOnlyDictionary<string, string> metadata, string key)
    => metadata.TryGetValue(key, out var value) ? value : string.Empty;

  private static int GetInt(IReadOnlyDictionary<string, string> metadata, string key, int fallback) {
    if (!metadata.TryGetValue(key, out var text))
      return fallback;
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hexadecimal))
      return hexadecimal;
    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
  }

  private static uint GetUInt(IReadOnlyDictionary<string, string> metadata, string key, uint fallback)
    => metadata.TryGetValue(key, out var text)
       && uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
      ? value
      : fallback;

  private static uint ParseFourCc(string text) {
    if (text.Length != 4 || text == "????")
      return 0;
    return (uint)(byte)text[0]
           | (uint)(byte)text[1] << 8
           | (uint)(byte)text[2] << 16
           | (uint)(byte)text[3] << 24;
  }

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
}
