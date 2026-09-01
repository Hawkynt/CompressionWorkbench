#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.Mp4;

/// <summary>Minimal standards-based audio-only ISO BMFF writer for AAC and MPEG-1/2 Layer II/III packets.</summary>
internal static class Mp4AudioMuxer {
  private static readonly string[] MuxCodecs = ["aac", "mp3", "mp2"];
  private static readonly int[] Mpeg1SampleRates = [32_000, 44_100, 48_000];
  private static readonly int[] Mpeg2SampleRates = [16_000, 22_050, 24_000];

  internal static IReadOnlyList<string> SupportedCodecs => MuxCodecs;

  internal static bool CanMux(AudioStreamFormat format, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);

    if (!MuxCodecs.Contains(format.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"the audio-only MP4 writer cannot carry codec '{format.CodecId}'";
      return false;
    }
    if (format.SampleRate <= 0 || format.SampleRate > ushort.MaxValue || format.Channels is < 1 or > 2) {
      reason = "MP4 audio muxing requires mono/stereo, a positive sample rate, and a version-0 mp4a-compatible sample rate no greater than 65535 Hz";
      return false;
    }
    if (format.CodecId.Equals("aac", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }

    if (!TryGetMpegVersion(format, out var version)) {
      reason = "MPEG audio in MP4 requires the demuxed 'mpeg-version' stream property";
      return false;
    }
    if (version == 25) {
      reason = "MPEG-2.5 audio has no registered MP4 objectTypeIndication and cannot be muxed without mislabeling the stream";
      return false;
    }
    if (version is not (1 or 2)) {
      reason = $"MPEG audio version '{GetProperty(format, "mpeg-version")}' is not supported by the MP4 writer";
      return false;
    }
    if (!TryGetMpegLayer(format, out var layer) || layer is not (2 or 3)) {
      reason = "MPEG audio in MP4 requires the demuxed 'mpeg-layer' stream property for Layer II or III";
      return false;
    }
    if (format.CodecId.Equals("mp3", StringComparison.OrdinalIgnoreCase) != (layer == 3)) {
      reason = $"codec '{format.CodecId}' does not match MPEG Layer {layer}";
      return false;
    }

    var validRates = version == 1 ? Mpeg1SampleRates : Mpeg2SampleRates;
    if (!validRates.Contains(format.SampleRate)) {
      reason = $"{format.SampleRate} Hz is not a standard MPEG-{version} audio sample rate";
      return false;
    }

    reason = null;
    return true;
  }

  internal static byte[] Mux(AudioEncodedStream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!CanMux(stream.Format, out var reason))
      throw new NotSupportedException(reason);
    if (stream.Packets.Count == 0)
      throw new ArgumentException("MP4 audio muxing requires at least one encoded packet.", nameof(stream));

    var codec = ResolveCodecConfiguration(stream);
    var sampleDurations = stream.Packets
      .Select(packet => checked((uint)(packet.DurationSamples > 0 ? packet.DurationSamples : codec.DefaultPacketDuration)))
      .ToArray();
    var mediaDuration = sampleDurations.Aggregate(0UL, static (sum, value) => checked(sum + value));
    var mediaBytes = checked((int)stream.Packets.Sum(static packet => (long)packet.Data.Length));
    var averageBitrate = mediaDuration == 0
      ? 0u
      : checked((uint)Math.Min(uint.MaxValue,
        (ulong)mediaBytes * 8UL * (ulong)stream.Format.SampleRate / mediaDuration));

    var ftyp = BuildFtyp();
    var mdatPayload = new byte[mediaBytes];
    var mediaOffset = 0;
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        throw new InvalidDataException("MP4 media samples cannot contain out-of-band header packets.");
      packet.Data.CopyTo(mdatPayload, mediaOffset);
      mediaOffset += packet.Data.Length;
    }
    var mdat = Box("mdat", mdatPayload);
    var chunkOffset = checked((uint)(ftyp.Length + 8));
    var moov = BuildMoov(stream, codec, sampleDurations, mediaDuration, averageBitrate, chunkOffset);

