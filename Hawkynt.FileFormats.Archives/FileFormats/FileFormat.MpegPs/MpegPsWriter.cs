#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using Compression.Registry;

namespace FileFormat.MpegPs;

/// <summary>
/// Minimal ISO/IEC 13818-1 MPEG-2 Program Stream writer. It rebuilds only the
/// systems/PES layer; elementary-stream bytes are kept opaque.
/// </summary>
internal static class MpegPsWriter {
  internal const int DefaultProgramMuxRate = 25_200; // 50-byte/s units = 10.08 Mbit/s.
  private const int MaxPesLength = ushort.MaxValue;
  private const int Mpeg2PesHeaderLengthWithoutPts = 3;
  private const int Mpeg2PesHeaderLengthWithPts = 8;
  private const uint CrcPolynomial = 0x04C11DB7;
  private const long TimestampMask = (1L << 33) - 1;

  private static readonly int[] AacSampleRates = [
    96_000, 88_200, 64_000, 48_000, 44_100, 32_000, 24_000,
    22_050, 16_000, 12_000, 11_025, 8_000, 7_350,
  ];

  internal enum StreamKind {
    Audio,
    Video,
  }

  internal sealed record ElementaryStream(byte StreamId, byte StreamType, StreamKind Kind, byte[] Data);

  internal static void WriteProgram(Stream output, IReadOnlyList<ElementaryStream> streams) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(streams);
    if (streams.Count == 0)
      throw new ArgumentException("MPEG-PS muxing requires at least one elementary stream.", nameof(streams));

    ValidateStreams(streams);
    WritePackHeader(output, scrBase: 0, DefaultProgramMuxRate);
    WriteSystemHeader(output, streams, DefaultProgramMuxRate);
    WriteProgramStreamMap(output, streams);

    foreach (var stream in streams)
      WritePesPayload(output, stream.StreamId, stream.Data, pts: null);

