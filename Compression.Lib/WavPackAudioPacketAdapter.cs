using System.Buffers.Binary;
using Codec.WavPack;
using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Packet-preserving WavPack v4/v5 adapter based on the published 32-byte block header specification.
/// </summary>
internal sealed class WavPackAudioPacketAdapter : IAudioDemuxSource, IAudioMuxTarget {
  internal static readonly WavPackAudioPacketAdapter Instance = new();

  private const int HeaderSize = 32;
  private static readonly string[] MuxCodecs = ["wavpack"];
  private static readonly int[] SampleRates = [
    6000, 8000, 9600, 11025, 12000, 16000, 22050, 24000,
    32000, 44100, 48000, 64000, 88200, 96000, 192000, 0,
  ];

  public IReadOnlyList<string> SupportedMuxCodecs => MuxCodecs;

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var bytes = Materialize(input);
    stream = null;
    if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("wvpk"u8)) return false;

    var packets = new List<AudioPacket>();
    WavPackBlockHeader? firstAudio = null;
    var offset = 0;
    var trailerStart = -1;
    while (offset < bytes.Length) {
      // APEv2 is WavPack's own tagging format and ID3v1 the legacy alternative,
      // so a tagged file is the normal case rather than a damaged one. Both sit
      // behind the last block, where a reader that only knows blocks sees
      // garbage.
      if (ApeTagReader.IsTrailingMetadata(bytes, offset)) {
        trailerStart = offset;
        offset = bytes.Length;
        break;
      }
      if (bytes.Length - offset < HeaderSize)
        throw new InvalidDataException($"Truncated WavPack block header at byte offset {offset}.");
      var header = ParseBlockHeader(bytes.AsSpan(offset, HeaderSize));
      var blockSize = checked((long)header.CkSize + 8L);
      if (blockSize < HeaderSize)
        throw new InvalidDataException($"Invalid WavPack block size {blockSize} at byte offset {offset}.");
      if (blockSize > bytes.Length - offset)
        throw new InvalidDataException($"Truncated WavPack block at byte offset {offset}: expected {blockSize} bytes.");
      if (blockSize > int.MaxValue)
        throw new InvalidDataException("WavPack block exceeds the supported in-memory packet size.");

      var packetBytes = bytes.AsSpan(offset, (int)blockSize).ToArray();
      var granulePosition = checked((long)(header.BlockIndex + header.BlockSamples));
      packets.Add(new AudioPacket(packetBytes, header.BlockSamples, granulePosition));
      if (header.BlockSamples != 0) firstAudio ??= header;
      offset += (int)blockSize;
    }

    if (offset != bytes.Length)
      throw new InvalidDataException($"Unexpected trailing data after WavPack blocks at byte offset {offset}.");
    if (packets.Count == 0)
      throw new InvalidDataException("WavPack stream contains no complete blocks.");

    var fallback = firstAudio ?? ParseBlockHeader(packets[0].Data);
    var sampleRate = SampleRates[(int)((fallback.Flags >> 23) & 0xF)];
    var channels = (fallback.Flags & 0x4) != 0 ? 1 : 2;
    var bitsPerSample = ((int)(fallback.Flags & 0x3) + 1) * 8;
    var isFloat = (fallback.Flags & 0x80) != 0;
    try {
      using var probe = new MemoryStream(bytes, writable: false);
      var info = WavPackCodec.ReadStreamInfo(probe);
      sampleRate = info.SampleRate;
      channels = info.Channels;
      bitsPerSample = info.BitsPerSample;
      isFloat = info.IsFloat;
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
      // Packet preservation is structural and can remain valid for a legal stream variant unsupported
      // by the local PCM decoder. The block header still supplies baseline stream information.
    }

    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["wavpack-version"] = $"0x{fallback.Version:X4}",
      ["block-count"] = packets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
      ["sample-format"] = isFloat ? "float" : "integer",
    };
    if (trailerStart >= 0)
      properties["trailing-metadata-bytes"] =
        (bytes.Length - trailerStart).ToString(System.Globalization.CultureInfo.InvariantCulture);
    stream = new AudioEncodedStream(
      new AudioStreamFormat("wavpack", sampleRate, channels, bitsPerSample, properties),
      packets);
    return true;
  }

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!stream.CodecId.Equals("wavpack", StringComparison.OrdinalIgnoreCase)) {
      reason = $"raw WavPack accepts WavPack blocks, not codec '{stream.CodecId}'";
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
    if (stream.Packets.Count == 0)
      throw new ArgumentException("WavPack muxing requires at least one block.", nameof(stream));

    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        throw new InvalidDataException("WavPack block streams do not use out-of-band header packets.");
      if (packet.Data.Length < HeaderSize)
        throw new InvalidDataException("WavPack packet is shorter than the 32-byte block header.");
      var header = ParseBlockHeader(packet.Data);
      var expectedLength = checked((long)header.CkSize + 8L);
      if (expectedLength != packet.Data.Length)
        throw new InvalidDataException($"WavPack packet length {packet.Data.Length} does not match block size {expectedLength}.");
      if (packet.DurationSamples > 0 && packet.DurationSamples != header.BlockSamples)
        throw new InvalidDataException("WavPack packet duration does not match block_samples.");
      output.Write(packet.Data);
    }
  }

  private static WavPackBlockHeader ParseBlockHeader(ReadOnlySpan<byte> bytes) {
    if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual("wvpk"u8))
      throw new InvalidDataException("WavPack packet does not begin with a valid 'wvpk' block header.");
    var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
    if (version is < 0x0402 or > 0x0410)
      throw new InvalidDataException($"Unsupported WavPack block version 0x{version:X4}.");
    var blockIndex = ((ulong)bytes[10] << 32) | BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
    return new WavPackBlockHeader(
      BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
      version,
      blockIndex,
      BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
      BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]));
  }

  private static byte[] Materialize(Stream input) {
    using var copy = new MemoryStream();
    input.CopyTo(copy);
    return copy.ToArray();
  }

  private readonly record struct WavPackBlockHeader(
    uint CkSize,
    ushort Version,
    ulong BlockIndex,
    uint BlockSamples,
    uint Flags);
}