    var result = new byte[checked(ftyp.Length + mdat.Length + moov.Length)];
    ftyp.CopyTo(result, 0);
    mdat.CopyTo(result, ftyp.Length);
    moov.CopyTo(result, ftyp.Length + mdat.Length);
    return result;
  }

  private static CodecConfiguration ResolveCodecConfiguration(AudioEncodedStream stream) {
    if (stream.Format.CodecId.Equals("aac", StringComparison.OrdinalIgnoreCase)) {
      if (stream.CodecPrivateData is not { Length: >= 2 } asc)
        throw new ArgumentException("AAC MP4 muxing requires AudioSpecificConfig codec-private data.", nameof(stream));
      return new CodecConfiguration(0x40, asc, 1_024);
    }

    if (!TryGetMpegVersion(stream.Format, out var version) || version is not (1 or 2))
      throw new NotSupportedException("MP4 MPEG audio muxing requires MPEG-1 or MPEG-2 stream metadata.");
    if (!TryGetMpegLayer(stream.Format, out var layer) || layer is not (2 or 3))
      throw new NotSupportedException("MP4 MPEG audio muxing requires Layer II or III stream metadata.");

    var objectType = version == 1 ? (byte)0x6B : (byte)0x69;
    var defaultDuration = layer == 2 || version == 1 ? 1_152u : 576u;
    return new CodecConfiguration(objectType, null, defaultDuration);
  }

  private static bool TryGetMpegVersion(AudioStreamFormat format, out int version) {
    var value = GetProperty(format, "mpeg-version");
    if (value is null) {
      version = 0;
      return false;
    }
    if (value.Equals("2.5", StringComparison.OrdinalIgnoreCase)) {
      version = 25;
      return true;
    }
    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
  }

  private static bool TryGetMpegLayer(AudioStreamFormat format, out int layer)
    => int.TryParse(GetProperty(format, "mpeg-layer"), NumberStyles.Integer, CultureInfo.InvariantCulture, out layer);

  private static string? GetProperty(AudioStreamFormat format, string key) {
    if (format.Properties is null) return null;
    foreach (var property in format.Properties)
      if (property.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        return property.Value;
    return null;
  }

  private static byte[] BuildFtyp() {
    using var body = new MemoryStream();
    body.Write("M4A "u8);
    WriteUInt32(body, 0x0000_0200);
    body.Write("M4A "u8);
    body.Write("isom"u8);
    body.Write("mp42"u8);
    return Box("ftyp", body.ToArray());
  }

  private static byte[] BuildMoov(
    AudioEncodedStream stream,
    CodecConfiguration codec,
    uint[] durations,
    ulong mediaDuration,
    uint averageBitrate,
    uint chunkOffset
  ) {
    var movieTimescale = 1_000u;
    var movieDuration = checked((uint)Math.Min(uint.MaxValue,
      checked(mediaDuration * movieTimescale) / (ulong)stream.Format.SampleRate));
    var mvhd = BuildMvhd(movieTimescale, movieDuration);
    var trak = BuildTrak(stream, codec, durations, mediaDuration, averageBitrate, chunkOffset, movieDuration);
    return Container("moov", mvhd, trak);
  }

  private static byte[] BuildMvhd(uint timescale, uint duration) {
    using var body = new MemoryStream();
    WriteUInt32(body, 0); // version + flags
    WriteUInt32(body, 0); // creation
    WriteUInt32(body, 0); // modification
    WriteUInt32(body, timescale);
    WriteUInt32(body, duration);
    WriteUInt32(body, 0x0001_0000); // rate 1.0
    WriteUInt16(body, 0x0100); // volume 1.0
    body.Write(new byte[10]);
    WriteUnityMatrix(body);
    body.Write(new byte[24]);
    WriteUInt32(body, 2); // next track id
    return Box("mvhd", body.ToArray());
  }

  private static byte[] BuildTrak(
    AudioEncodedStream stream,
    CodecConfiguration codec,
    uint[] durations,
    ulong mediaDuration,
    uint averageBitrate,
    uint chunkOffset,
    uint movieDuration
  ) {
    var tkhd = BuildTkhd(movieDuration);
    var mdia = BuildMdia(stream, codec, durations, mediaDuration, averageBitrate, chunkOffset);
    return Container("trak", tkhd, mdia);
  }

  private static byte[] BuildTkhd(uint movieDuration) {
    using var body = new MemoryStream();
    WriteUInt32(body, 0x0000_0007); // enabled + in movie + in preview
    WriteUInt32(body, 0);
    WriteUInt32(body, 0);
    WriteUInt32(body, 1); // track id
    WriteUInt32(body, 0);
    WriteUInt32(body, movieDuration);
    WriteUInt32(body, 0);
    WriteUInt32(body, 0);
    WriteUInt16(body, 0); // layer
    WriteUInt16(body, 0); // alternate group
    WriteUInt16(body, 0x0100); // audio volume
    WriteUInt16(body, 0);
    WriteUnityMatrix(body);
    WriteUInt32(body, 0);
    WriteUInt32(body, 0);
    return Box("tkhd", body.ToArray());
  }

  private static byte[] BuildMdia(
    AudioEncodedStream stream,
    CodecConfiguration codec,
    uint[] durations,
    ulong mediaDuration,
    uint averageBitrate,
    uint chunkOffset
  ) {
    var mdhd = BuildMdhd((uint)stream.Format.SampleRate, checked((uint)Math.Min(uint.MaxValue, mediaDuration)));
    var hdlr = BuildHdlr();
    var minf = BuildMinf(stream, codec, durations, averageBitrate, chunkOffset);
    return Container("mdia", mdhd, hdlr, minf);
  }

  private static byte[] BuildMdhd(uint timescale, uint duration) {
    using var body = new MemoryStream();
    WriteUInt32(body, 0);
    WriteUInt32(body, 0);
    WriteUInt32(body, 0);
    WriteUInt32(body, timescale);
    WriteUInt32(body, duration);
    WriteUInt16(body, 0x55C4); // und
    WriteUInt16(body, 0);
    return Box("mdhd", body.ToArray());
  }

  private static byte[] BuildHdlr() {
    using var body = new MemoryStream();
    WriteUInt32(body, 0);
    WriteUInt32(body, 0);
    body.Write("soun"u8);
    body.Write(new byte[12]);
    body.Write("SoundHandler\0"u8);
    return Box("hdlr", body.ToArray());
  }

  private static byte[] BuildMinf(
    AudioEncodedStream stream,
    CodecConfiguration codec,
    uint[] durations,
    uint averageBitrate,
    uint chunkOffset
  ) {
    var smhd = FullBox("smhd", 0, [0, 0, 0, 0]);
    var dinf = BuildDinf();
    var stbl = BuildStbl(stream, codec, durations, averageBitrate, chunkOffset);
    return Container("minf", smhd, dinf, stbl);
  }

  private static byte[] BuildDinf() {
    var url = FullBox("url ", 1, []); // self-contained media data
    using var drefBody = new MemoryStream();
    WriteUInt32(drefBody, 0);
    WriteUInt32(drefBody, 1);
    drefBody.Write(url);
    return Container("dinf", Box("dref", drefBody.ToArray()));
  }

  private static byte[] BuildStbl(
    AudioEncodedStream stream,
    CodecConfiguration codec,
    uint[] durations,
    uint averageBitrate,
    uint chunkOffset
  ) {
    var stsd = BuildStsd(stream.Format, codec, averageBitrate);
    var stts = BuildStts(durations);
    var stsc = BuildStsc(stream.Packets.Count);
    var stsz = BuildStsz(stream.Packets);
    var stco = BuildStco(chunkOffset);
    return Container("stbl", stsd, stts, stsc, stsz, stco);
  }

  private static byte[] BuildStsd(AudioStreamFormat format, CodecConfiguration codec, uint averageBitrate) {
    var esds = BuildEsds(codec.ObjectType, codec.DecoderSpecificInfo, averageBitrate);
    using var entry = new MemoryStream();
    entry.Write(new byte[6]);
    WriteUInt16(entry, 1); // data reference index
    WriteUInt16(entry, 0); // version
    WriteUInt16(entry, 0); // revision level
    WriteUInt32(entry, 0); // vendor
    WriteUInt16(entry, checked((ushort)format.Channels));
    WriteUInt16(entry, 16);
    WriteUInt16(entry, 0); // compression id
    WriteUInt16(entry, 0); // packet size
    WriteUInt32(entry, checked((uint)format.SampleRate * 0x1_0000u));
    entry.Write(esds);
    var mp4a = Box("mp4a", entry.ToArray());

    using var stsdBody = new MemoryStream();
    WriteUInt32(stsdBody, 0);
    WriteUInt32(stsdBody, 1);
    stsdBody.Write(mp4a);
    return Box("stsd", stsdBody.ToArray());
  }

  private static byte[] BuildEsds(byte objectType, byte[]? decoderSpecificInfo, uint averageBitrate) {
    using var decoderConfigBody = new MemoryStream();
    decoderConfigBody.WriteByte(objectType);
    decoderConfigBody.WriteByte(0x15); // AudioStream, upstream=0, reserved=1
    decoderConfigBody.Write([0, 0, 0]); // bufferSizeDB
    WriteUInt32(decoderConfigBody, averageBitrate);
    WriteUInt32(decoderConfigBody, averageBitrate);
    if (decoderSpecificInfo is { Length: > 0 })
      decoderConfigBody.Write(Descriptor(0x05, decoderSpecificInfo));
    var decoderConfig = Descriptor(0x04, decoderConfigBody.ToArray());
    var slConfig = Descriptor(0x06, [0x02]);

    using var esBody = new MemoryStream();
    WriteUInt16(esBody, 1); // ES_ID
    esBody.WriteByte(0); // flags
    esBody.Write(decoderConfig);
    esBody.Write(slConfig);
    var esDescriptor = Descriptor(0x03, esBody.ToArray());

    using var full = new MemoryStream();
    WriteUInt32(full, 0);
    full.Write(esDescriptor);
    return Box("esds", full.ToArray());
  }

  private static byte[] BuildStts(uint[] durations) {
    var runs = new List<(uint Count, uint Duration)>();
    foreach (var duration in durations) {
      if (runs.Count > 0 && runs[^1].Duration == duration) {
        var last = runs[^1];
        runs[^1] = (checked(last.Count + 1), last.Duration);
      } else {
        runs.Add((1, duration));
      }
    }

    using var body = new MemoryStream();
    WriteUInt32(body, 0);
    WriteUInt32(body, checked((uint)runs.Count));
    foreach (var run in runs) {
      WriteUInt32(body, run.Count);
      WriteUInt32(body, run.Duration);
    }
    return Box("stts", body.ToArray());
  }

  private static byte[] BuildStsc(int sampleCount) {
    using var body = new MemoryStream();
    WriteUInt32(body, 0);
    WriteUInt32(body, 1);
    WriteUInt32(body, 1);
    WriteUInt32(body, checked((uint)sampleCount));
    WriteUInt32(body, 1);
    return Box("stsc", body.ToArray());
  }

  private static byte[] BuildStsz(IReadOnlyList<AudioPacket> packets) {
    using var body = new MemoryStream();
    WriteUInt32(body, 0);
    WriteUInt32(body, 0);
    WriteUInt32(body, checked((uint)packets.Count));
    foreach (var packet in packets)
      WriteUInt32(body, checked((uint)packet.Data.Length));
    return Box("stsz", body.ToArray());
  }

  private static byte[] BuildStco(uint chunkOffset) {
    using var body = new MemoryStream();
    WriteUInt32(body, 0);
    WriteUInt32(body, 1);
    WriteUInt32(body, chunkOffset);
    return Box("stco", body.ToArray());
  }

  private static byte[] Descriptor(byte tag, byte[] body) {
    using var stream = new MemoryStream();
    stream.WriteByte(tag);
    WriteDescriptorLength(stream, body.Length);
    stream.Write(body);
    return stream.ToArray();
  }

  private static void WriteDescriptorLength(Stream output, int length) {
    if (length is < 0 or > 0x0FFF_FFFF)
      throw new ArgumentOutOfRangeException(nameof(length), "ISO/IEC 14496 descriptor lengths are limited to four 7-bit continuation bytes.");

    Span<byte> encoded = stackalloc byte[4];
    var count = 0;
    do {
      encoded[count++] = (byte)(length & 0x7F);
      length >>= 7;
    } while (length != 0);
    for (var i = count - 1; i >= 0; --i)
      output.WriteByte((byte)(encoded[i] | (i == 0 ? 0 : 0x80)));
  }

  private static byte[] Container(string type, params byte[][] children) {
    var length = children.Sum(static child => child.Length);
    var payload = new byte[length];
    var offset = 0;
    foreach (var child in children) {
      child.CopyTo(payload, offset);
      offset += child.Length;
    }
    return Box(type, payload);
  }

  private static byte[] FullBox(string type, uint versionAndFlags, byte[] payload) {
    using var body = new MemoryStream();
    WriteUInt32(body, versionAndFlags);
    body.Write(payload);
    return Box(type, body.ToArray());
  }

  private static byte[] Box(string type, byte[] payload) {
    if (type.Length != 4) throw new ArgumentException("ISO BMFF box types are four characters.", nameof(type));
    var result = new byte[checked(payload.Length + 8)];
    BinaryPrimitives.WriteUInt32BigEndian(result, checked((uint)result.Length));
    Encoding.ASCII.GetBytes(type, result.AsSpan(4, 4));
    payload.CopyTo(result, 8);
    return result;
  }

  private static void WriteUnityMatrix(Stream output) {
    WriteUInt32(output, 0x0001_0000); WriteUInt32(output, 0); WriteUInt32(output, 0);
    WriteUInt32(output, 0); WriteUInt32(output, 0x0001_0000); WriteUInt32(output, 0);
    WriteUInt32(output, 0); WriteUInt32(output, 0); WriteUInt32(output, 0x4000_0000);
  }

  private static void WriteUInt16(Stream output, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    output.Write(bytes);
  }

  private readonly record struct CodecConfiguration(
    byte ObjectType,
    byte[]? DecoderSpecificInfo,
    uint DefaultPacketDuration);
}
