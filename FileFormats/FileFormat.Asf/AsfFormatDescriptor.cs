#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Codec.Wma;
using Compression.Registry;

namespace FileFormat.Asf;

/// <summary>
/// Surfaces a Microsoft Advanced Systems Format container (<c>.asf</c>/<c>.wma</c>/
/// <c>.wmv</c>) as an archive of the byte-exact original (<c>FULL.asf</c>, Kind
/// <c>Container</c>) plus rich metadata and a description of each carried stream.
/// The Data Object packets are depayloaded into per-stream elementary bitstreams
/// (<c>streams/stream_NN.bin</c>, Kind <c>Stream</c>) and each stream is described in
/// <c>streams/stream_NN.info.txt</c> (Kind <c>Tag</c>) carrying its codec / bitrate.
/// WMA v1/v2 audio streams (WAVEFORMATEX tags <c>0x160</c>/<c>0x161</c>) are decoded
/// via <c>Codec.Wma</c> into one mono <c>&lt;CHANNEL&gt;.wav</c> per channel (Kind
/// <c>Channel</c>); streams the decoder can't handle (WMA Pro / Lossless, corrupt
/// data) fall back to just the <c>stream_NN.bin</c> blob. File properties and the
/// content description land in <c>metadata.ini</c>; the Extended Content Description
/// tags land in <c>metadata/tags.ini</c>. Read-only; parsing stops gracefully on a
/// malformed object, keeping whatever was read.
/// </summary>
public sealed class AsfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  public string Id => "Asf";
  public string DisplayName => "ASF (Advanced Systems Format)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".asf";
  public IReadOnlyList<string> Extensions => [".asf", ".wma", ".wmv"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASF Header Object GUID (little-endian byte order on disk).
    new([0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
         0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ASF/WMA/WMV container; full file + metadata + per-stream descriptions + packet payload.";

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
      new("FULL.asf", "Container", blob),
    };

    var parsed = AsfReader.Parse(blob);

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(parsed.RenderMetadataIni())));

    if (parsed.ExtendedTags.Count > 0)
      entries.Add(new("metadata/tags.ini", "Tag", Encoding.UTF8.GetBytes(parsed.RenderTagsIni())));

    foreach (var s in parsed.Streams) {
      entries.Add(new($"streams/stream_{s.StreamNumber:D2}.info.txt", "Tag",
        Encoding.UTF8.GetBytes(s.Render())));

      if (!parsed.StreamPayloads.TryGetValue(s.StreamNumber, out var payload) || payload.Length == 0)
        continue;

      // WMA v1/v2 audio → try to decode to per-channel WAVs; fall back to the raw blob.
      if (s.Kind == "audio" && s.FormatTag is 0x0160 or 0x0161 && TryDecodeWmaChannels(s, payload, entries))
        continue;

      entries.Add(new($"streams/stream_{s.StreamNumber:D2}.bin", "Stream", payload, Method: "asf_stream"));
    }

    return entries;
  }

  /// <summary>
  /// Decodes a WMA v1/v2 audio stream's reassembled superframes (via <see cref="WmaCodec"/>)
  /// and adds one mono <c>&lt;CHANNEL&gt;.wav</c> per channel under
  /// <c>streams/stream_NN/</c>. Each ASF media object is one coded superframe. Returns
  /// false (so the caller surfaces the raw blob instead) when the stream lacks the
  /// parameters needed to construct the decoder or decoding fails.
  /// </summary>
  private static bool TryDecodeWmaChannels(AsfReader.StreamInfo s, byte[] payload, List<AudioPseudoArchive.Entry> entries) {
    try {
      if (s.Channels is not (> 0) || s.SampleRate is not (> 0) || s.ByteRate is not (> 0) || s.BlockAlign is not (> 0))
        return false;

      var version = s.FormatTag == 0x0160 ? 1 : 2;
      var codec = new WmaCodec(version, s.Channels.Value, s.SampleRate.Value,
        s.ByteRate.Value * 8, s.BlockAlign.Value, s.ExtraData ?? []);

      var blockAlign = s.BlockAlign.Value;
      using var pcm = new MemoryStream();
      var decodedAny = false;
      // Each reassembled media object is one coded superframe of block_align bytes.
      for (var off = 0; off + 1 <= payload.Length; off += blockAlign) {
        var len = Math.Min(blockAlign, payload.Length - off);
        var samples = codec.DecodeSuperframe(payload.AsSpan(off, len));
        if (samples.Length == 0) continue;
        decodedAny = true;
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        pcm.Write(bytes);
      }
      if (!decodedAny || pcm.Length == 0)
        return false;

      var prefix = $"streams/stream_{s.StreamNumber:D2}";
      var raw = pcm.ToArray();
      if (s.Channels.Value == 1) {
        entries.Add(new($"{prefix}/MONO.wav", "Channel", PcmCodec.ToWavBlob(raw, 1, s.SampleRate.Value, 16), Method: "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(raw, s.Channels.Value, s.SampleRate.Value, 16))
          entries.Add(new($"{prefix}/{name}.wav", "Channel", wav, Method: "pcm"));
      }
      return true;
    } catch {
      return false; // graceful fallback to the raw stream blob
    }
  }
}
