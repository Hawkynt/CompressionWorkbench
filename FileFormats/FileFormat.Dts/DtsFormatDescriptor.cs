#pragma warning disable CS1591
using System.Text;
using Codec.Dts;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Dts;

/// <summary>
/// DTS (Coherent Acoustics) elementary stream surfaced as a pseudo-archive. The descriptor parses
/// the core frame headers (AMODE channel arrangement, SFREQ sample rate, RATE bitrate, LFE flag)
/// for <c>metadata.ini</c> and walks the stream by each frame's FSIZE to count frames and estimate
/// duration. The byte-exact <c>FULL.dts</c> (Kind <c>Container</c>) always round-trips the stream
/// unchanged. The presence of a DTS-HD substream extension (the "DTSHDHDR" chunk or the 0x64582025
/// extension sync) is reported in the metadata.
/// <para>
/// In addition the descriptor decodes the DTS <em>core</em> sub-stream (via <c>Codec.Dts</c>) and
/// surfaces one mono <c>&lt;CHANNEL&gt;.wav</c> per decoded channel, named via the AMODE speaker
/// layout (document order, with LFE last). DTS-HD extension substreams (XCH / XXCH / X96 / XBR /
/// XLL) are not decoded — only the embedded core is. When the decoder can't handle the input
/// (unsupported framing, malformed, truncated) it falls back to the info-only layout
/// (<c>FULL.dts</c> + <c>metadata.ini</c>).
/// </para>
/// </summary>
public sealed class DtsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  /// <summary>DTS-HD container chunk magic ("DTSHDHDR").</summary>
  private static readonly byte[] DtsHdChunkMagic = "DTSHDHDR"u8.ToArray();

  /// <summary>DTS extension substream sync word (0x64582025, big-endian).</summary>
  private static readonly byte[] ExtensionSync = [0x64, 0x58, 0x20, 0x25];

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Dts";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "DTS (Coherent Acoustics)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Audio;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".dts";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".dts", ".dtshd"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(DtsFrameHeader.SyncWord, Confidence: 0.90),           // 7F FE 80 01 core sync
    new(DtsHdChunkMagic, Confidence: 0.95),                   // "DTSHDHDR" container chunk
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("dts", "DTS")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "DTS Coherent Acoustics elementary stream; per-channel WAVs for the core (DTS-HD extensions info-only).";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

    /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.dts", "Container", blob, "dts"),
    };

    AddDecodedChannels(blob, entries);
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(BuildMetadata(blob))));
    return entries;
  }

  /// <summary>
  /// Decodes the DTS core to per-channel float PCM (via <see cref="DtsCodec"/>) and adds one mono
  /// <c>&lt;CHANNEL&gt;.wav</c> per channel, named for its AMODE speaker (document order, LFE last).
  /// Unsupported / malformed / truncated input is silently skipped so the archive still surfaces
  /// <c>FULL.dts</c> + metadata.
  /// </summary>
  private static void AddDecodedChannels(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    try {
      DtsStreamInfo info;
      using (var infoStream = new MemoryStream(blob, writable: false))
        info = DtsCodec.ReadStreamInfo(infoStream);
      if (info.Channels < 1 || info.SampleRate <= 0)
        return;

      byte[] pcm;
      using (var src = new MemoryStream(blob, writable: false))
      using (var dst = new MemoryStream()) {
        DtsCodec.Decompress(src, dst);
        pcm = dst.ToArray();
      }
      if (pcm.Length == 0)
        return;

      var names = AmodeChannelNames(info.Amode, info.Lfe);
      if (names.Count != info.Channels)
        return;

      var bytesPerFrame = info.Channels * 2;
      if (pcm.Length % bytesPerFrame != 0)
        return;
      var frameCount = pcm.Length / bytesPerFrame;

      if (info.Channels == 1) {
        entries.Add(new($"{names[0]}.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, info.SampleRate, 16), "pcm"));
        return;
      }

      for (var c = 0; c < info.Channels; ++c) {
        var mono = new byte[frameCount * 2];
        for (var f = 0; f < frameCount; ++f) {
          var src = (f * info.Channels + c) * 2;
          mono[f * 2] = pcm[src];
          mono[f * 2 + 1] = pcm[src + 1];
        }
        entries.Add(new($"{names[c]}.wav", "Channel", PcmCodec.ToWavBlob(mono, 1, info.SampleRate, 16), "pcm"));
      }
    } catch {
      // Undecodable (unsupported framing, malformed, truncated) — FULL.dts + metadata only.
    }
  }

  /// <summary>
  /// DTS AMODE → ordered speaker names (the document order the channels are coded in), mapped to
  /// <c>Codec.Pcm.ChannelLayout</c> names. LFE, when present, is appended last. Only the AMODE
  /// arrangements the core decoder emits at their native channel count are mapped here.
  /// </summary>
  private static IReadOnlyList<string> AmodeChannelNames(int amode, bool lfe) {
    var names = amode switch {
      0 => new List<string> { "CENTER" },                                                // A (mono)
      1 => ["CH_0", "CH_1"],                                                              // A+B dual mono
      2 => ["LEFT", "RIGHT"],                                                             // L+R stereo
      3 => ["LEFT", "RIGHT"],                                                             // sum/difference stereo
      4 => ["LEFT", "RIGHT"],                                                             // LT+RT total stereo
      5 => ["CENTER", "LEFT", "RIGHT"],                                                   // C+L+R
      6 => ["LEFT", "RIGHT", "BACK_CENTER"],                                              // L+R+S
      7 => ["CENTER", "LEFT", "RIGHT", "BACK_CENTER"],                                    // C+L+R+S
      8 => ["LEFT", "RIGHT", "SIDE_LEFT", "SIDE_RIGHT"],                                  // L+R+SL+SR
      9 => ["CENTER", "LEFT", "RIGHT", "SIDE_LEFT", "SIDE_RIGHT"],                        // C+L+R+SL+SR
      _ => new List<string>(),
    };
    if (names.Count > 0 && lfe)
      names.Add("LFE");
    return names;
  }

  private static string BuildMetadata(byte[] blob) {
    var info = new StringBuilder();
    info.AppendLine("codec=DTS (Coherent Acoustics)");

    var hasDtsHd = IndexOf(blob, DtsHdChunkMagic, 0) >= 0 || IndexOf(blob, ExtensionSync, 0) >= 0;

    // Find the first core sync word; DTS-HD files often lead with a container header chunk.
    var firstCore = IndexOf(blob, DtsFrameHeader.SyncWord, 0);
    if (firstCore < 0 || DtsFrameHeader.TryParse(blob, firstCore) is not { } first) {
      info.AppendLine("frames=0");
      info.AppendLine($"dts_hd_present={(hasDtsHd ? "yes" : "no")}");
      info.AppendLine("note=no parseable DTS core frame found.");
      return info.ToString();
    }

    var channels = DtsFrameHeader.AmodeChannelCount(first.Amode) + (first.Lfe > 0 ? 1 : 0);
    info.AppendLine($"channel_layout={DtsFrameHeader.AmodeName(first.Amode)}{(first.Lfe > 0 ? " + LFE" : "")}");
    info.AppendLine($"channels={channels}");
    info.AppendLine($"amode={first.Amode}");
    info.AppendLine($"lfe={(first.Lfe > 0 ? "yes" : "no")}");
    info.AppendLine($"sample_rate={first.SampleRate}");
    info.AppendLine($"bitrate={(first.BitRate > 3 ? first.BitRate.ToString() : "variable/open")}");
    info.AppendLine($"sample_blocks={first.SampleBlocks}");

    // Walk frames by FSIZE, counting and accumulating samples (32 PCM samples per sample block).
    var frames = 0;
    long totalSamples = 0;
    var pos = firstCore;
    while (pos + 14 <= blob.Length) {
      if (DtsFrameHeader.TryParse(blob, pos) is not { } header)
        break;
      ++frames;
      totalSamples += (long)header.SampleBlocks * 32;
      pos += header.FrameSize;
    }

    var duration = first.SampleRate > 0 ? (double)totalSamples / first.SampleRate : 0;
    info.AppendLine($"frames={frames}");
    info.AppendLine($"duration_seconds={duration:0.###}");
    info.AppendLine($"dts_hd_present={(hasDtsHd ? "yes" : "no")}");
    return info.ToString();
  }

  private static int IndexOf(byte[] haystack, byte[] needle, int start) {
    for (var i = start; i + needle.Length <= haystack.Length; ++i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j)
        if (haystack[i + j] != needle[j]) {
          match = false;
          break;
        }
      if (match)
        return i;
    }
    return -1;
  }
}
