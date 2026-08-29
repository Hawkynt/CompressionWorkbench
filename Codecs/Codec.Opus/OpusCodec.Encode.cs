#pragma warning disable CS1591
#pragma warning disable CS0618

using System.Buffers.Binary;
using System.Text;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using ConcentusOpusMode = Concentus.Enums.OpusMode;
using ConcentusOpusBandwidth = Concentus.Enums.OpusBandwidth;

namespace Codec.Opus;

/// <summary>Encoder controls for RFC 6716 Opus and RFC 7845 Ogg Opus output.</summary>
public sealed record OpusEncoderOptions(
  int SampleRate,
  int Channels,
  OpusApplication Application = OpusApplication.OPUS_APPLICATION_AUDIO,
  int? Bitrate = null,
  int Complexity = 10,
  bool UseVbr = true,
  bool ConstrainedVbr = false,
  bool UseDtx = false,
  bool UseInbandFec = false,
  int PacketLossPercent = 0,
  int? ForceChannels = null,
  OpusBandwidth? MaxBandwidth = null,
  OpusBandwidth? Bandwidth = null,
  OpusSignal Signal = OpusSignal.OPUS_SIGNAL_AUTO,
  ConcentusOpusMode? ForceMode = null,
  bool PredictionDisabled = false,
  int LsbDepth = 16,
  double FrameDurationMilliseconds = 20.0,
  int? SerialNumber = null,
  string Vendor = "CompressionWorkbench",
  IReadOnlyList<string>? Comments = null
);

public static partial class OpusCodec {

