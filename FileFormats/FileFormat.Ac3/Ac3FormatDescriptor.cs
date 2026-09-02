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
/// For both AC-3 and E-AC-3 the descriptor additionally decodes the stream (via <c>Codec.Ac3</c>)
/// and surfaces one mono <c>&lt;CHANNEL&gt;.wav</c> per decoded channel, named via the acmod speaker
/// layout (L/C/R → FRONT_LEFT/CENTER/FRONT_RIGHT, surrounds → BACK_*/SIDE_*, plus LFE last). For
/// E-AC-3 only the primary independent substream (id 0) is decoded; dependent substreams are skipped
/// (noted in <c>metadata.ini</c>). When the decoder can't handle the input (enhanced coupling,
/// malformed, truncated) it falls back to the info-only layout (<c>FULL.ac3</c> + <c>metadata.ini</c>).
/// </para>
/// </summary>
public sealed class Ac3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Ac3";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "AC-3 / E-AC-3";
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
public string DefaultExtension => ".ac3";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".ac3", ".eac3"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  // 16-bit 0x0B77 sync word; low confidence keeps false positives down for arbitrary streams.
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Ac3SyncFrame.SyncWord, Confidence: 0.60),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("ac3", "AC-3")];
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
public string Description => "AC-3 / E-AC-3 (Dolby Digital) elementary stream; per-channel WAVs for AC-3, stream info for E-AC-3.";

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
      if (info.Channels < 1 || info.SampleRate <= 0)
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

    // Walk frames by size, counting and accumulating samples. Each AC-3 frame is 1536 PCM samples
    // (6 blocks × 256); an E-AC-3 frame carries a variable block count, and only its primary
    // independent substream (id 0) contributes audio — dependent substreams are skipped.
    var frames = 0;
    var dependentFrames = 0;
    long totalSamples = 0;
    var pos = firstOffset;
    while (pos + 6 <= blob.Length) {
      if (Ac3FrameHeader.TryParse(blob, pos) is not { } header || header.FrameSize <= 0)
        break;
      ++frames;
      if (header.IsDependentSubstream || (header.IsEnhanced && header.SubstreamId != 0))
        ++dependentFrames;
      else
        totalSamples += header.IsEnhanced ? header.NumBlocks * 256L : 1536L;
      pos += header.FrameSize;
    }

    var duration = first.SampleRate > 0 ? (double)totalSamples / first.SampleRate : 0;
    info.AppendLine($"frames={frames}");
    info.AppendLine($"duration_seconds={duration:0.###}");
    if (dependentFrames > 0)
      info.AppendLine($"note=skipped {dependentFrames} dependent/non-primary E-AC-3 substream frame(s) (only independent substream 0 is decoded).");
    return info.ToString();
  }

  private static int IndexOfSync(byte[] blob, int start) {
    for (var i = start; i + 1 < blob.Length; ++i)
      if (blob[i] == 0x0B && blob[i + 1] == 0x77)
        return i;
    return -1;
  }
}
