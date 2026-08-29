using System.Buffers.Binary;
using Codec.ALaw;
using Codec.ImaAdpcm;
using Codec.MuLaw;
using Compression.Registry;
using FileFormat.Caf;

namespace Compression.Lib;

/// <summary>Canonical PCM/G.711/QuickTime-IMA adapter for Apple Core Audio Format.</summary>
internal sealed class CafAudioAdapter : IAudioPcmSource, IAudioPcmTarget {
  private const uint FlagIsFloat = 0x1;
  private const uint FlagIsSignedInteger = 0x4;
  private const uint FlagIsPacked = 0x8;
  private const int Ima4PacketBytesPerChannel = 34;
  private const int Ima4FramesPerPacket = 64;
  private static readonly string[] Codecs = ["lpcm", "pcm", "float", "mulaw", "alaw", "ima4"];

  public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    if (input.CanSeek) input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    var parsed = new CafReader().Read(memory.ToArray());
    if (parsed.FormatId != "lpcm")
      throw new NotSupportedException($"CAF codec '{parsed.FormatId}' is not decoded by this adapter.");
    return new AudioPcmBuffer(
      new AudioPcmFormat(
        parsed.SampleRate,
        parsed.NumChannels,
        parsed.BitsPerSample,
        parsed.IsFloat ? AudioPcmEncoding.IeeeFloat : AudioPcmEncoding.SignedInteger,
        parsed.ChannelMask),
      parsed.InterleavedPcm);
  }

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"CAF codec '{codecId}' is not supported by this writer";
      return false;
    }
    if (format.Channels < 1 || format.SampleRate < 1) {
      reason = "CAF requires a positive sample rate and at least one channel";
      return false;
    }
    var codec = codecId.ToLowerInvariant();
    if (codec == "ima4") {
      if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
        reason = "CAF ima4 encoding requires signed PCM16 input";
        return false;
      }
      reason = null;
      return true;
    }
    if (codec is "mulaw" or "alaw") {
      if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
        reason = "CAF G.711 encoding requires signed PCM16 input";
        return false;
      }
      reason = null;
      return true;
    }
    if (codec == "float") {
      reason = format.Encoding == AudioPcmEncoding.IeeeFloat && format.BitsPerSample is 32 or 64
        ? null : "CAF float encoding requires 32- or 64-bit IEEE-float PCM";
      return reason is null;
    }
    if (format.Encoding == AudioPcmEncoding.IeeeFloat) {
      reason = "floating-point PCM must select the 'float' CAF codec";
      return false;
    }
    if (format.BitsPerSample is not (8 or 16 or 24 or 32)) {
      reason = "CAF integer LPCM supports 8/16/24/32 bits";
      return false;
    }
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    switch (codecId.ToLowerInvariant()) {
      case "lpcm":
      case "pcm":
        WriteCaf(output, pcm.Format.SampleRate, pcm.Format.Channels, "lpcm", FlagIsSignedInteger | FlagIsPacked,
          checked((uint)pcm.Format.BytesPerFrame), 1, checked((uint)pcm.Format.BitsPerSample), NormalizeSignedEightBit(pcm));
        break;
      case "float":
        WriteCaf(output, pcm.Format.SampleRate, pcm.Format.Channels, "lpcm", FlagIsFloat | FlagIsPacked,
          checked((uint)pcm.Format.BytesPerFrame), 1, checked((uint)pcm.Format.BitsPerSample), pcm.InterleavedData);
        break;
      case "mulaw":
        WriteG711(output, pcm, aLaw: false);
        break;
      case "alaw":
        WriteG711(output, pcm, aLaw: true);
        break;
      case "ima4":
        WriteIma4(output, pcm);
        break;
    }
  }

  private static void WriteIma4(Stream output, AudioPcmBuffer pcm) {
    var samples = ReadPcm16(pcm.InterleavedData);
    var payload = ImaAdpcmCodec.EncodeQuickTime(samples, pcm.Format.Channels);
    var packetBytes = checked(Ima4PacketBytesPerChannel * pcm.Format.Channels);
    var packetCount = payload.Length / packetBytes;
    var validFrames = pcm.FrameCount;
    var codedFrames = checked((long)packetCount * Ima4FramesPerPacket);
    var remainderFrames = checked((int)(codedFrames - validFrames));
    WriteCaf(output, pcm.Format.SampleRate, pcm.Format.Channels, "ima4", 0,
      checked((uint)packetBytes), Ima4FramesPerPacket, 0, payload,
      packetCount, validFrames, remainderFrames);
  }

  private static void WriteG711(Stream output, AudioPcmBuffer pcm, bool aLaw) {
    var samples = ReadPcm16(pcm.InterleavedData);
    var payload = aLaw ? ALawCodec.Encode(samples) : MuLawCodec.Encode(samples);
    WriteCaf(output, pcm.Format.SampleRate, pcm.Format.Channels, aLaw ? "alaw" : "ulaw", 0,
      checked((uint)pcm.Format.Channels), 1, 8, payload);
  }

  private static void WriteCaf(Stream output, int sampleRate, int channels, string formatId,
    uint formatFlags, uint bytesPerPacket, uint framesPerPacket, uint bitsPerChannel, ReadOnlySpan<byte> payload,
    long? packetCount = null, long? validFrames = null, int remainderFrames = 0) {
    Span<byte> header = stackalloc byte[8];
    "caff"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
    BinaryPrimitives.WriteUInt16BigEndian(header[6..], 0);
    output.Write(header);

    Span<byte> desc = stackalloc byte[32];
    BinaryPrimitives.WriteDoubleBigEndian(desc, sampleRate);
    System.Text.Encoding.ASCII.GetBytes(formatId, desc[8..12]);
    BinaryPrimitives.WriteUInt32BigEndian(desc[12..], formatFlags);
    BinaryPrimitives.WriteUInt32BigEndian(desc[16..], bytesPerPacket);
    BinaryPrimitives.WriteUInt32BigEndian(desc[20..], framesPerPacket);
    BinaryPrimitives.WriteUInt32BigEndian(desc[24..], checked((uint)channels));
    BinaryPrimitives.WriteUInt32BigEndian(desc[28..], bitsPerChannel);
    WriteChunk(output, "desc"u8, desc);

    if (packetCount is { } packets && validFrames is { } frames) {
      Span<byte> pakt = stackalloc byte[24];
      BinaryPrimitives.WriteInt64BigEndian(pakt, packets);
      BinaryPrimitives.WriteInt64BigEndian(pakt[8..], frames);
      BinaryPrimitives.WriteInt32BigEndian(pakt[16..], 0);
      BinaryPrimitives.WriteInt32BigEndian(pakt[20..], remainderFrames);
      WriteChunk(output, "pakt"u8, pakt);
    }

    var data = new byte[4 + payload.Length];
    payload.CopyTo(data.AsSpan(4));
    WriteChunk(output, "data"u8, data);
  }

  private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body) {
    if (type.Length != 4) throw new ArgumentException("CAF chunk type must be four bytes.", nameof(type));
    Span<byte> header = stackalloc byte[12];
    type.CopyTo(header);
    BinaryPrimitives.WriteInt64BigEndian(header[4..], body.Length);
    output.Write(header);
    output.Write(body);
  }

  private static byte[] NormalizeSignedEightBit(AudioPcmBuffer pcm) {
    var data = (byte[])pcm.InterleavedData.Clone();
    if (pcm.Format.BitsPerSample == 8 && pcm.Format.Encoding == AudioPcmEncoding.UnsignedInteger)
      for (var i = 0; i < data.Length; ++i) data[i] ^= 0x80;
    return data;
  }

  private static short[] ReadPcm16(ReadOnlySpan<byte> data) {
    if ((data.Length & 1) != 0) throw new InvalidDataException("PCM16 payload has odd length.");
    var samples = new short[data.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
    return samples;
  }
}
