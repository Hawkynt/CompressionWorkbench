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

  private const int FmtChunkSize = 52;
  private const int DataChunkHeaderSize = 12;

  /// <summary>The DSF specification's channel range (channelNum 1..6).</summary>
  private const int MaxSpecChannels = 6;

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  /// <remarks>
  /// Every size and count in the header is checked against the bytes present before anything is
  /// allocated from it: a channel buffer holds at most the bytes its blocks carry, however many
  /// samples the header claims, and a channel count beyond the specification's six is accepted
  /// only when the data carries a block for each channel.
  /// </remarks>
  /// <exception cref="InvalidDataException">A chunk is missing or a size or count is out of range.</exception>
  public ParsedDsf Read(ReadOnlySpan<byte> data) {
    if (data.Length < 28 || !data[..4].SequenceEqual("DSD "u8))
      throw new InvalidDataException("Missing 'DSD ' magic.");

    var dsdChunkSize = BinaryPrimitives.ReadUInt64LittleEndian(data[4..]);
    if (dsdChunkSize < 28)
      throw new InvalidDataException("DSF 'DSD ' chunk too small.");
    var metadataPointer = BinaryPrimitives.ReadUInt64LittleEndian(data[20..]);

    if (dsdChunkSize > (ulong)Math.Max(0, data.Length - FmtChunkSize))
      throw new InvalidDataException("Missing 'fmt ' chunk.");
    var fmtOffset = (int)dsdChunkSize;
    if (!data.Slice(fmtOffset, 4).SequenceEqual("fmt "u8))
      throw new InvalidDataException("Missing 'fmt ' chunk.");

    var fmt = data.Slice(fmtOffset);
    var fmtSize = BinaryPrimitives.ReadUInt64LittleEndian(fmt[4..]);
    var channelType = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[20..]);
    var channelNum = BinaryPrimitives.ReadUInt32LittleEndian(fmt[24..]);
    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[28..]);
    var bitsPerSample = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[32..]);
    var sampleCount = BinaryPrimitives.ReadUInt64LittleEndian(fmt[36..]);
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(fmt[44..]);

    if (sampleRate is 0 or > int.MaxValue)
      throw new InvalidDataException($"DSF samplingFrequency {sampleRate} is out of range.");
    if (blockSize is 0 or > int.MaxValue)
      throw new InvalidDataException($"DSF blockSizePerChannel {blockSize} must be positive.");

    if (fmtSize < FmtChunkSize || fmtSize > (ulong)(fmt.Length - DataChunkHeaderSize))
      throw new InvalidDataException("Missing 'data' chunk.");
    var dataOffset = fmtOffset + (int)fmtSize;
    if (!data.Slice(dataOffset, 4).SequenceEqual("data"u8))
      throw new InvalidDataException("Missing 'data' chunk.");

    var dataChunkSize = BinaryPrimitives.ReadUInt64LittleEndian(data[(dataOffset + 4)..]);
    var payloadStart = dataOffset + DataChunkHeaderSize;
    if (dataChunkSize < DataChunkHeaderSize || dataChunkSize - DataChunkHeaderSize > (ulong)(data.Length - payloadStart))
      throw new InvalidDataException("DSF 'data' chunk truncated.");
    var payload = data.Slice(payloadStart, (int)(dataChunkSize - DataChunkHeaderSize));

    var block = (int)blockSize;
    var blocks = (payload.Length + (long)block - 1) / block;
    if (channelNum < 1 || channelNum > Math.Max(MaxSpecChannels, blocks))
      throw new InvalidDataException($"DSF channelNum {channelNum} is out of range.");
    var channelCount = (int)channelNum;

    // Bytes the blocks carry for each channel; blocks are interleaved round-robin.
    var present = new long[channelCount];
    for (long pos = 0, ch = 0; pos < payload.Length; pos += block, ch = (ch + 1) % channelCount)
      present[ch] += Math.Min(block, payload.Length - pos);

    // De-interleave into one contiguous DSD buffer per channel, holding the significant bytes
    // (sampleCount bits) the data actually carries for it.
    var significant = sampleCount / 8 + (sampleCount % 8 == 0 ? 0UL : 1UL);
    var channels = new byte[channelCount][];
    for (var c = 0; c < channelCount; ++c)
      channels[c] = new byte[(int)Math.Min(significant, (ulong)present[c])];

    var writers = new int[channelCount];
    for (long pos = 0, ch = 0; pos < payload.Length; pos += block, ch = (ch + 1) % channelCount) {
      var take = (int)Math.Min(block, payload.Length - pos);
      var w = writers[ch];
      var copy = Math.Min(take, channels[ch].Length - w);
      if (copy > 0) {
        payload.Slice((int)pos, copy).CopyTo(channels[ch].AsSpan(w));
        writers[ch] = w + copy;
      }
    }

    byte[]? id3 = null;
    if (metadataPointer > 0 && metadataPointer < (ulong)data.Length)
      id3 = data[(int)metadataPointer..].ToArray();

    return new ParsedDsf(channelType, channelCount, (int)sampleRate, bitsPerSample,
      (long)Math.Min(sampleCount, long.MaxValue), block, channels, id3);
  }
}
