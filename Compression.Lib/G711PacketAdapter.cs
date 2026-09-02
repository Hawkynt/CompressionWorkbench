using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Aiff;
using FileFormat.Au;
using FileFormat.Wav;

namespace Compression.Lib;

/// <summary>
/// Packet-preserving G.711 adapter for WAVE, AIFC and AU. The encoded A-law/μ-law bytes are
/// treated as opaque payload and only the destination container framing is rebuilt.
/// </summary>
internal sealed class G711PacketAdapter : IAudioDemuxSource, IAudioMuxTarget {
  private enum ContainerKind {
    Wav,
    Aiff,
    Au,
  }

  private static readonly string[] Codecs = ["alaw", "mulaw"];

  internal static readonly G711PacketAdapter Wav = new(ContainerKind.Wav);
  internal static readonly G711PacketAdapter Aiff = new(ContainerKind.Aiff);
  internal static readonly G711PacketAdapter Au = new(ContainerKind.Au);

  private readonly ContainerKind _container;

  private G711PacketAdapter(ContainerKind container) => this._container = container;

  public IReadOnlyList<string> SupportedMuxCodecs => Codecs;

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    stream = this._container switch {
      ContainerKind.Wav => TryDemuxWav(input),
      ContainerKind.Aiff => TryDemuxAiff(input),
      ContainerKind.Au => TryDemuxAu(input),
      _ => null,
    };
    return stream is not null;
  }

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!Codecs.Contains(stream.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"{this._container} packet mux supports only G.711 A-law/μ-law";
      return false;
    }
    if (stream.SampleRate <= 0 || stream.Channels <= 0) {
      reason = "G.711 requires a positive sample rate and channel count";
      return false;
    }
    reason = null;
    return true;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);

    var payload = ConcatenatePayload(stream.Packets);
    if (payload.Length % stream.Format.Channels != 0)
      throw new InvalidDataException("G.711 payload length is not divisible by the channel count.");
    var frames = ResolveFrameCount(stream, payload.Length);
    var aLaw = stream.Format.CodecId.Equals("alaw", StringComparison.OrdinalIgnoreCase);

    switch (this._container) {
      case ContainerKind.Wav:
        WriteWav(output, payload, stream.Format.SampleRate, stream.Format.Channels, frames, aLaw);
        break;
      case ContainerKind.Aiff:
        WriteAifc(output, payload, stream.Format.SampleRate, stream.Format.Channels, frames, aLaw);
        break;
      case ContainerKind.Au:
        WriteAu(output, payload, stream.Format.SampleRate, stream.Format.Channels, aLaw);
        break;
    }
  }

  private static AudioEncodedStream? TryDemuxWav(Stream input) {
    using var materialized = Materialize(input);
    var parsed = new WavReader().Read(materialized.ToArray());
    var codec = parsed.FormatCode switch {
      0x0006 => "alaw",
      0x0007 => "mulaw",
      _ => null,
    };
    if (codec is null) return null;
    var frames = parsed.NumChannels == 0 ? 0 : parsed.InterleavedPcm.LongLength / parsed.NumChannels;
    return Encoded(codec, parsed.SampleRate, parsed.NumChannels, parsed.InterleavedPcm, frames);
  }

  private static AudioEncodedStream? TryDemuxAiff(Stream input) {
    using var materialized = Materialize(input);
    var parsed = new AiffReader().Read(materialized.ToArray());
    if (!parsed.IsAifc) return null;
    var codec = parsed.CompressionId switch {
      "alaw" or "ALAW" => "alaw",
      "ulaw" or "ULAW" => "mulaw",
      _ => null,
    };
    if (codec is null) return null;
    var frames = parsed.SampleFrames > 0
      ? parsed.SampleFrames
      : parsed.NumChannels == 0 ? 0 : parsed.SoundData.LongLength / parsed.NumChannels;
    return Encoded(codec, parsed.SampleRate, parsed.NumChannels, parsed.SoundData, frames);
  }

  private static AudioEncodedStream? TryDemuxAu(Stream input) {
    using var materialized = Materialize(input);
    var parsed = new AuReader().Read(materialized.ToArray());
    var codec = parsed.Encoding switch {
      1 => "mulaw",
      27 => "alaw",
      _ => null,
    };
    if (codec is null) return null;
    var frames = parsed.NumChannels == 0 ? 0 : parsed.SoundData.LongLength / parsed.NumChannels;
    return Encoded(codec, parsed.SampleRate, parsed.NumChannels, parsed.SoundData, frames);
  }

  private static AudioEncodedStream Encoded(string codec, int sampleRate, int channels, byte[] payload, long frames)
    => new(
      new AudioStreamFormat(codec, sampleRate, channels, 8),
      [new AudioPacket((byte[])payload.Clone(), frames)]);

  private static byte[] ConcatenatePayload(IReadOnlyList<AudioPacket> packets) {
    var length = packets.Where(static packet => !packet.IsHeader).Sum(static packet => (long)packet.Data.Length);
    if (length > int.MaxValue) throw new NotSupportedException("G.711 remux payload exceeds the in-memory writer limit.");
    var result = new byte[(int)length];
    var offset = 0;
    foreach (var packet in packets) {
      if (packet.IsHeader) continue;
      packet.Data.CopyTo(result, offset);
      offset += packet.Data.Length;
    }
    return result;
  }

  private static uint ResolveFrameCount(AudioEncodedStream stream, int payloadLength) {
    long frames = 0;
    var hasDurations = false;
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader || packet.DurationSamples <= 0) continue;
      frames = checked(frames + packet.DurationSamples);
      hasDurations = true;
    }
    if (!hasDurations) frames = payloadLength / stream.Format.Channels;
    return checked((uint)frames);
  }

  private static void WriteWav(Stream output, byte[] payload, int sampleRate, int channels, uint frames, bool aLaw) {
    var blockAlign = checked((ushort)channels);
    const int fmtBodyLength = 18;
    const int factChunkLength = 12;
    var dataPadded = payload.Length + (payload.Length & 1);
    var riffPayloadLength = checked(4 + 8 + fmtBodyLength + factChunkLength + 8 + dataPadded);

    Span<byte> riff = stackalloc byte[12];
    "RIFF"u8.CopyTo(riff);
    BinaryPrimitives.WriteUInt32LittleEndian(riff[4..], checked((uint)riffPayloadLength));
    "WAVE"u8.CopyTo(riff[8..]);
    output.Write(riff);

    Span<byte> fmt = stackalloc byte[26];
    "fmt "u8.CopyTo(fmt);
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[4..], fmtBodyLength);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[8..], aLaw ? (ushort)0x0006 : (ushort)0x0007);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[10..], checked((ushort)channels));
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[12..], checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32LittleEndian(fmt[16..], checked((uint)(sampleRate * channels)));
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[20..], blockAlign);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[22..], 8);
    BinaryPrimitives.WriteUInt16LittleEndian(fmt[24..], 0);
    output.Write(fmt);

    Span<byte> fact = stackalloc byte[12];
    "fact"u8.CopyTo(fact);
    BinaryPrimitives.WriteUInt32LittleEndian(fact[4..], 4);
    BinaryPrimitives.WriteUInt32LittleEndian(fact[8..], frames);
    output.Write(fact);

    WriteLittleEndianChunk(output, "data"u8, payload);
  }

  private static void WriteAifc(Stream output, byte[] payload, int sampleRate, int channels, uint frames, bool aLaw) {
    using var commBody = new MemoryStream();
    Span<byte> fixedComm = stackalloc byte[18];
    BinaryPrimitives.WriteInt16BigEndian(fixedComm, checked((short)channels));
    BinaryPrimitives.WriteUInt32BigEndian(fixedComm[2..], frames);
    BinaryPrimitives.WriteInt16BigEndian(fixedComm[6..], 16);
    AiffWriter.Encode80BitFloat(sampleRate).CopyTo(fixedComm[8..]);
    commBody.Write(fixedComm);
    commBody.Write(Encoding.ASCII.GetBytes(aLaw ? "alaw" : "ulaw"));
    var compressionName = Encoding.ASCII.GetBytes(aLaw ? "A-law 2:1" : "mu-law 2:1");
    commBody.WriteByte((byte)compressionName.Length);
    commBody.Write(compressionName);

    var fver = WrapBigEndianChunk("FVER"u8, [0xA2, 0x80, 0x51, 0x40]);
    var comm = WrapBigEndianChunk("COMM"u8, commBody.ToArray());
    var ssndBody = new byte[8 + payload.Length];
    payload.CopyTo(ssndBody, 8);
    var ssnd = WrapBigEndianChunk("SSND"u8, ssndBody);
    var formSize = checked(4 + fver.Length + comm.Length + ssnd.Length);

    Span<byte> header = stackalloc byte[12];
    "FORM"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)formSize));
    "AIFC"u8.CopyTo(header[8..]);
    output.Write(header);
    output.Write(fver);
    output.Write(comm);
    output.Write(ssnd);
  }

  private static void WriteAu(Stream output, byte[] payload, int sampleRate, int channels, bool aLaw) {
    Span<byte> header = stackalloc byte[24];
    ".snd"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], 24);
    BinaryPrimitives.WriteUInt32BigEndian(header[8..], checked((uint)payload.Length));
    BinaryPrimitives.WriteUInt32BigEndian(header[12..], aLaw ? 27u : 1u);
    BinaryPrimitives.WriteUInt32BigEndian(header[16..], checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32BigEndian(header[20..], checked((uint)channels));
    output.Write(header);
    output.Write(payload);
  }

  private static byte[] WrapBigEndianChunk(ReadOnlySpan<byte> id, byte[] body) {
    var paddedLength = body.Length + (body.Length & 1);
    var result = new byte[8 + paddedLength];
    id.CopyTo(result);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), checked((uint)body.Length));
    body.CopyTo(result, 8);
    return result;
  }

  private static void WriteLittleEndianChunk(Stream output, ReadOnlySpan<byte> id, byte[] body) {
    Span<byte> header = stackalloc byte[8];
    id.CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)body.Length));
    output.Write(header);
    output.Write(body);
    if ((body.Length & 1) != 0) output.WriteByte(0);
  }

  private static MemoryStream Materialize(Stream input) {
    if (input.CanSeek) input.Position = 0;
    var memory = new MemoryStream();
    input.CopyTo(memory);
    memory.Position = 0;
    return memory;
  }
}
