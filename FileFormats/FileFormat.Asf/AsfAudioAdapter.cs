#pragma warning disable CS1591
using System.Globalization;
using Compression.Registry;

namespace FileFormat.Asf;

/// <summary>
/// Packet-preserving audio adapter for Advanced Systems Format. Demuxing is intentionally limited
/// to a single unencrypted audio stream because <see cref="AudioEncodedStream"/> represents one
/// stream; refusing multi-stream ASF prevents a remux from silently discarding video or sibling audio.
/// Muxing rebuilds only ASF/WAVEFORMATEX framing and never invokes an audio codec.
/// </summary>
public sealed class AsfAudioAdapter : IAudioDemuxSource, IAudioMuxTarget {
  /// <summary>Shared stateless adapter instance.</summary>
  public static AsfAudioAdapter Instance { get; } = new();

  /// <inheritdoc />
  public IReadOnlyList<string> SupportedMuxCodecs => AsfWriter.KnownCodecs;

  /// <inheritdoc />
  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);

    if (stream.SampleRate <= 0) {
      reason = "ASF audio requires a positive sample rate";
      return false;
    }
    if (stream.Channels is <= 0 or > ushort.MaxValue) {
      reason = "ASF audio channel count must fit WAVEFORMATEX";
      return false;
    }
    if (stream.BitsPerSample is < 0 or > ushort.MaxValue) {
      reason = "ASF audio bits per sample must fit WAVEFORMATEX";
      return false;
    }
    if (!AsfWriter.TryResolveFormatTag(stream, out _)) {
      reason = $"codec '{stream.CodecId}' has no ASF/WAVEFORMATEX format tag";
      return false;
    }
    if (options.TryGetInt("packet-size", out var packetSize) && packetSize is < 100 or > ushort.MaxValue) {
      reason = "ASF packet-size must be 100..65535 bytes";
      return false;
    }

    reason = null;
    return true;
  }

  /// <inheritdoc />
  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);
    AsfWriter.Write(output, stream, options);
  }

  /// <inheritdoc />
  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    stream = null;

    byte[] blob;
    try {
      using var copy = new MemoryStream();
      input.CopyTo(copy);
      blob = copy.ToArray();
    } catch (IOException) {
      return false;
    }

    var parsed = AsfReader.Parse(blob);
    if (parsed.Streams.Count != 1 || parsed.Streams[0] is not { Kind: "audio", Encrypted: false } source ||
        source.SampleRate is not (> 0) || source.Channels is not (> 0) || parsed.DataPayload is not { Length: > 0 })
      return false;

    var packetSize = parsed.MinPacketSize == parsed.MaxPacketSize && parsed.MinPacketSize is > 0 and <= int.MaxValue
      ? (int)parsed.MinPacketSize.Value
      : parsed.MaxPacketSize is > 0 and <= int.MaxValue ? (int)parsed.MaxPacketSize.Value : 0;

    var depayloaded = AsfDepayloader.Depayload(parsed.DataPayload, packetSize);
    if (!depayloaded.TryGetValue(source.StreamNumber, out var sourceData) || sourceData.Objects.Count == 0)
      return false;

    var sampleRate = source.SampleRate.Value;
    var totalBytes = sourceData.Objects.Sum(static data => (long)data.Length);
    var totalSamples = EffectiveDurationSamples(parsed, sampleRate);
    var packets = BuildPackets(sourceData, sampleRate, parsed.Preroll ?? 0, source.ByteRate, totalBytes, totalSamples);
    if (packets.Count == 0)
      return false;

    var codecId = source.CodecName ?? (source.FormatTag is { } tag ? $"format_0x{tag:X4}" : "unknown");
    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (source.FormatTag is { } formatTag)
      properties["format-tag"] = $"0x{formatTag:X4}";
    if (source.ByteRate is { } byteRate)
      properties["byte-rate"] = byteRate.ToString(CultureInfo.InvariantCulture);
    if (source.BlockAlign is { } blockAlign)
      properties["block-align"] = blockAlign.ToString(CultureInfo.InvariantCulture);
    if (parsed.Preroll is { } preroll)
      properties["asf-preroll-ms"] = preroll.ToString(CultureInfo.InvariantCulture);

    stream = new AudioEncodedStream(
      new AudioStreamFormat(
        codecId,
        sampleRate,
        source.Channels.Value,
        source.BitsPerSample ?? 0,
        properties),
      packets,
      source.ExtraData?.ToArray());
    return true;
  }

  private static List<AudioPacket> BuildPackets(
    AsfDepayloader.StreamData sourceData,
    int sampleRate,
    ulong prerollMs,
    long? byteRate,
    long totalBytes,
    long totalSamples
  ) {
    var packets = new List<AudioPacket>(sourceData.Objects.Count);
    long cursor = 0;
    long consumedBytes = 0;

    for (var index = 0; index < sourceData.Objects.Count; ++index) {
      var data = sourceData.Objects[index];
      var start = index < sourceData.PresentationTimesMs.Count
        ? PresentationSamples(sourceData.PresentationTimesMs[index], prerollMs, sampleRate)
        : cursor;
      start = Math.Max(start, cursor);

      consumedBytes = checked(consumedBytes + data.LongLength);
      long end = start;

      if (index + 1 < sourceData.PresentationTimesMs.Count) {
        var next = PresentationSamples(sourceData.PresentationTimesMs[index + 1], prerollMs, sampleRate);
        if (next > start)
          end = next;
      }

      if (end == start && index == sourceData.Objects.Count - 1 && totalSamples > start)
        end = totalSamples;

      if (end == start && totalSamples > 0 && totalBytes > 0) {
        var scaled = (UInt128)(ulong)consumedBytes * (ulong)totalSamples / (ulong)totalBytes;
        var weightedEnd = scaled > (UInt128)long.MaxValue ? long.MaxValue : (long)scaled;
        if (weightedEnd > start)
          end = weightedEnd;
      }

      if (end == start && byteRate is > 0) {
        var duration = (UInt128)(uint)data.Length * (uint)sampleRate / (ulong)byteRate.Value;
        var durationSamples = duration > (UInt128)long.MaxValue ? long.MaxValue : (long)duration;
        end = durationSamples > long.MaxValue - start ? long.MaxValue : start + durationSamples;
      }

      packets.Add(new AudioPacket(data.ToArray(), Math.Max(0, end - start), start));
      cursor = Math.Max(cursor, end);
    }

    return packets;
  }

  private static long PresentationSamples(uint presentationTimeMs, ulong prerollMs, int sampleRate) {
    var normalizedMs = (ulong)presentationTimeMs > prerollMs ? (ulong)presentationTimeMs - prerollMs : 0;
    var scaled = (UInt128)normalizedMs * (uint)sampleRate / 1000u;
    return scaled > (UInt128)long.MaxValue ? long.MaxValue : (long)scaled;
  }

  private static long EffectiveDurationSamples(AsfReader.Parsed parsed, int sampleRate) {
    if (parsed.PlayDuration100ns is not { } playDuration)
      return 0;

    var preroll100ns = parsed.Preroll is { } preroll && preroll <= ulong.MaxValue / 10_000
      ? preroll * 10_000
      : 0;
    var effective = playDuration > preroll100ns ? playDuration - preroll100ns : playDuration;
    var scaled = (UInt128)effective * (uint)sampleRate / 10_000_000u;
    return scaled > (UInt128)long.MaxValue ? long.MaxValue : (long)scaled;
  }
}
