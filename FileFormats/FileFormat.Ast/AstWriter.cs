#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.AdpcmX;

namespace FileFormat.Ast;

/// <summary>Codec identifiers stored in the AST STRM header.</summary>
public enum AstCodec : ushort {
  Afc = 0,
  Pcm16BigEndian = 1,
}

/// <summary>Container-level options supported by the AST writer.</summary>
public sealed record AstWriterOptions(
  AstCodec Codec = AstCodec.Pcm16BigEndian,
  int BlockSize = AstWriter.BlockSize,
  bool Loop = false,
  int LoopStart = 0,
  int? LoopEnd = null,
  byte Volume = 0x7F);

/// <summary>
/// Writes big-endian GameCube/Wii <c>.ast</c> (STRM) audio. Both format-defined codecs are
/// supported: planar PCM16BE (codec 1) and Nintendo AFC ADPCM (codec 0). Audio is split into
/// <c>BLCK</c> chunks whose size field is the byte count for one channel.
/// </summary>
public sealed class AstWriter {

  /// <summary>The conventional AST block size, in bytes per channel.</summary>
  public const int BlockSize = 0x2760;

  /// <summary>
  /// Backward-compatible PCM16BE writer overload.
  /// </summary>
  public byte[] Write(IReadOnlyList<short[]> channels, int sampleRate, bool loop = false,
                      int loopStart = 0, int loopEnd = 0)
    => this.Write(channels, sampleRate, new AstWriterOptions(
      Loop: loop,
      LoopStart: loopStart,
      LoopEnd: loop && loopEnd > 0 ? loopEnd : null));

  /// <summary>
  /// Serializes equal-length mono PCM16 channels using the requested AST codec and container
  /// geometry. PCM16 is lossless; AFC is encoded independently per channel using its fixed
  /// predictor table.
  /// </summary>
  public byte[] Write(IReadOnlyList<short[]> channels, int sampleRate, AstWriterOptions options) {
    ArgumentNullException.ThrowIfNull(channels);
    ArgumentNullException.ThrowIfNull(options);
    if (channels.Count == 0)
      throw new ArgumentException("AST needs at least one channel.", nameof(channels));
    if (channels.Count > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(channels), "AST stores the channel count in 16 bits.");
    if (options.Codec == AstCodec.Afc && channels.Count > 6)
      throw new ArgumentOutOfRangeException(nameof(channels), "AST AFC BLCK headers carry histories for at most six channels.");
    if (sampleRate <= 0)
      throw new ArgumentOutOfRangeException(nameof(sampleRate));

    var sampleCount = channels[0].Length;
    if (channels.Any(channel => channel.Length != sampleCount))
      throw new ArgumentException("All channels must have the same sample count.", nameof(channels));

    ValidateOptions(options, sampleCount);

    var encoded = new byte[channels.Count][];
    var decodedAfc = options.Codec == AstCodec.Afc ? new short[channels.Count][] : null;
    for (var channel = 0; channel < channels.Count; ++channel) {
      if (options.Codec == AstCodec.Afc) {
        encoded[channel] = Thp.EncodeAfc(channels[channel]);
        decodedAfc![channel] = Thp.DecodeAfc(encoded[channel], sampleCount);
      } else {
        encoded[channel] = EncodePcm16BigEndian(channels[channel]);
      }
    }

    var encodedLength = encoded[0].Length;
    var blockCount = encodedLength == 0 ? 0 : checked((encodedLength + options.BlockSize - 1) / options.BlockSize);
    using var output = new MemoryStream();
    output.Write(new byte[0x40]);

    var firstBlockSize = 0;
    for (var blockIndex = 0; blockIndex < blockCount; ++blockIndex) {
      var encodedOffset = checked(blockIndex * options.BlockSize);
      var perChannelSize = Math.Min(options.BlockSize, encodedLength - encodedOffset);
      if (blockIndex == 0)
        firstBlockSize = perChannelSize;

      Span<byte> blockHeader = stackalloc byte[32];
      blockHeader.Clear();
      "BLCK"u8.CopyTo(blockHeader);
      BinaryPrimitives.WriteUInt32BigEndian(blockHeader[4..], checked((uint)perChannelSize));
      if (decodedAfc is not null)
        WriteAfcHistories(blockHeader[8..], decodedAfc, encodedOffset);
      output.Write(blockHeader);

      for (var channel = 0; channel < channels.Count; ++channel)
        output.Write(encoded[channel], encodedOffset, perChannelSize);
    }

    var file = output.ToArray();
    WriteHeader(file, options, channels.Count, sampleRate, sampleCount, firstBlockSize);
    return file;
  }

