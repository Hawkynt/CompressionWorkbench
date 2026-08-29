#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Mp4;

/// <summary>Minimal standards-based audio-only ISO BMFF writer for AAC access units.</summary>
internal static class Mp4AudioMuxer {

  internal static byte[] MuxAac(AudioEncodedStream stream) {
    if (!stream.Format.CodecId.Equals("aac", StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException($"MP4 AAC muxer cannot carry codec '{stream.Format.CodecId}'.");
    if (stream.Format.SampleRate <= 0 || stream.Format.Channels is < 1 or > 2)
      throw new ArgumentOutOfRangeException(nameof(stream), "AAC MP4 muxing requires a positive sample rate and mono/stereo channels.");
    if (stream.Packets.Count == 0)
      throw new ArgumentException("AAC MP4 muxing requires at least one access unit.", nameof(stream));
    if (stream.CodecPrivateData is not { Length: >= 2 } asc)
      throw new ArgumentException("AAC MP4 muxing requires AudioSpecificConfig codec-private data.", nameof(stream));

    var sampleDurations = stream.Packets
      .Select(static packet => checked((uint)(packet.DurationSamples > 0 ? packet.DurationSamples : 1024)))
      .ToArray();
    var mediaDuration = sampleDurations.Aggregate(0UL, static (sum, value) => sum + value);
    var mediaBytes = checked((int)stream.Packets.Sum(static packet => (long)packet.Data.Length));
    var averageBitrate = mediaDuration == 0
      ? 0u
      : checked((uint)Math.Min(uint.MaxValue,
        (ulong)mediaBytes * 8UL * (ulong)stream.Format.SampleRate / mediaDuration));

    var ftyp = BuildFtyp();
    var mdatPayload = new byte[mediaBytes];
    var mediaOffset = 0;
    foreach (var packet in stream.Packets) {
      packet.Data.CopyTo(mdatPayload, mediaOffset);
      mediaOffset += packet.Data.Length;
    }
    var mdat = Box("mdat", mdatPayload);
    var chunkOffset = checked((uint)(ftyp.Length + 8));
    var moov = BuildMoov(stream, asc, sampleDurations, mediaDuration, averageBitrate, chunkOffset);

    var result = new byte[checked(ftyp.Length + mdat.Length + moov.Length)];
    ftyp.CopyTo(result, 0);
    mdat.CopyTo(result, ftyp.Length);
    moov.CopyTo(result, ftyp.Length + mdat.Length);
    return result;
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
    byte[] asc,
    uint[] durations,
    ulong mediaDuration,
    uint averageBitrate,
    uint chunkOffset
  ) {
    var movieTimescale = 1_000u;
    var movieDuration = checked((uint)Math.Min(uint.MaxValue,
      mediaDuration * movieTimescale / (ulong)stream.Format.SampleRate));
    var mvhd = BuildMvhd(movieTimescale, movieDuration);
    var trak = BuildTrak(stream, asc, durations, mediaDuration, averageBitrate, chunkOffset, movieTimescale, movieDuration);
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
    byte[] asc,
    uint[] durations,
    ulong mediaDuration,
    uint averageBitrate,
    uint chunkOffset,
    uint movieTimescale,
    uint movieDuration
  ) {
    var tkhd = BuildTkhd(movieDuration);
    var mdia = BuildMdia(stream, asc, durations, mediaDuration, averageBitrate, chunkOffset);
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
    byte[] asc,
    uint[] durations,
    ulong mediaDuration,
    uint averageBitrate,
    uint chunkOffset
  ) {
    var mdhd = BuildMdhd((uint)stream.Format.SampleRate, checked((uint)Math.Min(uint.MaxValue, mediaDuration)));
    var hdlr = BuildHdlr();
    var minf = BuildMinf(stream, asc, durations, averageBitrate, chunkOffset);
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
    byte[] asc,
    uint[] durations,
    uint averageBitrate,
    uint chunkOffset
  ) {
    var smhd = FullBox("smhd", 0, [0, 0, 0, 0]);
    var dinf = BuildDinf();
    var stbl = BuildStbl(stream, asc, durations, averageBitrate, chunkOffset);
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
    byte[] asc,
    uint[] durations,
    uint averageBitrate,
    uint chunkOffset
  ) {
    var stsd = BuildStsd(stream.Format, asc, averageBitrate);
    var stts = BuildStts(durations);
    var stsc = BuildStsc(stream.Packets.Count);
    var stsz = BuildStsz(stream.Packets);
    var stco = BuildStco(chunkOffset);
    return Container("stbl", stsd, stts, stsc, stsz, stco);
  }

  private static byte[] BuildStsd(AudioStreamFormat format, byte[] asc, uint averageBitrate) {
    var esds = BuildEsds(asc, averageBitrate);
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
    WriteUInt32(entry, checked((uint)format.SampleRate << 16));
    entry.Write(esds);
    var mp4a = Box("mp4a", entry.ToArray());

    using var stsdBody = new MemoryStream();
    WriteUInt32(stsdBody, 0);
    WriteUInt32(stsdBody, 1);
    stsdBody.Write(mp4a);
    return Box("stsd", stsdBody.ToArray());
  }

  private static byte[] BuildEsds(byte[] asc, uint averageBitrate) {
    var decoderSpecific = Descriptor(0x05, asc);
    using var decoderConfigBody = new MemoryStream();
    decoderConfigBody.WriteByte(0x40); // MPEG-4 Audio
    decoderConfigBody.WriteByte(0x15); // AudioStream, upstream=0, reserved=1
    decoderConfigBody.Write([0, 0, 0]); // bufferSizeDB
    WriteUInt32(decoderConfigBody, averageBitrate);
    WriteUInt32(decoderConfigBody, averageBitrate);
    decoderConfigBody.Write(decoderSpecific);
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
        runs[^1] = (last.Count + 1, last.Duration);
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
}
