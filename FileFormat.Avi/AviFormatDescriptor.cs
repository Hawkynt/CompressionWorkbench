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
public sealed class AviFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

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

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    var parsed = new AviReader().Read(blob);

    var entries = new List<(string, string, byte[])> {
      ("FULL.avi", "Track", blob),
    };

    for (var i = 0; i < parsed.Tracks.Count; ++i) {
      var t = parsed.Tracks[i];
      if (t.StreamType == "vids") {
        var ext = VideoFourCcToExtension(t.Handler);
        entries.Add(($"track_{i:D2}_video{ext}", "Track", t.Data));
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