    WriteProgramEnd(output);
  }

  internal static void WriteAudio(Stream output, AudioEncodedStream stream) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);

    var codec = stream.Format.CodecId.ToLowerInvariant();
    var streamType = GetAudioStreamType(stream);
    var elementary = new ElementaryStream(0xC0, streamType, StreamKind.Audio, []);

    WritePackHeader(output, scrBase: 0, DefaultProgramMuxRate);
    WriteSystemHeader(output, [elementary], DefaultProgramMuxRate);
    WriteProgramStreamMap(output, [elementary]);

    long totalSamples = 0;
    var written = 0;
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        continue;
      if (packet.Data.Length == 0)
        throw new InvalidDataException("MPEG-PS cannot mux an empty encoded audio packet.");

      var duration = packet.DurationSamples > 0
        ? packet.DurationSamples
        : DefaultDurationSamples(codec, packet.Data);
      if (duration <= 0)
        throw new InvalidDataException("Encoded audio packets need a positive sample duration for MPEG-PS timestamps.");

      var pts = SamplesToPts(totalSamples, stream.Format.SampleRate);
      if (written > 0)
        WritePackHeader(output, pts, DefaultProgramMuxRate);

      var payload = codec is "aac" or "aac-lc"
        ? BuildAdtsPacket(stream.Format, packet.Data, duration)
        : packet.Data;
      WritePesPayload(output, 0xC0, payload, pts);
      totalSamples = checked(totalSamples + duration);
      ++written;
    }

    if (written == 0)
      throw new ArgumentException("MPEG-PS audio muxing requires at least one encoded packet.", nameof(stream));

    WriteProgramEnd(output);
  }

  internal static byte GetAudioStreamType(AudioEncodedStream stream) {
    var codec = stream.Format.CodecId.ToLowerInvariant();
    if (codec is "aac" or "aac-lc")
      return 0x0F;

    if (codec is not ("mp2" or "mp3"))
      throw new NotSupportedException($"MPEG-PS audio remux does not support codec '{stream.Format.CodecId}'.");

    if (TryGetIntProperty(stream.Format, "mpeg-version", out var version))
      return version == 1 ? (byte)0x03 : (byte)0x04;

    var first = stream.Packets.FirstOrDefault(static packet => !packet.IsHeader && packet.Data.Length >= 2);
    if (first is not null && TryReadMpegAudioVersion(first.Data, out version))
      return version == 1 ? (byte)0x03 : (byte)0x04;

    return 0x03;
  }

  internal static byte DetectMpegAudioStreamType(ReadOnlySpan<byte> data) =>
    TryReadMpegAudioVersion(data, out var version) && version != 1 ? (byte)0x04 : (byte)0x03;

  internal static bool IsAacSampleRate(int sampleRate) => Array.IndexOf(AacSampleRates, sampleRate) >= 0;

  private static void ValidateStreams(IReadOnlyList<ElementaryStream> streams) {
    if (streams.Count > 48)
      throw new ArgumentException("MPEG-PS writer supports at most 32 audio plus 16 video stream ids.", nameof(streams));

    var ids = new HashSet<byte>();
    foreach (var stream in streams) {
      if (!ids.Add(stream.StreamId))
        throw new ArgumentException($"Duplicate MPEG-PS stream id 0x{stream.StreamId:X2}.", nameof(streams));
      if (stream.Kind == StreamKind.Audio && stream.StreamId is < 0xC0 or > 0xDF)
        throw new ArgumentException($"Audio stream id 0x{stream.StreamId:X2} is outside 0xC0..0xDF.", nameof(streams));
      if (stream.Kind == StreamKind.Video && stream.StreamId is < 0xE0 or > 0xEF)
        throw new ArgumentException($"Video stream id 0x{stream.StreamId:X2} is outside 0xE0..0xEF.", nameof(streams));
    }
  }

  private static void WritePackHeader(Stream output, long scrBase, int programMuxRate) {
    if (programMuxRate is <= 0 or > 0x3F_FFFF)
      throw new ArgumentOutOfRangeException(nameof(programMuxRate));

    Span<byte> header = stackalloc byte[14];
    header.Clear();
    header[2] = 0x01;
    header[3] = MpegPsReader.PackStartCode;

    var bits = 0;
    var body = header[4..];
    var scr = (ulong)(scrBase & TimestampMask);
    WriteBits(body, ref bits, 0x01, 2);
    WriteBits(body, ref bits, (scr >> 30) & 0x07, 3);
    WriteBits(body, ref bits, 1, 1);
    WriteBits(body, ref bits, (scr >> 15) & 0x7FFF, 15);
    WriteBits(body, ref bits, 1, 1);
    WriteBits(body, ref bits, scr & 0x7FFF, 15);
    WriteBits(body, ref bits, 1, 1);
    WriteBits(body, ref bits, 0, 9); // SCR extension: the writer's timestamps are exact 90 kHz ticks.
    WriteBits(body, ref bits, 1, 1);
    WriteBits(body, ref bits, (uint)programMuxRate, 22);
    WriteBits(body, ref bits, 1, 1);
    WriteBits(body, ref bits, 1, 1);
    WriteBits(body, ref bits, 0x1F, 5);
    WriteBits(body, ref bits, 0, 3); // no pack stuffing
    output.Write(header);
  }

  private static void WriteSystemHeader(Stream output, IReadOnlyList<ElementaryStream> streams, int rateBound) {
    var audioBound = streams.Count(static stream => stream.Kind == StreamKind.Audio);
    var videoBound = streams.Count(static stream => stream.Kind == StreamKind.Video);
    if (audioBound > 0x3F || videoBound > 0x1F)
      throw new InvalidOperationException("MPEG-PS system-header stream bounds overflow their fields.");

    WriteStartCode(output, MpegPsReader.SystemHeaderStartCode);
    WriteUInt16(output, checked((ushort)(6 + streams.Count * 3)));

    Span<byte> fixedFields = stackalloc byte[6];
    fixedFields.Clear();
    var bits = 0;
    WriteBits(fixedFields, ref bits, 1, 1);
    WriteBits(fixedFields, ref bits, (uint)rateBound, 22);
    WriteBits(fixedFields, ref bits, 1, 1);
    WriteBits(fixedFields, ref bits, (uint)audioBound, 6);
    WriteBits(fixedFields, ref bits, 0, 1); // fixed_flag
    WriteBits(fixedFields, ref bits, 0, 1); // CSPS_flag
    WriteBits(fixedFields, ref bits, 0, 1); // system_audio_lock_flag
    WriteBits(fixedFields, ref bits, 0, 1); // system_video_lock_flag
    WriteBits(fixedFields, ref bits, 1, 1);
    WriteBits(fixedFields, ref bits, (uint)videoBound, 5);
    WriteBits(fixedFields, ref bits, 0, 1); // packet_rate_restriction_flag
    WriteBits(fixedFields, ref bits, 0x7F, 7);
    output.Write(fixedFields);

    foreach (var stream in streams) {
      output.WriteByte(stream.StreamId);
      var scale = stream.Kind == StreamKind.Video;
      var sizeBound = stream.Kind == StreamKind.Video ? 512 : 128;
      var encoded = (ushort)(0xC000 | (scale ? 0x2000 : 0) | sizeBound);
      WriteUInt16(output, encoded);
    }
  }

  private static void WriteProgramStreamMap(Stream output, IReadOnlyList<ElementaryStream> streams) {
    var elementaryMapLength = checked(streams.Count * 4);
    var mapLength = checked(10 + elementaryMapLength);
    if (mapLength > 1018)
      throw new InvalidOperationException("MPEG-PS program stream map exceeds its 1018-byte limit.");

    using var map = new MemoryStream(6 + mapLength);
    WriteStartCode(map, MpegPsReader.ProgramStreamMapId);
    WriteUInt16(map, (ushort)mapLength);
    map.WriteByte(0xE0); // current_next=1, single_extension_stream=1, reserved=1, version=0
    map.WriteByte(0xFF); // reserved + marker_bit
    WriteUInt16(map, 0); // program_stream_info_length
    WriteUInt16(map, (ushort)elementaryMapLength);
    foreach (var stream in streams) {
      map.WriteByte(stream.StreamType);
      map.WriteByte(stream.StreamId);
      WriteUInt16(map, 0); // elementary_stream_info_length
    }

    var bytesWithoutCrc = map.GetBuffer().AsSpan(0, checked((int)map.Length));
    WriteUInt32(map, ComputeMpeg2Crc(bytesWithoutCrc));
    output.Write(map.GetBuffer().AsSpan(0, checked((int)map.Length)));
  }

  private static void WritePesPayload(Stream output, byte streamId, ReadOnlySpan<byte> payload, long? pts) {
    var first = true;
    while (payload.Length > 0 || first) {
      var currentPts = first ? pts : null;
      var headerLength = currentPts.HasValue ? Mpeg2PesHeaderLengthWithPts : Mpeg2PesHeaderLengthWithoutPts;
      var chunkLength = Math.Min(payload.Length, MaxPesLength - headerLength);
      WritePesPacket(output, streamId, payload[..chunkLength], currentPts);
      payload = payload[chunkLength..];
      first = false;
      if (payload.Length == 0)
        break;
    }
  }

  private static void WritePesPacket(Stream output, byte streamId, ReadOnlySpan<byte> payload, long? pts) {
    var optionalLength = pts.HasValue ? 5 : 0;
    var packetLength = checked(Mpeg2PesHeaderLengthWithoutPts + optionalLength + payload.Length);
    if (packetLength > MaxPesLength)
      throw new ArgumentException("PES packet payload is too large.", nameof(payload));

    WriteStartCode(output, streamId);
    WriteUInt16(output, (ushort)packetLength);
    output.WriteByte(0x80); // '10' MPEG-2 PES marker, all other first-byte flags clear.
    output.WriteByte(pts.HasValue ? (byte)0x80 : (byte)0x00);
    output.WriteByte((byte)optionalLength);
    if (pts is { } timestamp) {
      Span<byte> encoded = stackalloc byte[5];
      WriteTimestamp(encoded, timestamp);
      output.Write(encoded);
    }
    output.Write(payload);
  }

  private static byte[] BuildAdtsPacket(AudioStreamFormat format, ReadOnlySpan<byte> accessUnit, long durationSamples) {
    var sampleRateIndex = Array.IndexOf(AacSampleRates, format.SampleRate);
    if (sampleRateIndex < 0)
      throw new NotSupportedException($"AAC/ADTS has no sample-rate index for {format.SampleRate} Hz.");
    if (format.Channels is < 1 or > 7)
      throw new NotSupportedException("AAC/ADTS channel configuration must be between 1 and 7.");

    var objectType = TryGetIntProperty(format, "object-type", out var configuredObjectType) ? configuredObjectType : 2;
    var profile = objectType - 1;
    if (profile is < 0 or > 3)
      throw new NotSupportedException($"AAC object type {objectType} has no two-bit ADTS profile encoding.");

    if (durationSamples <= 0 || durationSamples % 1024 != 0)
      throw new InvalidDataException("AAC packet duration must be a positive multiple of 1024 samples for ADTS framing.");
    var rawBlocks = durationSamples / 1024 - 1;
    if (rawBlocks is < 0 or > 3)
      throw new InvalidDataException("One ADTS frame can describe only 1 to 4 AAC raw data blocks.");

    var frameLength = checked(accessUnit.Length + 7);
    if (frameLength > 0x1FFF)
      throw new InvalidDataException($"AAC access unit of {accessUnit.Length} bytes exceeds the ADTS frame-length field.");

    var mpeg2 = TryGetIntProperty(format, "adts-mpeg2", out var adtsMpeg2) && adtsMpeg2 == 1;
    var channels = format.Channels;
    var result = new byte[frameLength];
    result[0] = 0xFF;
    result[1] = (byte)(0xF1 | (mpeg2 ? 0x08 : 0x00));
    result[2] = (byte)((profile << 6) | (sampleRateIndex << 2) | (channels >> 2));
    result[3] = (byte)(((channels & 0x03) << 6) | ((frameLength >> 11) & 0x03));
    result[4] = (byte)(frameLength >> 3);
    result[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F); // VBR buffer fullness = 0x7FF
    result[6] = (byte)(0xFC | (int)rawBlocks);
    accessUnit.CopyTo(result.AsSpan(7));
    return result;
  }

  private static long DefaultDurationSamples(string codec, ReadOnlySpan<byte> packet) => codec switch {
    "aac" or "aac-lc" => 1024,
    "mp2" => 1152,
    "mp3" when TryReadMpegAudioVersion(packet, out var version) => version == 1 ? 1152 : 576,
    _ => 0,
  };

  private static long SamplesToPts(long samples, int sampleRate) {
    if (sampleRate <= 0)
      throw new ArgumentOutOfRangeException(nameof(sampleRate));
    var wholeSeconds = Math.DivRem(samples, sampleRate, out var remainder);
    return checked(wholeSeconds * 90_000 + remainder * 90_000 / sampleRate) & TimestampMask;
  }

  private static bool TryReadMpegAudioVersion(ReadOnlySpan<byte> data, out int version) {
    version = 0;
    if (data.Length < 2 || data[0] != 0xFF || (data[1] & 0xE0) != 0xE0)
      return false;
    var bits = (data[1] >> 3) & 0x03;
    version = bits switch {
      3 => 1,
      2 => 2,
      0 => 25,
      _ => 0,
    };
    return version != 0;
  }

  private static bool TryGetIntProperty(AudioStreamFormat format, string key, out int value) {
    value = 0;
    return format.Properties is { } properties
           && properties.TryGetValue(key, out var text)
           && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  private static void WriteTimestamp(Span<byte> target, long timestamp) {
    var pts = timestamp & TimestampMask;
    target[0] = (byte)(0x20 | (((pts >> 30) & 0x07) << 1) | 1);
    target[1] = (byte)(pts >> 22);
    target[2] = (byte)((((pts >> 15) & 0x7F) << 1) | 1);
    target[3] = (byte)(pts >> 7);
    target[4] = (byte)(((pts & 0x7F) << 1) | 1);
  }

  private static uint ComputeMpeg2Crc(ReadOnlySpan<byte> data) {
    var crc = uint.MaxValue;
    foreach (var value in data) {
      crc ^= (uint)value << 24;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ CrcPolynomial : crc << 1;
    }
    return crc;
  }

  private static void WriteProgramEnd(Stream output) => WriteStartCode(output, MpegPsReader.ProgramEndCode);

  private static void WriteStartCode(Stream output, byte code) {
    Span<byte> startCode = stackalloc byte[4];
    startCode[0] = 0;
    startCode[1] = 0;
    startCode[2] = 1;
    startCode[3] = code;
    output.Write(startCode);
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

  private static void WriteBits(Span<byte> target, ref int bitOffset, ulong value, int bitCount) {
    for (var shift = bitCount - 1; shift >= 0; --shift) {
      if (((value >> shift) & 1) != 0)
        target[bitOffset >> 3] |= (byte)(1 << (7 - (bitOffset & 7)));
      ++bitOffset;
    }
  }
}
