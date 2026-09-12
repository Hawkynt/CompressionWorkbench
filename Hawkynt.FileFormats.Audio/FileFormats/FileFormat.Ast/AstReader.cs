#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.AdpcmX;

namespace FileFormat.Ast;

/// <summary>
/// Parses GameCube/Wii AST STRM streams, retaining the encoded BLCK structure for packet-preserving
/// remux while also decoding both format-defined codecs to PCM16.
/// </summary>
public sealed class AstReader {

  /// <summary>Parsed STRM header.</summary>
  public sealed record Header(
    int Codec,
    int BitDepth,
    int NumChannels,
    bool Loop,
    int SampleRate,
    int SampleCount,
    int LoopStart,
    int LoopEnd) {
    public uint DataSize { get; init; }
    public ushort LoopFlag { get; init; }
    public int FirstBlockSize { get; init; }
    public byte Volume { get; init; }
    public byte[] ReservedHeader { get; init; } = [];
  }

  /// <summary>One BLCK chunk, split into its per-channel encoded byte ranges.</summary>
  public sealed record Block(int SizePerChannel, byte[][] Channels, byte[] HeaderData);

  /// <summary>Parsed container, decoded PCM and original encoded block structure.</summary>
  public sealed record ParsedAst(Header Info, short[][] Pcm) {
    public IReadOnlyList<Block> Blocks { get; init; } = [];
  }

  /// <summary>Reads and validates a complete AST stream.</summary>
  public ParsedAst Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x40)
      throw new InvalidDataException("AST too short for STRM header.");
    if (!data[..4].SequenceEqual("STRM"u8))
      throw new InvalidDataException("Missing STRM magic.");

    var dataSize = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
    var codec = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
    var bitDepth = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
    var channels = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
    var loopFlag = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
    var sampleRate = ReadPositiveInt32(data[16..], "sample rate");
    var sampleCount = ReadInt32(data[20..], "sample count");
    var loopStart = ReadInt32(data[24..], "loop start");
    var loopEnd = ReadInt32(data[28..], "loop end");
    var firstBlockSize = ReadInt32(data[32..], "first block size");

    if (bitDepth != 16)
      throw new InvalidDataException($"AST bit depth {bitDepth} is unsupported; STRM audio is 16-bit.");
    if (channels == 0)
      throw new InvalidDataException("AST has no channels.");

    var logicalEnd = dataSize == 0
      ? data.Length
      : checked(0x40L + dataSize) <= data.Length
        ? checked((int)(0x40L + dataSize))
        : throw new InvalidDataException("AST dataSize exceeds the available input.");

    var blocks = ParseBlocks(data[..logicalEnd], channels, codec);
    var info = new Header(codec, bitDepth, channels, loopFlag != 0, sampleRate, sampleCount, loopStart, loopEnd) {
      DataSize = dataSize,
      LoopFlag = loopFlag,
      FirstBlockSize = firstBlockSize,
      Volume = data[0x28],
      ReservedHeader = data.Slice(0x24, 0x1C).ToArray(),
    };

    var pcm = codec switch {
      (int)AstCodec.Pcm16BigEndian => DecodePcm16(blocks, info),
      (int)AstCodec.Afc => DecodeAfc(blocks, info),
      _ => [],
    };
    return new ParsedAst(info, pcm) { Blocks = blocks };
  }

  private static List<Block> ParseBlocks(ReadOnlySpan<byte> data, int channels, int codec) {
    var blocks = new List<Block>();
    var position = 0x40;
    while (position < data.Length) {
      if (data.Length - position < 32)
        throw new InvalidDataException("Truncated AST BLCK header.");
      if (!data.Slice(position, 4).SequenceEqual("BLCK"u8))
        throw new InvalidDataException($"Missing BLCK magic at offset 0x{position:X}.");

      var sizeValue = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
      if (sizeValue > int.MaxValue)
        throw new InvalidDataException("AST BLCK per-channel size exceeds the supported range.");
      var size = (int)sizeValue;
      if (codec == (int)AstCodec.Pcm16BigEndian && (size & 1) != 0)
        throw new InvalidDataException("PCM16 AST BLCK size is not divisible by two.");
      if (codec == (int)AstCodec.Afc && size % Thp.AfcBytesPerFrame != 0)
        throw new InvalidDataException($"AFC AST BLCK size is not divisible by {Thp.AfcBytesPerFrame}.");

      var payloadBytes = checked((long)size * channels);
      var payloadOffset = position + 32;
      if (payloadBytes > data.Length - payloadOffset)
        throw new InvalidDataException("AST BLCK payload overruns the input.");

      var channelData = new byte[channels][];
      for (var channel = 0; channel < channels; ++channel)
        channelData[channel] = data.Slice(payloadOffset + channel * size, size).ToArray();

      blocks.Add(new Block(size, channelData, data.Slice(position + 8, 24).ToArray()));
      position = checked(payloadOffset + (int)payloadBytes);
    }

    return blocks;
  }

  private static short[][] DecodeAfc(IReadOnlyList<Block> blocks, Header info) {
    var encodedBytes = blocks.Sum(static block => (long)block.SizePerChannel);
    var capacity = encodedBytes / Thp.AfcBytesPerFrame * Thp.AfcSamplesPerFrame;
    if (info.SampleCount > capacity)
      throw new InvalidDataException("AST AFC sample count exceeds the encoded BLCK capacity.");
    if (encodedBytes > int.MaxValue)
      throw new InvalidDataException("AST AFC channel stream is too large to materialize.");

    var result = new short[info.NumChannels][];
    for (var channel = 0; channel < info.NumChannels; ++channel) {
      var encoded = new byte[(int)encodedBytes];
      var offset = 0;
      foreach (var block in blocks) {
        block.Channels[channel].CopyTo(encoded, offset);
        offset += block.SizePerChannel;
      }
      result[channel] = Thp.DecodeAfc(encoded, info.SampleCount);
    }
    return result;
  }

  private static short[][] DecodePcm16(IReadOnlyList<Block> blocks, Header info) {
    var capacity = blocks.Sum(static block => (long)block.SizePerChannel / sizeof(short));
    if (info.SampleCount > capacity)
      throw new InvalidDataException("AST PCM sample count exceeds the encoded BLCK capacity.");

    var result = new short[info.NumChannels][];
    for (var channel = 0; channel < info.NumChannels; ++channel) {
      var samples = new short[info.SampleCount];
      var produced = 0;
      foreach (var block in blocks) {
        var bytes = block.Channels[channel];
        for (var offset = 0; offset < bytes.Length && produced < samples.Length; offset += sizeof(short))
          samples[produced++] = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(offset));
      }
      result[channel] = samples;
    }
    return result;
  }

  private static int ReadPositiveInt32(ReadOnlySpan<byte> data, string field) {
    var value = ReadInt32(data, field);
    if (value <= 0)
      throw new InvalidDataException($"AST {field} must be positive.");
    return value;
  }

  private static int ReadInt32(ReadOnlySpan<byte> data, string field) {
    var value = BinaryPrimitives.ReadUInt32BigEndian(data);
    if (value > int.MaxValue)
      throw new InvalidDataException($"AST {field} exceeds the supported signed 32-bit range.");
    return (int)value;
  }
}
