#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Codec.Wma;
using Codec.WmaLossless;
using Codec.WmaPro;
using Compression.Registry;

namespace FileFormat.Asf;

/// <summary>
/// Surfaces Microsoft Advanced Systems Format (<c>.asf</c>/<c>.wma</c>/<c>.wmv</c>)
/// as a pseudo-archive and supports codec-preserving mux/remux. Demuxed raw streams carry
/// their exact Stream Properties body plus a media-object manifest, so rebuilding preserves
/// codec-private data, object boundaries, presentation timestamps and key-frame flags without
/// decoding/re-encoding the WMV/WMA elementary bytes.
/// </summary>
public sealed class AsfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract,
  IArchiveCreatable, IArchiveModifiable {

  /// <summary>Gets the id.</summary>
  public string Id => "Asf";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "ASF (Advanced Systems Format)";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Audio;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".asf";

  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".asf", ".wma", ".wmv"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
         0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C], Confidence: 0.95),
  ];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("asf-remux", "ASF stream mux/remux")];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description => "ASF/WMA/WMV demux plus codec-preserving fixed-packet mux/remux.";

  /// <summary>The normalized remux profile deliberately does not expose a zero-stream ASF instance.</summary>
  public bool CanPurgeToEmpty => false;

  /// <summary>Lists the entries in the supplied container.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  /// <summary>Extracts the supplied container.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  /// <summary>Extracts one pseudo-archive entry.</summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  /// <summary>
  /// Builds ASF from canonical demux artifacts. A lone <c>FULL.asf</c> is accepted as a
  /// byte-exact passthrough; otherwise every stream needs <c>.properties.bin</c> and
  /// <c>.bin</c>, with optional <c>.objects.csv</c> timing/boundary metadata.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => AsfRemuxer.Create(output, inputs, options);

  /// <summary>Adds/replaces canonical ASF stream artifacts and transactionally remuxes the file.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => AsfRemuxer.Add(archive, inputs);

  /// <summary>Removes the referenced logical stream and transactionally remuxes the survivors.</summary>
  public void Remove(Stream archive, string[] entryNames)
    => AsfRemuxer.Remove(archive, entryNames);

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
    if (parsed.PreservedHeaderObjects.Count > 0)
      entries.Add(new(AsfRemuxer.PreservedHeaderPath, "Tag",
        AsfContainerWriter.JoinPreservedHeaderObjects(parsed.PreservedHeaderObjects), Method: "asf_header"));

    foreach (var s in parsed.Streams) {
      entries.Add(new($"streams/stream_{s.StreamNumber:D2}.info.txt", "Tag",
        Encoding.UTF8.GetBytes(s.Render())));

      if (!parsed.StreamPayloads.TryGetValue(s.StreamNumber, out var payload) || payload.Length == 0)
        continue;

      // Decoded channel WAVs are a convenience view, not a replacement for the encoded
      // stream. Always retain the canonical raw artifacts as well: a mixed WMV+WMA file
      // must survive extract -> create without silently losing the audio track merely
      // because the WMA decoder happened to understand it.
      if (s.Kind == "audio" && s.FormatTag is 0x0160 or 0x0161)
        TryDecodeWmaChannels(s, payload, entries);
      else if (s.Kind == "audio" && s.FormatTag == 0x0162)
        TryDecodeWmaProChannels(s, payload, entries);
      else if (s.Kind == "audio" && s.FormatTag == 0x0163)
        TryDecodeWmaLosslessChannels(s, payload, entries);

      entries.Add(new(AsfRemuxer.PayloadPath(s.StreamNumber), "Stream", payload, Method: "asf_stream"));
      if (s.StreamPropertiesBody.Length > 0)
        entries.Add(new(AsfRemuxer.PropertiesPath(s.StreamNumber), "Tag", s.StreamPropertiesBody, Method: "asf_stream_properties"));
      var objects = parsed.StreamObjects.TryGetValue(s.StreamNumber, out var manifest)
        ? manifest
        : [new AsfMediaObjectInfo(payload.Length, 0, false)];
      entries.Add(new(AsfRemuxer.ObjectsPath(s.StreamNumber), "Tag", AsfRemuxer.RenderObjects(objects), Method: "asf_media_objects"));
    }

    return entries;
  }

  /// <summary>
  /// Decodes a WMA v1/v2 audio stream's reassembled superframes and adds one mono WAV per
  /// channel. Returns false when decoding is unavailable so the raw stream is surfaced.
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
      return false;
    }
  }

  /// <summary>Attempts WMA Professional channel decode, otherwise leaves the raw stream available.</summary>
  private static bool TryDecodeWmaProChannels(AsfReader.StreamInfo s, byte[] payload, List<AudioPseudoArchive.Entry> entries) {
    try {
      if (s.Channels is not (> 0) || s.SampleRate is not (> 0) || s.BlockAlign is not (> 0) ||
          s.BitsPerSample is not (> 0) || s.ExtraData is not { Length: >= 18 })
        return false;

      var codec = new WmaProCodec(s.Channels.Value, s.SampleRate.Value, s.BitsPerSample.Value,
        s.BlockAlign.Value, (s.ByteRate ?? 0), s.ExtraData);
      var channels = codec.Channels;

      var blockAlign = s.BlockAlign.Value;
      using var pcm = new MemoryStream();
      var decodedAny = false;
      for (var off = 0; off + blockAlign <= payload.Length; off += blockAlign) {
        var samples = codec.DecodePacket(payload.AsSpan(off, blockAlign));
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
      if (channels == 1) {
        entries.Add(new($"{prefix}/MONO.wav", "Channel", PcmCodec.ToWavBlob(raw, 1, s.SampleRate.Value, 16), Method: "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(raw, channels, s.SampleRate.Value, 16))
          entries.Add(new($"{prefix}/{name}.wav", "Channel", wav, Method: "pcm"));
      }
      return true;
    } catch {
      return false;
    }
  }

  /// <summary>Attempts WMA Lossless channel decode, otherwise leaves the raw stream available.</summary>
  private static bool TryDecodeWmaLosslessChannels(AsfReader.StreamInfo s, byte[] payload, List<AudioPseudoArchive.Entry> entries) {
    try {
      if (s.Channels is not (> 0) || s.SampleRate is not (> 0) || s.BlockAlign is not (> 0) ||
          s.ExtraData is not { Length: >= 18 })
        return false;

      var codec = new WmaLosslessCodec(s.Channels.Value, s.SampleRate.Value, s.BlockAlign.Value, s.ExtraData);
      var channels = codec.Channels;

      var blockAlign = s.BlockAlign.Value;
      using var pcm = new MemoryStream();
      var decodedAny = false;
      for (var off = 0; off + blockAlign <= payload.Length; off += blockAlign) {
        var samples = codec.DecodePacket(payload.AsSpan(off, blockAlign));
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
      if (channels == 1) {
        entries.Add(new($"{prefix}/MONO.wav", "Channel", PcmCodec.ToWavBlob(raw, 1, s.SampleRate.Value, 16), Method: "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(raw, channels, s.SampleRate.Value, 16))
          entries.Add(new($"{prefix}/{name}.wav", "Channel", wav, Method: "pcm"));
      }
      return true;
    } catch {
      return false;
    }
  }
}