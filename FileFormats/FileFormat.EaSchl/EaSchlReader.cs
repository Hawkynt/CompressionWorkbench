#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.EaXa;

namespace FileFormat.EaSchl;

/// <summary>
/// Parses an Electronic Arts SCHl audio stream. The stream is a chain of blocks, each
/// <c>4CC + u32 LE blockSize</c> where <c>blockSize</c> counts the 8-byte block header too:
/// <list type="bullet">
///   <item><c>SCHl</c> — header. Carries a PT (patch table) describing the audio: this
///         reader scans the block body for the PT marker byte <c>0xFD</c> and walks its TLV
///         entries — <c>0x82</c> channels, <c>0x84</c> sample rate, <c>0x85</c> total
///         samples, <c>0xA0</c> compression (<c>0x07</c>/<c>0x00</c> = EA-XA, <c>0x14</c> =
///         16-bit PCM). When the PT cannot be parsed it defaults to mono / 22050 Hz /
///         EA-XA.</item>
///   <item><c>SCCl</c> — block count (informational; skipped).</item>
///   <item><c>SCDl</c> — data. The body after the 8-byte header begins with a u32 LE sample
///         count, after which the bytes are channel-interleaved EA-XA frames (or interleaved
///         16-bit PCM for the PCM compression type).</item>
///   <item><c>SCEl</c> — end of stream.</item>
/// </list>
/// </summary>
public sealed class EaSchlReader {

  /// <summary>EA-XA / EA-ADPCM compression types as encoded by the PT <c>0xA0</c> field.</summary>
  public const int CompressionEaXa = 0x07;
  /// <summary>
  /// Defines the compression ea xa alt constant value.
  /// </summary>
  public const int CompressionEaXaAlt = 0x00;
  /// <summary>
  /// Defines the compression pcm 16 constant value.
  /// </summary>
  public const int CompressionPcm16 = 0x14;

  // PT TLV sub-codes.
  private const byte PtMarker = 0xFD;
  private const byte PtChannels = 0x82;
  private const byte PtSampleRate = 0x84;
  private const byte PtTotalSamples = 0x85;
  private const byte PtCompression = 0xA0;
  private const byte PtEnd = 0x8A;

  /// <summary>
  /// Gets or sets the channels.
  /// </summary>
  public int Channels { get; private set; } = 1;
  /// <summary>
  /// Gets or sets the sample rate.
  /// </summary>
  public int SampleRate { get; private set; } = 22050;
  /// <summary>
  /// Gets or sets the compression.
  /// </summary>
  public int Compression { get; private set; } = CompressionEaXa;
  /// <summary>
  /// Gets or sets the total samples.
  /// </summary>
  public long TotalSamples { get; private set; }

  /// <summary>Concatenated coded audio bytes from every SCDl block (header stripped).</summary>
  public byte[] CodedData { get; private set; } = [];

  /// <summary>
  /// Initializes a new instance of <see cref="EaSchlReader"/>.
  /// </summary>
  public EaSchlReader(ReadOnlySpan<byte> data) {
    if (data.Length < 8 || data[0] != 'S' || data[1] != 'C' || data[2] != 'H' || data[3] != 'l')
      throw new InvalidDataException("Missing SCHl magic.");

    var coded = new List<byte>();
    var pos = 0;
    var headerParsed = false;

    while (pos + 8 <= data.Length) {
      var tag = data.Slice(pos, 4);
      var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
      if (blockSize < 8 || pos + blockSize > data.Length)
        break; // malformed length: stop gracefully on what we have

      var body = data.Slice(pos + 8, blockSize - 8);

      if (tag.SequenceEqual("SCHl"u8)) {
        if (!headerParsed) {
          this.ParsePatchTable(body);
          headerParsed = true;
        }
      } else if (tag.SequenceEqual("SCDl"u8)) {
        // Body: u32 LE sample count, then coded audio bytes.
        if (body.Length >= 4)
          coded.AddRange(body[4..].ToArray());
      } else if (tag.SequenceEqual("SCEl"u8)) {
        break;
      }
      // SCCl and any other block kinds are skipped.

      pos += blockSize;
    }

    this.CodedData = coded.ToArray();
    if (this.Channels < 1) this.Channels = 1;
    if (this.SampleRate <= 0) this.SampleRate = 22050;
  }

  /// <summary>Decodes the carried audio into interleaved 16-bit PCM, or null if unsupported.</summary>
  public short[]? DecodeInterleaved() {
    if (this.CodedData.Length == 0)
      return [];
    switch (this.Compression) {
      case CompressionEaXa:
      case CompressionEaXaAlt:
        return EaXaCodec.Decode(this.CodedData, this.Channels);
      case CompressionPcm16: {
        var samples = new short[this.CodedData.Length / 2];
        for (var i = 0; i < samples.Length; ++i)
          samples[i] = BinaryPrimitives.ReadInt16LittleEndian(this.CodedData.AsSpan(i * 2));
        return samples;
      }
      default:
        return null;
    }
  }

  private void ParsePatchTable(ReadOnlySpan<byte> body) {
    var start = body.IndexOf(PtMarker);
    if (start < 0)
      return; // keep defaults

    var p = start + 1;
    while (p < body.Length) {
      var code = body[p++];
      if (code == PtEnd)
        break;

      // Each TLV is: code, u8 length, length value bytes (big-endian integer).
      if (p >= body.Length)
        break;
      var len = body[p++];
      if (len == 0 || p + len > body.Length)
        break;

      var value = ReadBigEndian(body.Slice(p, len));
      p += len;

      switch (code) {
        case PtChannels: this.Channels = (int)value; break;
        case PtSampleRate: this.SampleRate = (int)value; break;
        case PtTotalSamples: this.TotalSamples = value; break;
        case PtCompression: this.Compression = (int)value; break;
      }
    }
  }

  private static long ReadBigEndian(ReadOnlySpan<byte> bytes) {
    long value = 0;
    foreach (var b in bytes)
      value = (value << 8) | b;
    return value;
  }
}
