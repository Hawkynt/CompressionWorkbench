#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.MacSnd;

/// <summary>
/// Parser for the classic Mac OS <c>'snd '</c> sampled-sound resource carried as a
/// data fork. All fields are big-endian. Two resource formats exist:
/// <list type="bullet">
///   <item><b>Format 1</b> — <c>u16 format(1)</c>, <c>u16 nDataFormats</c>, then per
///     data format a <c>u16 id</c> + <c>u32 initOption</c>, then <c>u16 nCommands</c>
///     and that many 8-byte commands (<c>u16 cmd | u16 param1 | u32 param2</c>). The
///     sampled data is reached through a <c>bufferCmd</c> (0x8051) or <c>soundCmd</c>
///     (0x8050) whose <c>param2</c> is the offset (from the start of the resource) of a
///     <c>SoundHeader</c>.</item>
///   <item><b>Format 2</b> — <c>u16 format(2)</c>, <c>u16 refCount</c>, then
///     <c>u16 nCommands</c> and the commands, same as format 1 from there on.</item>
/// </list>
/// The <c>SoundHeader</c> (big-endian) is:
/// <c>u32 samplePtr | u32 length | u32 sampleRate(16.16 fixed) | u32 loopStart |
/// u32 loopEnd | u8 encode | u8 baseFrequency</c> followed by the data. The
/// <c>encode</c> byte selects the variant: <c>0x00</c> standard 8-bit unsigned PCM,
/// <c>0xFF</c> extended (numChannels/numFrames/sampleSize), <c>0xFE</c> compressed
/// (a <c>compressionID</c> selecting MACE 3:1 / 6:1).
/// </summary>
public sealed class MacSndReader {

  /// <summary>
  /// Defines the standard header constant value.
  /// </summary>
  public const byte StandardHeader = 0x00;
  /// <summary>
  /// Defines the extended header constant value.
  /// </summary>
  public const byte ExtendedHeader = 0xFF;
  /// <summary>
  /// Defines the compressed header constant value.
  /// </summary>
  public const byte CompressedHeader = 0xFE;

  // Sound Manager command numbers (the high bit flags a pointer/handle parameter).
  private const ushort SoundCmd = 0x8050;
  private const ushort BufferCmd = 0x8051;

  // compressionID values used by the compressed header.
  /// <summary>
  /// Defines the compression mace 3 constant value.
  /// </summary>
  public const short CompressionMace3 = 3;
  /// <summary>
  /// Defines the compression mace 6 constant value.
  /// </summary>
  public const short CompressionMace6 = 4;
  /// <summary>
  /// Defines the compression not compressed constant value.
  /// </summary>
  public const short CompressionNotCompressed = 0; // also -1/-2 in the wild

  /// <summary>
  /// Represents a parsed snd.
  /// </summary>
  public sealed record ParsedSnd(
    int Format,
    byte Encode,
    int NumChannels,
    int BitsPerSample,
    int SampleRate,
    uint NumFrames,
    short CompressionId,
    byte[] SampleData);

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  public ParsedSnd Read(ReadOnlySpan<byte> data) {
    if (data.Length < 6)
      throw new InvalidDataException("'snd ' resource too short for a header.");

    var format = BinaryPrimitives.ReadUInt16BigEndian(data);
    int commandsPos;
    if (format == 1) {
      var nDataFormats = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
      // each data format = u16 id + u32 initOption = 6 bytes
      commandsPos = 4 + nDataFormats * 6;
    } else if (format == 2) {
      // u16 format | u16 refCount | u16 nCommands | commands…
      commandsPos = 4;
    } else {
      throw new InvalidDataException($"Unsupported 'snd ' resource format {format}.");
    }

    if (commandsPos + 2 > data.Length)
      throw new InvalidDataException("'snd ' resource truncated before command list.");

    var nCommands = BinaryPrimitives.ReadUInt16BigEndian(data[commandsPos..]);
    var cmdPos = commandsPos + 2;

    // Walk the command list for a bufferCmd / soundCmd carrying the SoundHeader offset.
    var headerOffset = -1;
    for (var i = 0; i < nCommands; ++i) {
      if (cmdPos + 8 > data.Length)
        throw new InvalidDataException("'snd ' resource truncated inside command list.");
      var cmd = BinaryPrimitives.ReadUInt16BigEndian(data[cmdPos..]);
      var param2 = BinaryPrimitives.ReadUInt32BigEndian(data[(cmdPos + 4)..]);
      if (cmd is BufferCmd or SoundCmd) {
        headerOffset = (int)param2;
        break;
      }
      cmdPos += 8;
    }

    if (headerOffset < 0 || headerOffset + 22 > data.Length)
      throw new InvalidDataException("'snd ' resource has no usable bufferCmd/soundCmd SoundHeader.");

    return ParseSoundHeader(data, headerOffset, format);
  }