  private static void ValidateOptions(AstWriterOptions options, int sampleCount) {
    if (options.BlockSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(options), "AST block size must be positive.");
    if (options.Codec == AstCodec.Pcm16BigEndian && (options.BlockSize & 1) != 0)
      throw new ArgumentException("PCM16 AST block size must be divisible by two.", nameof(options));
    if (options.Codec == AstCodec.Afc && options.BlockSize % Thp.AfcBytesPerFrame != 0)
      throw new ArgumentException($"AFC AST block size must be divisible by {Thp.AfcBytesPerFrame} bytes.", nameof(options));
    if (!options.Loop)
      return;

    var loopEnd = options.LoopEnd ?? sampleCount;
    if (sampleCount == 0 || options.LoopStart < 0 || options.LoopStart >= loopEnd || loopEnd > sampleCount)
      throw new ArgumentOutOfRangeException(nameof(options), "AST loop points must satisfy 0 <= start < end <= sample count.");
    if (options.Codec == AstCodec.Afc && options.LoopStart % Thp.AfcSamplesPerFrame != 0)
      throw new ArgumentException($"AFC loop start must be aligned to {Thp.AfcSamplesPerFrame} samples.", nameof(options));
  }

  private static byte[] EncodePcm16BigEndian(ReadOnlySpan<short> samples) {
    var result = new byte[checked(samples.Length * sizeof(short))];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16BigEndian(result.AsSpan(i * sizeof(short)), samples[i]);
    return result;
  }

  private static void WriteAfcHistories(Span<byte> destination, short[][] decoded, int encodedOffset) {
    var sampleOffset = checked(encodedOffset / Thp.AfcBytesPerFrame * Thp.AfcSamplesPerFrame);
    for (var channel = 0; channel < decoded.Length; ++channel) {
      var history1 = sampleOffset > 0 && sampleOffset <= decoded[channel].Length
        ? decoded[channel][sampleOffset - 1]
        : (short)0;
      var history2 = sampleOffset > 1 && sampleOffset <= decoded[channel].Length
        ? decoded[channel][sampleOffset - 2]
        : (short)0;
      BinaryPrimitives.WriteInt16BigEndian(destination[(channel * 4)..], history1);
      BinaryPrimitives.WriteInt16BigEndian(destination[(channel * 4 + 2)..], history2);
    }
  }

  private static void WriteHeader(byte[] file, AstWriterOptions options, int channels, int sampleRate,
                                  int sampleCount, int firstBlockSize) {
    var header = file.AsSpan(0, 0x40);
    "STRM"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)(file.Length - 0x40)));
    BinaryPrimitives.WriteUInt16BigEndian(header[8..], (ushort)options.Codec);
    BinaryPrimitives.WriteUInt16BigEndian(header[10..], 16);
    BinaryPrimitives.WriteUInt16BigEndian(header[12..], checked((ushort)channels));
    BinaryPrimitives.WriteUInt16BigEndian(header[14..], options.Loop ? ushort.MaxValue : (ushort)0);
    BinaryPrimitives.WriteUInt32BigEndian(header[16..], checked((uint)sampleRate));
    BinaryPrimitives.WriteUInt32BigEndian(header[20..], checked((uint)sampleCount));
    BinaryPrimitives.WriteUInt32BigEndian(header[24..], checked((uint)(options.Loop ? options.LoopStart : 0)));
    BinaryPrimitives.WriteUInt32BigEndian(header[28..], checked((uint)(options.Loop ? options.LoopEnd ?? sampleCount : sampleCount)));
    BinaryPrimitives.WriteUInt32BigEndian(header[32..], checked((uint)firstBlockSize));
    header[0x28] = options.Volume;
  }
}
