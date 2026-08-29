namespace Compression.Registry;

/// <summary>Canonical PCM sample representation used by the cross-format audio pipeline.</summary>
public enum AudioPcmEncoding {
  UnsignedInteger,
  SignedInteger,
  IeeeFloat,
}

/// <summary>Describes interleaved PCM independently of any particular container.</summary>
public sealed record AudioPcmFormat(
  int SampleRate,
  int Channels,
  int BitsPerSample,
  AudioPcmEncoding Encoding = AudioPcmEncoding.SignedInteger,
  ulong? ChannelMask = null
) {
  public int BytesPerSample => checked((this.BitsPerSample + 7) / 8);
  public int BytesPerFrame => checked(this.BytesPerSample * this.Channels);
}

/// <summary>Materialized interleaved PCM together with its format.</summary>
public sealed record AudioPcmBuffer(AudioPcmFormat Format, byte[] InterleavedData) {
  public long FrameCount => this.Format.BytesPerFrame == 0 ? 0 : this.InterleavedData.LongLength / this.Format.BytesPerFrame;
}

/// <summary>Codec-level description of an encoded audio stream.</summary>
public sealed record AudioStreamFormat(
  string CodecId,
  int SampleRate,
  int Channels,
  int BitsPerSample = 0,
  IReadOnlyDictionary<string, string>? Properties = null
);

/// <summary>One encoded access unit/packet suitable for packet-preserving remux.</summary>
public sealed record AudioPacket(
  byte[] Data,
  long DurationSamples = 0,
  long? GranulePosition = null,
  bool IsHeader = false
);

/// <summary>Container-neutral encoded audio stream.</summary>
public sealed record AudioEncodedStream(
  AudioStreamFormat Format,
  IReadOnlyList<AudioPacket> Packets,
  byte[]? CodecPrivateData = null
);

/// <summary>
/// Marker for descriptors that are valid participants in audio conversion even when
/// their primary registry category is not <see cref="FormatCategory.Audio"/> (for example MP4/MOV).
/// </summary>
public interface IAudioContainerFormat;

/// <summary>Capability for lossless/decoded conversion through canonical PCM.</summary>
public interface IAudioPcmSource {
  AudioPcmBuffer DecodePcm(Stream input);
}

/// <summary>Capability for encoding canonical PCM into a target format/container.</summary>
public interface IAudioPcmTarget {
  IReadOnlyList<string> SupportedEncodeCodecs { get; }

  bool CanEncode(
    AudioPcmFormat format,
    string codecId,
    FormatCreateOptions options,
    out string? reason
  );

  void EncodePcm(
    Stream output,
    AudioPcmBuffer pcm,
    string codecId,
    FormatCreateOptions options
  );
}

/// <summary>Capability for exposing encoded packets without decoding them.</summary>
public interface IAudioDemuxSource {
  bool TryDemux(Stream input, out AudioEncodedStream? stream);
}

/// <summary>Capability for muxing already-encoded packets without re-encoding.</summary>
public interface IAudioMuxTarget {
  IReadOnlyList<string> SupportedMuxCodecs { get; }

  bool CanMux(
    AudioStreamFormat stream,
    FormatCreateOptions options,
    out string? reason
  );

  void Mux(
    Stream output,
    AudioEncodedStream stream,
    FormatCreateOptions options
  );
}