  private static ParsedSnd ParseSoundHeader(ReadOnlySpan<byte> data, int off, int format) {
    // SoundHeader common prefix (22 bytes through baseFrequency).
    // off+0  u32 samplePtr (0 → data follows inline)
    var length = BinaryPrimitives.ReadUInt32BigEndian(data[(off + 4)..]);
    var rateFixed = BinaryPrimitives.ReadUInt32BigEndian(data[(off + 8)..]);
    // off+12 loopStart, off+16 loopEnd
    var encode = data[off + 20];
    // off+21 baseFrequency
    var sampleRate = Fixed1616ToInt(rateFixed);

    switch (encode) {
      case StandardHeader: {
        var dataStart = off + 22;
        var count = ClampLength(length, data.Length - dataStart);
        var sampleData = data.Slice(dataStart, count).ToArray();
        return new ParsedSnd(format, encode, NumChannels: 1, BitsPerSample: 8,
          sampleRate, NumFrames: length, CompressionId: 0, sampleData);
      }

      case ExtendedHeader: {
        // ExtSoundHeader: after the 22-byte prefix:
        //   u32 numChannels | (rate already read at +8 is the 16.16 fixed rate)
        // Layout used here (the documented ExtSoundHeader): the 22-byte prefix's
        // sampleRate field doubles as the rate; numChannels lives at +4 of the
        // *common* header in the extended/compressed variants. We follow the IM:Sound
        // layout where numChannels replaces the "length" slot:
        var numChannels = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(off + 4)..]);
        if (numChannels is < 1 or > 2) numChannels = 1;
        var numFrames = BinaryPrimitives.ReadUInt32BigEndian(data[(off + 22)..]);
        // off+26 .. +35: 10-byte AIFF (80-bit extended) sample rate — rate already taken from 16.16.
        // off+36 markerChunk u32, +40 instrumentChunks u32, +44 AESRecording u32,
        // off+48 sampleSize u16, +50 futureUse u16, +52 futureUse u32, +56..+63 futureUse.
        var sampleSize = BinaryPrimitives.ReadUInt16BigEndian(data[(off + 48)..]);
        var bits = sampleSize == 16 ? 16 : 8;
        var dataStart = off + 64;
        var bytesPerFrame = numChannels * (bits / 8);
        var avail = data.Length - dataStart;
        var want = (int)Math.Min((long)numFrames * bytesPerFrame, avail < 0 ? 0 : avail);
        var sampleData = want > 0 ? data.Slice(dataStart, want).ToArray() : [];
        return new ParsedSnd(format, encode, numChannels, bits, sampleRate, numFrames,
          CompressionId: 0, sampleData);
      }

      case CompressedHeader: {
        // CmpSoundHeader: numChannels at +4 (replacing length); compressionID at +56 (u16).
        var numChannels = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(off + 4)..]);
        if (numChannels is < 1 or > 2) numChannels = 1;
        var numFrames = BinaryPrimitives.ReadUInt32BigEndian(data[(off + 22)..]);
        // off+26 AIFF rate(10), +36 markerChunk u32, +40 format(OSType) u32,
        // +44 futureUse2 u32, +48 stateVars u32, +52 leftOverSamples u32,
        // +56 compressionID i16, +58 packetSize u16, +60 snthID u16, +62 sampleSize u16.
        var compressionId = BinaryPrimitives.ReadInt16BigEndian(data[(off + 56)..]);
        var sampleSize = BinaryPrimitives.ReadUInt16BigEndian(data[(off + 62)..]);
        var dataStart = off + 64;
        var sampleData = dataStart <= data.Length ? data[dataStart..].ToArray() : [];
        var bits = sampleSize == 16 ? 16 : 8;
        return new ParsedSnd(format, encode, numChannels, bits, sampleRate, numFrames,
          compressionId, sampleData);
      }

      default:
        throw new InvalidDataException($"Unsupported 'snd ' encode byte 0x{encode:X2}.");
    }
  }

  /// <summary>Mac 16.16 unsigned fixed-point sample rate → integer hertz.</summary>
  public static int Fixed1616ToInt(uint fixed1616) => (int)(fixed1616 >> 16);

  private static int ClampLength(uint requested, int available) {
    if (available < 0) return 0;
    return requested > (uint)available ? available : (int)requested;
  }
}
