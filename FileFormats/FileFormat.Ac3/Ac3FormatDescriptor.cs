#pragma warning disable CS1591
using System.Text;
using Codec.Ac3;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.Ac3;

/// <summary>
/// AC-3 / E-AC-3 (Dolby Digital / Dolby Digital Plus) elementary stream surfaced as a
/// pseudo-archive. The descriptor parses the sync-frame headers (syncinfo + BSI: sample rate,
/// frame size, channel arrangement via acmod, LFE, dialnorm) for <c>metadata.ini</c> and
/// distinguishes AC-3 (bsid ≤ 10) from E-AC-3 (bsid = 16). The byte-exact <c>FULL.ac3</c>
/// (Kind <c>Container</c>) always round-trips the stream unchanged.
/// <para>
/// For legacy AC-3 the descriptor additionally decodes the stream (via <c>Codec.Ac3</c>) and
/// surfaces one mono <c>&lt;CHANNEL&gt;.wav</c> per decoded channel, named via the acmod speaker
/// layout (L/C/R → FRONT_LEFT/CENTER/FRONT_RIGHT, surrounds → BACK_*/SIDE_*, plus LFE last). When
/// the decoder can't handle the input (E-AC-3, malformed, truncated) it falls back to the
/// info-only layout (<c>FULL.ac3</c> + <c>metadata.ini</c>).
/// </para>
/// </summary>
public sealed class Ac3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "Ac3";
  public string DisplayName => "AC-3 / E-AC-3";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ac3";
  public IReadOnlyList<string> Extensions => [".ac3", ".eac3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // 16-bit 0x0B77 sync word; low confidence keeps false positives down for arbitrary streams.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Ac3SyncFrame.SyncWord, Confidence: 0.60),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ac3", "AC-3")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "AC-3 / E-AC-3 (Dolby Digital) elementary stream; per-channel WAVs for AC-3, stream info for E-AC-3.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.ac3", "Container", blob, "ac3"),
    };
    AddDecodedChannels(blob, entries);
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(BuildMetadata(blob))));
    return entries;
  }

  /// <summary>
  /// Decodes the AC-3 stream to interleaved PCM (via <see cref="Ac3Codec"/>) and adds one mono
  /// <c>&lt;CHANNEL&gt;.wav</c> per channel, named for its acmod speaker. E-AC-3, malformed or
  /// truncated inputs are silently skipped so the archive still surfaces <c>FULL.ac3</c> + metadata.
  /// </summary>
  private static void AddDecodedChannels(byte[] blob, List<AudioPseudoArchive.Entry> entries) {
    try {
      Ac3StreamInfo info;
      using (var infoStream = new MemoryStream(blob, writable: false))
        info = Ac3Codec.ReadStreamInfo(infoStream);
      if (info.IsEnhanced || info.Channels < 1 || info.SampleRate <= 0)
        return;

      byte[] pcm;
      using (var src = new MemoryStream(blob, writable: false))
      using (var dst = new MemoryStream()) {
        Ac3Codec.Decompress(src, dst);
        pcm = dst.ToArray();
      }
      if (pcm.Length == 0)
        return;

      var names = AcmodChannelNames(info.Acmod, info.Lfe);
      // Defensive: the decoder must emit exactly the acmod channel count.
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
      // Undecodable (E-AC-3, unsupported, truncated) — FULL.ac3 + metadata only.
    }
  }

  /// <summary>
  /// AC-3 acmod → ordered speaker names (the order channels are coded in the bitstream, i.e. the
  /// decoder's interleave order), mapped to <c>Codec.Pcm.ChannelLayout</c> names. LFE, when present,
  /// is appended last. acmod 0 (1+1 dual mono) surfaces two independent mono channels.
  /// </summary>
  private static IReadOnlyList<string> AcmodChannelNames(int acmod, bool lfe) {
    var names = acmod switch {
      0 => new List<string> { "CH_0", "CH_1" },                                       // 1+1 dual mono
      1 => ["CENTER"],                                                                 // 1/0
      2 => ["LEFT", "RIGHT"],                                                          // 2/0
      3 => ["FRONT_LEFT", "CENTER", "FRONT_RIGHT"],                                    // 3/0
      4 => ["FRONT_LEFT", "FRONT_RIGHT", "BACK_CENTER"],                               // 2/1
      5 => ["FRONT_LEFT", "CENTER", "FRONT_RIGHT", "BACK_CENTER"],                     // 3/1
      6 => ["FRONT_LEFT", "FRONT_RIGHT", "SIDE_LEFT", "SIDE_RIGHT"],                   // 2/2
      7 => ["FRONT_LEFT", "CENTER", "FRONT_RIGHT", "SIDE_LEFT", "SIDE_RIGHT"],         // 3/2
      _ => new List<string>(),
    };
    if (lfe)
      names.Add("LFE");
    return names;
  }

  private static string BuildMetadata(byte[] blob) {
    var info = new StringBuilder();

    var firstOffset = IndexOfSync(blob, 0);
    if (firstOffset < 0 || Ac3SyncFrame.TryParse(blob, firstOffset) is not { } first) {
      info.AppendLine("codec=AC-3");
      info.AppendLine("frames=0");
      info.AppendLine("note=no parseable AC-3 sync frame found.");
      return info.ToString();
    }

    info.AppendLine($"codec={(first.IsEnhanced ? "E-AC-3 (Dolby Digital Plus)" : "AC-3 (Dolby Digital)")}");
    var channels = Ac3SyncFrame.AcmodChannelCount(first.Acmod) + (first.LowFrequencyEffects ? 1 : 0);
    info.AppendLine($"channel_layout={Ac3SyncFrame.LayoutName(first.Acmod, first.LowFrequencyEffects)}");
    info.AppendLine($"channels={channels}");
    info.AppendLine($"acmod={first.Acmod}");
    info.AppendLine($"lfe={(first.LowFrequencyEffects ? "yes" : "no")}");
    info.AppendLine($"sample_rate={first.SampleRate}");
    info.AppendLine($"bitrate={first.Bitrate}");
    info.AppendLine($"bsid={first.Bsid}");
    info.AppendLine($"dialnorm=-{first.DialNorm} dBFS");

    // Walk frames by size, counting and accumulating samples (1536 PCM samples per AC-3 frame;
    // E-AC-3 frames carry a variable block count handled inside FrameSize-driven walking).
    var frames = 0;
    long totalSamples = 0;
    var pos = firstOffset;
    while (pos + 6 <= blob.Length) {
      if (Ac3SyncFrame.TryParse(blob, pos) is not { } header)
        break;
      ++frames;
      totalSamples += 1536;     // 6 blocks × 256 samples per full AC-3 frame
      pos += header.FrameSize;
    }

    var duration = first.SampleRate > 0 ? (double)totalSamples / first.SampleRate : 0;
    info.AppendLine($"frames={frames}");
    info.AppendLine($"duration_seconds={duration:0.###}");
    return info.ToString();
  }

  private static int IndexOfSync(byte[] blob, int start) {
    for (var i = start; i + 1 < blob.Length; ++i)
      if (blob[i] == 0x0B && blob[i + 1] == 0x77)
        return i;
    return -1;
  }
}
