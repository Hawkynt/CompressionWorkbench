#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Dsf;

/// <summary>
/// Sony DSD Stream File (<c>.dsf</c>) parser. All integers are little-endian and chunk/file
/// sizes are unsigned 64-bit. Layout:
/// <list type="bullet">
///   <item><c>DSD&#160;</c> header chunk (28 bytes): magic | u64 chunkSize(=28) |
///         u64 totalFileSize | u64 metadataPointer (0, or the file offset of a trailing
///         ID3v2 tag).</item>
///   <item><c>fmt&#160;</c> chunk (52 bytes): magic | u64 size(=52) | u32 formatVersion(=1) |
///         u32 formatId(=0 raw DSD) | u32 channelType | u32 channelNum | u32 samplingFrequency |
///         u32 bitsPerSample (1 or 8) | u64 sampleCount (per channel, in bits) |
///         u32 blockSizePerChannel(=4096) | u32 reserved.</item>
///   <item><c>data</c> chunk: magic | u64 size | payload of per-channel blocks interleaved
///         round-robin (blockSize bytes ch0, blockSize bytes ch1, …, repeating). For
///         <c>bitsPerSample==1</c> the bits within a byte are LSB-first; for <c>==8</c> they are
///         treated MSB-first. Only <c>sampleCount</c> bits per channel are significant.</item>
/// </list>
/// </summary>
public sealed class DsfReader {

  /// <summary>
  /// Represents a parsed dsf.
  /// </summary>
  public sealed record ParsedDsf(
    int ChannelType,
    int ChannelNum,
    int SampleRate,
    int BitsPerSample,
    long SampleCount,
    int BlockSize,
    byte[][] ChannelDsd,
    byte[]? Id3);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedDsf Read(ReadOnlySpan<byte> data) {
    if (data.Length < 28 || !data[..4].SequenceEqual("DSD "u8))
      throw new InvalidDataException("Missing 'DSD ' magic.");

    var dsdChunkSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[4..]);
    if (dsdChunkSize < 28)
      throw new InvalidDataException("DSF 'DSD ' chunk too small.");
    var metadataPointer = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[20..]);

    var fmtOffset = (int)dsdChunkSize;
    if (fmtOffset + 52 > data.Length || !data.Slice(fmtOffset, 4).SequenceEqual("fmt "u8))
      throw new InvalidDataException("Missing 'fmt ' chunk.");

    var fmt = data.Slice(fmtOffset);
    var fmtSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(fmt[4..]);
    var channelType = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[20..]);
    var channelNum = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[24..]);
    var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[28..]);
    var bitsPerSample = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[32..]);
    var sampleCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(fmt[36..]);
    var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[44..]);

    if (channelNum < 1)
      throw new InvalidDataException("DSF channelNum must be at least 1.");
    if (blockSize <= 0)
      throw new InvalidDataException("DSF blockSizePerChannel must be positive.");

    var dataOffset = (int)(fmtOffset + fmtSize);
    if (dataOffset + 12 > data.Length || !data.Slice(dataOffset, 4).SequenceEqual("data"u8))
      throw new InvalidDataException("Missing 'data' chunk.");

    var dataChunkSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(dataOffset + 4));
    var payloadStart = dataOffset + 12;
    var payloadLen = (int)(dataChunkSize - 12);
    if (payloadLen < 0 || payloadStart + payloadLen > data.Length)
      throw new InvalidDataException("DSF 'data' chunk truncated.");

    var payload = data.Slice(payloadStart, payloadLen);

    // De-interleave the block round-robin into one contiguous DSD buffer per channel.
    var channelBytes = (sampleCount + 7) / 8; // significant bytes per channel
    var channels = new byte[channelNum][];
    var writers = new int[channelNum];
    for (var c = 0; c < channelNum; ++c)
      channels[c] = new byte[channelBytes];

    var pos = 0;
    var ch = 0;
    while (pos < payload.Length) {
      var take = Math.Min(blockSize, payload.Length - pos);
      var w = writers[ch];
      var copy = (int)Math.Min(take, channelBytes - w);
      if (copy > 0) {
        payload.Slice(pos, copy).CopyTo(channels[ch].AsSpan(w));
        writers[ch] = w + copy;
      }
      pos += blockSize;
      ch = (ch + 1) % channelNum;
    }

    byte[]? id3 = null;
    if (metadataPointer > 0 && metadataPointer < data.Length)
      id3 = data.Slice((int)metadataPointer).ToArray();

    return new ParsedDsf(channelType, channelNum, sampleRate, bitsPerSample, sampleCount, blockSize, channels, id3);
  }
}