  /// <summary>
  /// Encodes interleaved PCM16 to Ogg Opus using pure-managed Concentus. Mono/stereo use
  /// mapping family 0; 3-8 channel surround uses RFC 7845 mapping family 1 / Vorbis order.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, OpusEncoderOptions options) {
    ValidateEncoderOptions(interleaved.Length, options);
    return options.Channels <= 2
      ? EncodeFamily0(interleaved, options)
      : EncodeFamily1(interleaved, options);
  }

  private static byte[] EncodeFamily0(ReadOnlySpan<short> interleaved, OpusEncoderOptions options) {
    var frameSize = FrameSize(options.SampleRate, options.FrameDurationMilliseconds);
    var inputFrames = interleaved.Length / options.Channels;
    using IOpusEncoder encoder = new OpusEncoder(options.SampleRate, options.Channels, options.Application);
    ApplyOptions(encoder, options);

    var preSkip = ScaleLookahead(encoder.Lookahead, options.SampleRate);
    var mapping = options.Channels == 1 ? new byte[] { 0 } : new byte[] { 0, 1 };
    using var output = BeginOggStream(options, preSkip, family: 0, streams: 1,
      coupledStreams: (byte)(options.Channels == 2 ? 1 : 0), mapping, inputFrames > 0, out var serial, out var sequence);
    if (inputFrames == 0) return output.ToArray();

    var packetBuffer = new byte[1275];
    var pcmFrame = new short[frameSize * options.Channels];
    var totalFrames = inputFrames + PadFrames(preSkip, options.SampleRate);
    long encodedFrames = 0;
    for (var inputOffset = 0; inputOffset < totalFrames; inputOffset += frameSize) {
      var available = Math.Clamp(inputFrames - inputOffset, 0, frameSize);
      Array.Clear(pcmFrame);
      if (available > 0)
        interleaved.Slice(inputOffset * options.Channels, available * options.Channels).CopyTo(pcmFrame);
      var bytes = encoder.Encode(pcmFrame, frameSize, packetBuffer, packetBuffer.Length);
      if (bytes <= 0) throw new InvalidDataException($"Opus encoder produced invalid packet length {bytes}.");
      encodedFrames += frameSize;
      var last = inputOffset + frameSize >= totalFrames;
      WriteAudioPage(output, packetBuffer.AsSpan(0, bytes), serial, ref sequence,
        preSkip, inputFrames, encodedFrames, options.SampleRate, last);
    }
    return output.ToArray();
  }

  private static byte[] EncodeFamily1(ReadOnlySpan<short> interleaved, OpusEncoderOptions options) {
    var frameSize = FrameSize(options.SampleRate, options.FrameDurationMilliseconds);
    var inputFrames = interleaved.Length / options.Channels;
    var mapping = new byte[options.Channels];
    using IOpusMultiStreamEncoder encoder = OpusMSEncoder.CreateSurround(
      options.SampleRate, options.Channels, 1, out var streams, out var coupledStreams,
      mapping, options.Application);
    ApplyOptions(encoder, options);

    var preSkip = ScaleLookahead(encoder.Lookahead, options.SampleRate);
    using var output = BeginOggStream(options, preSkip, family: 1, (byte)streams,
      (byte)coupledStreams, mapping, inputFrames > 0, out var serial, out var sequence);
    if (inputFrames == 0) return output.ToArray();

    var packetBuffer = new byte[Math.Max(1275, 1275 * streams + 64)];
    var pcmFrame = new short[frameSize * options.Channels];
    var totalFrames = inputFrames + PadFrames(preSkip, options.SampleRate);
    long encodedFrames = 0;
    for (var inputOffset = 0; inputOffset < totalFrames; inputOffset += frameSize) {
      var available = Math.Clamp(inputFrames - inputOffset, 0, frameSize);
      Array.Clear(pcmFrame);
      if (available > 0)
        interleaved.Slice(inputOffset * options.Channels, available * options.Channels).CopyTo(pcmFrame);
      var bytes = encoder.EncodeMultistream(pcmFrame, frameSize, packetBuffer, packetBuffer.Length);
      if (bytes <= 0) throw new InvalidDataException($"Opus multistream encoder produced invalid packet length {bytes}.");
      encodedFrames += frameSize;
      var last = inputOffset + frameSize >= totalFrames;
      WriteAudioPage(output, packetBuffer.AsSpan(0, bytes), serial, ref sequence,
        preSkip, inputFrames, encodedFrames, options.SampleRate, last);
    }
    return output.ToArray();
  }

  private static ushort ScaleLookahead(int lookahead, int sampleRate)
    => checked((ushort)Math.Min(ushort.MaxValue, (long)lookahead * 48000 / sampleRate));

  /// <summary>
  /// Silence to encode after the input, in encoder-rate frames. A decoder discards the first
  /// <c>preSkip</c> frames it decodes (RFC 7845 §4.2), so without this tail the round trip comes
  /// back exactly that much shorter than it went in. <c>preSkip</c> counts 48 kHz frames whatever
  /// the encoder runs at, so it is scaled back down here.
  /// </summary>
  private static int PadFrames(ushort preSkip, int sampleRate)
    => (int)((long)preSkip * sampleRate / 48000);

  private static MemoryStream BeginOggStream(OpusEncoderOptions options, ushort preSkip,
    byte family, byte streams, byte coupledStreams, byte[] mapping, bool hasAudio,
    out int serial, out uint sequence) {
    serial = options.SerialNumber ?? unchecked((int)0x4357424F);
    sequence = 0;
    var output = new MemoryStream();
    WriteOggPage(output, BuildHead(options, preSkip, family, streams, coupledStreams, mapping),
      serial, sequence++, 0, bos: true, eos: false);
    WriteOggPage(output, BuildTags(options), serial, sequence++, 0, bos: false, eos: !hasAudio);
    return output;
  }

  private static void WriteAudioPage(Stream output, ReadOnlySpan<byte> packet, int serial, ref uint sequence,
    ushort preSkip, int inputFrames, long encodedFrames, int sampleRate, bool last) {
    var granule = last
      ? preSkip + (long)inputFrames * 48000 / sampleRate
      : preSkip + encodedFrames * 48000 / sampleRate;
    WriteOggPage(output, packet, serial, sequence++, granule, bos: false, eos: last);
  }

  private static void ValidateEncoderOptions(int sampleCount, OpusEncoderOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.SampleRate is not (8000 or 12000 or 16000 or 24000 or 48000))
      throw new ArgumentOutOfRangeException(nameof(options), "Opus input rate must be 8, 12, 16, 24, or 48 kHz.");
    if (options.Channels is < 1 or > 8)
      throw new ArgumentOutOfRangeException(nameof(options), "Ogg Opus mapping families 0/1 support 1-8 channels on this surface.");
    if (sampleCount % options.Channels != 0)
      throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.");
    if (options.Bitrate.HasValue && options.Bitrate.Value is < 6144 or > 522240)
      throw new ArgumentOutOfRangeException(nameof(options), "Opus bitrate must be 6144-522240 bit/s.");
    if (options.Complexity is < 0 or > 10)
      throw new ArgumentOutOfRangeException(nameof(options), "Opus complexity must be 0-10.");
    if (options.PacketLossPercent is < 0 or > 100)
      throw new ArgumentOutOfRangeException(nameof(options), "Packet loss percentage must be 0-100.");
    if (options.ForceChannels.HasValue && options.ForceChannels.Value is < 1 or > 2)
      throw new ArgumentOutOfRangeException(nameof(options), "Forced channel count must be 1 or 2.");
    if (options.Channels > 2 && options.ForceChannels.HasValue)
      throw new ArgumentException("ForceChannels is a mono/stereo encoder control and is not valid for multistream surround.", nameof(options));
    if (options.LsbDepth is < 8 or > 24)
      throw new ArgumentOutOfRangeException(nameof(options), "Opus LSB depth must be 8-24.");
    _ = FrameSize(options.SampleRate, options.FrameDurationMilliseconds);
  }

  private static int FrameSize(int sampleRate, double milliseconds) {
    if (milliseconds is not (2.5 or 5.0 or 10.0 or 20.0 or 40.0 or 60.0))
      throw new ArgumentOutOfRangeException(nameof(milliseconds), "Opus frame duration must be 2.5, 5, 10, 20, 40, or 60 ms.");
    return checked((int)Math.Round(sampleRate * milliseconds / 1000.0));
  }

  private static void ApplyOptions(IOpusEncoder encoder, OpusEncoderOptions options) {
    if (options.Bitrate.HasValue) encoder.Bitrate = options.Bitrate.Value;
    encoder.Complexity = options.Complexity;
    encoder.UseVBR = options.UseVbr;
    encoder.UseConstrainedVBR = options.ConstrainedVbr;
    encoder.UseDTX = options.UseDtx;
    encoder.UseInbandFEC = options.UseInbandFec;
    encoder.PacketLossPercent = options.PacketLossPercent;
    if (options.ForceChannels.HasValue) encoder.ForceChannels = options.ForceChannels.Value;
    if (options.MaxBandwidth.HasValue) encoder.MaxBandwidth = ToConcentus(options.MaxBandwidth.Value);
    if (options.Bandwidth.HasValue) encoder.Bandwidth = ToConcentus(options.Bandwidth.Value);
    encoder.SignalType = options.Signal;
    if (options.ForceMode.HasValue) encoder.ForceMode = options.ForceMode.Value;
    encoder.PredictionDisabled = options.PredictionDisabled;
    encoder.LSBDepth = options.LsbDepth;
  }

  private static void ApplyOptions(IOpusMultiStreamEncoder encoder, OpusEncoderOptions options) {
    if (options.Bitrate.HasValue) encoder.Bitrate = options.Bitrate.Value;
    encoder.Complexity = options.Complexity;
    encoder.UseVBR = options.UseVbr;
    encoder.UseConstrainedVBR = options.ConstrainedVbr;
    encoder.UseDTX = options.UseDtx;
    encoder.UseInbandFEC = options.UseInbandFec;
    encoder.PacketLossPercent = options.PacketLossPercent;
    if (options.MaxBandwidth.HasValue) encoder.MaxBandwidth = ToConcentus(options.MaxBandwidth.Value);
    if (options.Bandwidth.HasValue) encoder.Bandwidth = ToConcentus(options.Bandwidth.Value);
    encoder.SignalType = options.Signal;
    if (options.ForceMode.HasValue) encoder.ForceMode = options.ForceMode.Value;
    encoder.PredictionDisabled = options.PredictionDisabled;
    encoder.LSBDepth = options.LsbDepth;
  }

  private static byte[] BuildHead(OpusEncoderOptions options, ushort preSkip, byte family,
    byte streams, byte coupledStreams, byte[] mapping) {
    var result = new byte[family == 0 ? 19 : 21 + options.Channels];
    "OpusHead"u8.CopyTo(result);
    result[8] = 1;
    result[9] = (byte)options.Channels;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10, 2), preSkip);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), (uint)options.SampleRate);
    BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(16, 2), 0);
    result[18] = family;
    if (family != 0) {
      result[19] = streams;
      result[20] = coupledStreams;
      mapping.CopyTo(result, 21);
    }
    return result;
  }

  private static byte[] BuildTags(OpusEncoderOptions options) {
    var vendor = Encoding.UTF8.GetBytes(options.Vendor ?? string.Empty);
    var comments = options.Comments ?? Array.Empty<string>();
    using var stream = new MemoryStream();
    stream.Write("OpusTags"u8);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)vendor.Length);
    stream.Write(u32);
    stream.Write(vendor);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)comments.Count);
    stream.Write(u32);
    foreach (var comment in comments) {
      var bytes = Encoding.UTF8.GetBytes(comment);
      BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)bytes.Length);
      stream.Write(u32);
      stream.Write(bytes);
    }
    return stream.ToArray();
  }

  private static void WriteOggPage(Stream output, ReadOnlySpan<byte> packet, int serial, uint sequence,
    long granulePosition, bool bos, bool eos) {
    var segments = packet.Length / 255 + 1;
    if (segments > 255)
      throw new ArgumentOutOfRangeException(nameof(packet), "One Opus packet must fit one Ogg page.");

    var header = new byte[27 + segments];
    "OggS"u8.CopyTo(header);
    header[4] = 0;
    header[5] = (byte)((bos ? 0x02 : 0) | (eos ? 0x04 : 0));
    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6, 8), granulePosition);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14, 4), serial);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18, 4), sequence);
    header[26] = (byte)segments;
    var remaining = packet.Length;
    for (var i = 0; i < segments; ++i) {
      var length = Math.Min(255, remaining);
      header[27 + i] = (byte)length;
      remaining -= length;
    }

    var page = new byte[header.Length + packet.Length];
    header.CopyTo(page, 0);
    packet.CopyTo(page.AsSpan(header.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), OggCrc(page));
    output.Write(page);
  }

  private static uint OggCrc(ReadOnlySpan<byte> data) {
    uint crc = 0;
    foreach (var value in data) {
      crc ^= (uint)value << 24;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
    }
    return crc;
  }

  /// <summary>Our ordinal bandwidth as the value the encoder expects.</summary>
  /// <remarks>
  /// The two enumerations do not share numbering: ours counts from zero, the
  /// encoder's narrowband is 1101. Casting one to the other by value asks for a
  /// bandwidth that does not exist.
  /// </remarks>
  private static ConcentusOpusBandwidth ToConcentus(OpusBandwidth bandwidth)
    => ConcentusOpusBandwidth.OPUS_BANDWIDTH_NARROWBAND + (int)bandwidth;
}
