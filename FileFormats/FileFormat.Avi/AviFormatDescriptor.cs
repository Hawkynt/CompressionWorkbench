#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Avi;

/// <summary>
/// Exposes an AVI file as an archive: <c>FULL.avi</c>, one entry per demuxed
/// stream (video blob with codec-FourCC extension, audio blob as either a
/// synthesised WAV for PCM or raw bytes for compressed codecs), and
/// <c>metadata.ini</c> with FourCC/dimensions/duration info.
/// </summary>
public sealed class AviFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IFileInternalLayoutMap, IFileInternalChunkMover {

  public string Id => "Avi";
  public string DisplayName => "AVI (RIFF video)";
  public FormatCategory Category => FormatCategory.Video;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".avi";
  public IReadOnlyList<string> Extensions => [".avi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "RIFF" at 0 is shared with WAV, but we include the AVI tag at +8 as an additional
    // confidence hint. FormatDetector short-circuits on the longest match.
    new([(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0,
         (byte)'A', (byte)'V', (byte)'I', (byte)' '],
        Confidence: 0.95,
        Mask: [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00,
               0xFF, 0xFF, 0xFF, 0xFF]),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "AVI video container; per-track video/audio demuxing + metadata.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
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

  private readonly AviOptimizer _optimizer = new();

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) => AviLayoutMap.Enumerate(file);

  /// <inheritdoc />
  public void Optimize(Stream file) => _optimizer.Optimize(file);

  /// <inheritdoc />
  public void Optimize(Stream file, MetadataPlacementProfile? profile) => _optimizer.Optimize(file, profile);

  /// <summary>Maximum number of individual frame entries to list per video track to keep List() responsive.</summary>
  private const int MaxFrameEntries = 100_000;

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new AviReader().Read(blob);

    var entries = new List<(string, string, byte[])> {
      ("FULL.avi", "Container", blob),
    };

    for (var i = 0; i < parsed.Tracks.Count; ++i) {
      var t = parsed.Tracks[i];
      if (t.StreamType == "vids") {
        var ext = VideoFourCcToExtension(t.Handler);
        entries.Add(($"track_{i:D2}_video{ext}", "Track", t.Data));

        // Emit individual video frames.
        var frameExt = VideoFourCcToFrameExtension(t.Handler);
        var frameCount = Math.Min(t.Chunks.Count, MaxFrameEntries);
        for (var f = 0; f < frameCount; ++f) {
          var chunk = t.Chunks[f];
          var frameData = chunk.Data;

          // For uncompressed DIB/RGB video, wrap raw pixels in a BMP header.
          if (IsUncompressedVideo(t.Handler) && t.Width > 0 && t.Height > 0)
            frameData = WrapAsBmp(frameData, t.Width, t.Height, t.Format);

          entries.Add(($"frames/track_{i:D2}/frame_{f + 1:D6}{frameExt}", "Frame", frameData));
        }
      } else if (t.StreamType == "auds") {
        if (t.AudioFormatTag == 1 && t.AudioBitsPerSample is 8 or 16 or 24 or 32 && t.AudioChannels > 0) {
          // Pack raw PCM into a WAV so it's directly playable.
          var wav = PcmCodec.ToWavBlob(t.Data, t.AudioChannels, t.AudioSampleRate, t.AudioBitsPerSample);
          entries.Add(($"track_{i:D2}_audio.wav", "Track", wav));
        } else {
          var ext = AudioFormatTagToExtension(t.AudioFormatTag);
          entries.Add(($"track_{i:D2}_audio{ext}", "Track", t.Data));
        }
      } else {
        entries.Add(($"track_{i:D2}_{t.StreamType}.bin", "Track", t.Data));
      }
    }

    var info = new StringBuilder();
    info.AppendLine($"width={parsed.Width}");
    info.AppendLine($"height={parsed.Height}");
    info.AppendLine($"microseconds_per_frame={parsed.MicroSecPerFrame}");
    info.AppendLine($"total_frames={parsed.TotalFrames}");
    info.AppendLine($"track_count={parsed.Tracks.Count}");
    for (var i = 0; i < parsed.Tracks.Count; ++i) {
      var t = parsed.Tracks[i];
      info.AppendLine($"track_{i}.type={t.StreamType}");
      info.AppendLine($"track_{i}.fourcc={FourCcToString(t.Handler)}");
      if (t.StreamType == "vids") {
        info.AppendLine($"track_{i}.width={t.Width}");
        info.AppendLine($"track_{i}.height={t.Height}");
        info.AppendLine($"track_{i}.frame_count={t.Chunks.Count}");
      } else if (t.StreamType == "auds") {
        info.AppendLine($"track_{i}.channels={t.AudioChannels}");
        info.AppendLine($"track_{i}.sample_rate={t.AudioSampleRate}");
        info.AppendLine($"track_{i}.bits_per_sample={t.AudioBitsPerSample}");
        info.AppendLine($"track_{i}.format_tag=0x{t.AudioFormatTag:X4}");
      }
    }
    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static string FourCcToString(uint f) {
    Span<char> buf = stackalloc char[4];
    buf[0] = (char)(f & 0xFF);
    buf[1] = (char)((f >> 8) & 0xFF);
    buf[2] = (char)((f >> 16) & 0xFF);
    buf[3] = (char)((f >> 24) & 0xFF);
    // Replace non-printable with '?'.
    for (var i = 0; i < 4; ++i)
      if (buf[i] is < ' ' or > '~') buf[i] = '?';
    return new string(buf);
  }

  private static string VideoFourCcToExtension(uint handler) {
    var s = FourCcToString(handler).ToUpperInvariant();
    return s switch {
      "H264" or "AVC1" or "X264" => ".h264",
      "HEVC" or "H265" or "HVC1" => ".hevc",
      "MJPG" => ".mjpg",
      "DIVX" or "DX50" or "XVID" or "FMP4" => ".m4v",
      "MP42" or "MP43" => ".m4v",
      "DV  " => ".dv",
      "I420" or "YV12" or "UYVY" or "YUY2" => ".yuv",
      "    " or "RGB " or "" or "???? " => ".raw",
      _ => ".bin",
    };
  }

  /// <summary>Returns the appropriate extension for an individual video frame.</summary>
  private static string VideoFourCcToFrameExtension(uint handler) {
    if (IsUncompressedVideo(handler)) return ".bmp";
    var s = FourCcToString(handler).ToUpperInvariant();
    return s switch {
      "MJPG" => ".jpg",
      "H264" or "AVC1" or "X264" => ".h264",
      "HEVC" or "H265" or "HVC1" => ".hevc",
      _ => ".bin",
    };
  }

  /// <summary>Checks whether the FourCC indicates uncompressed RGB/DIB video.</summary>
  private static bool IsUncompressedVideo(uint handler) {
    // handler=0 means no compression (raw DIB).
    if (handler == 0) return true;
    var s = FourCcToString(handler).ToUpperInvariant();
    return s is "    " or "DIB " or "RGB " or "RAW " or "NONE" or "????";
  }

  /// <summary>
  /// Wraps raw bottom-up BGR pixel data in a BMP file header.
  /// The <paramref name="format"/> is the BITMAPINFOHEADER from the strf chunk.
  /// </summary>
  private static byte[] WrapAsBmp(byte[] rawPixels, int width, int height, byte[] format) {
    // Determine bits-per-pixel from BITMAPINFOHEADER (offset 14 = biBitCount).
    var bpp = 24;
    if (format.Length >= 16)
      bpp = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
    if (bpp is not (8 or 16 or 24 or 32)) bpp = 24;

    var absHeight = Math.Abs(height);
    var rowSize = ((width * bpp + 31) / 32) * 4;
    var pixelDataSize = rowSize * absHeight;
    var headerSize = 14 + 40; // BMP file header + DIB header
    var fileSize = headerSize + pixelDataSize;

    var bmp = new byte[fileSize];
    // BMP file header
    bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)fileSize);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)headerSize);
    // DIB header (BITMAPINFOHEADER)
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), absHeight);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1); // planes
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), (ushort)bpp);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(34), (uint)pixelDataSize);

    // Copy raw pixel data (capped to available bytes).
    var copyLen = Math.Min(rawPixels.Length, pixelDataSize);
    rawPixels.AsSpan(0, copyLen).CopyTo(bmp.AsSpan(headerSize));
    return bmp;
  }

  private static string AudioFormatTagToExtension(int tag) => tag switch {
    0x0050 or 0x0055 => ".mp3",
    0x00FF => ".aac",
    0x0002 => ".ms_adpcm",
    0x0011 => ".ima_adpcm",
    0x0006 => ".alaw",
    0x0007 => ".ulaw",
    0x0031 or 0x0032 => ".gsm",
    0x2000 => ".ac3",
    0x2001 => ".dts",
    _ => ".bin",
  };
}
