namespace Codec.Gsm610;

/// <summary>
/// Microsoft GSM 6.10 / WAV49 framing used by WAVE format tag <c>0x0031</c>.
/// A WAV49 block stores two ordinary GSM 06.10 frames in 65 bytes: the 76 coded
/// parameters from each frame are written field-by-field least-significant-bit first,
/// without the raw <c>.gsm</c> frame's leading <c>0xD</c> marker nibble.
/// </summary>
public static class Gsm610Wav49 {
  /// <summary>Encoded size of one Microsoft GSM block.</summary>
  public const int BlockBytes = 65;

  /// <summary>Decoded PCM sample count represented by one Microsoft GSM block.</summary>
  public const int SamplesPerBlock = Gsm610Codec.FrameSamples * 2;

  private static readonly byte[] ParameterWidths = CreateParameterWidths();

  /// <summary>
  /// Re-packs an even number of ordinary 33-byte GSM frames into 65-byte WAV49 blocks.
  /// </summary>
  public static byte[] PackRawFrames(ReadOnlySpan<byte> rawFrames) {
    if (rawFrames.Length % Gsm610Codec.FrameBytes != 0)
      throw new ArgumentException("Raw GSM input must contain whole 33-byte frames.", nameof(rawFrames));

    var frameCount = rawFrames.Length / Gsm610Codec.FrameBytes;
    if ((frameCount & 1) != 0)
      throw new ArgumentException("WAV49 packs GSM frames in pairs.", nameof(rawFrames));

    var result = new byte[frameCount / 2 * BlockBytes];
    for (var pair = 0; pair < frameCount / 2; ++pair) {
      var writer = new LsbBitWriter(result.AsSpan(pair * BlockBytes, BlockBytes));
      PackFrame(rawFrames.Slice(pair * 2 * Gsm610Codec.FrameBytes, Gsm610Codec.FrameBytes), ref writer);
      PackFrame(rawFrames.Slice((pair * 2 + 1) * Gsm610Codec.FrameBytes, Gsm610Codec.FrameBytes), ref writer);
      if (writer.BitsWritten != BlockBytes * 8)
        throw new InvalidOperationException($"WAV49 block writer produced {writer.BitsWritten} bits instead of {BlockBytes * 8}.");
    }
    return result;
  }

  /// <summary>
  /// Expands 65-byte WAV49 blocks into ordinary marker-prefixed 33-byte GSM frames.
  /// </summary>
  public static byte[] UnpackToRawFrames(ReadOnlySpan<byte> wav49) {
    if (wav49.Length % BlockBytes != 0)
      throw new ArgumentException("WAV49 input must contain whole 65-byte blocks.", nameof(wav49));

    var blockCount = wav49.Length / BlockBytes;
    var result = new byte[blockCount * 2 * Gsm610Codec.FrameBytes];
    for (var block = 0; block < blockCount; ++block) {
      var reader = new LsbBitReader(wav49.Slice(block * BlockBytes, BlockBytes));
      UnpackFrame(ref reader, result.AsSpan(block * 2 * Gsm610Codec.FrameBytes, Gsm610Codec.FrameBytes));
      UnpackFrame(ref reader, result.AsSpan((block * 2 + 1) * Gsm610Codec.FrameBytes, Gsm610Codec.FrameBytes));
    }
    return result;
  }

  /// <summary>
  /// Encodes mono 8 kHz PCM16 into Microsoft GSM/WAV49 blocks. An incomplete final
  /// 160-sample GSM frame is padded by the codec encoder; an unpaired final frame is
  /// followed by a valid silence frame so every WAVE block remains 65 bytes.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    if (pcm.IsEmpty)
      return [];

    var raw = Gsm610Codec.EncodeRaw(pcm);
    var frameCount = raw.Length / Gsm610Codec.FrameBytes;
    if ((frameCount & 1) == 0)
      return PackRawFrames(raw);

    var padded = new byte[raw.Length + Gsm610Codec.FrameBytes];
    raw.AsSpan().CopyTo(padded);
    var silence = Gsm610Codec.EncodeRaw(new short[Gsm610Codec.FrameSamples], padFinalFrame: false);
    silence.AsSpan().CopyTo(padded.AsSpan(raw.Length));
    return PackRawFrames(padded);
  }

  /// <summary>Decodes Microsoft GSM/WAV49 blocks to mono 8 kHz PCM16.</summary>
  public static short[] Decode(ReadOnlySpan<byte> wav49)
    => Gsm610Codec.DecodeRaw(UnpackToRawFrames(wav49));

  private static void PackFrame(ReadOnlySpan<byte> rawFrame, ref LsbBitWriter writer) {
    if (rawFrame.Length != Gsm610Codec.FrameBytes)
      throw new ArgumentException("A raw GSM frame must contain exactly 33 bytes.", nameof(rawFrame));
    if ((rawFrame[0] >> 4) != 0xD)
      throw new InvalidDataException("Raw GSM frame is missing the 0xD marker nibble.");

    var reader = new MsbBitReader(rawFrame);
    if (reader.Read(4) != 0xD)
      throw new InvalidDataException("Raw GSM frame is missing the 0xD marker nibble.");
    foreach (var width in ParameterWidths)
      writer.Write(reader.Read(width), width);
  }

  private static void UnpackFrame(ref LsbBitReader reader, Span<byte> rawFrame) {
    rawFrame.Clear();
    var writer = new MsbBitWriter(rawFrame);
    writer.Write(0xD, 4);
    foreach (var width in ParameterWidths)
      writer.Write(reader.Read(width), width);
    if (writer.BitsWritten != Gsm610Codec.FrameBytes * 8)
      throw new InvalidOperationException($"Raw GSM frame writer produced {writer.BitsWritten} bits instead of 264.");
  }

  private static byte[] CreateParameterWidths() {
    var widths = new List<byte>(76) { 6, 6, 5, 5, 4, 4, 3, 3 };
    for (var subframe = 0; subframe < 4; ++subframe) {
      widths.Add(7);
      widths.Add(2);
      widths.Add(2);
      widths.Add(6);
      for (var pulse = 0; pulse < 13; ++pulse)
        widths.Add(3);
    }
    return [.. widths];
  }

  private ref struct MsbBitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bitPosition;

    public int Read(int bitCount) {
      var value = 0;
      for (var bit = 0; bit < bitCount; ++bit) {
        if (this._bitPosition >= this._data.Length * 8)
          throw new InvalidDataException("Truncated raw GSM frame.");
        value = (value << 1) | ((this._data[this._bitPosition >> 3] >> (7 - (this._bitPosition & 7))) & 1);
        ++this._bitPosition;
      }
      return value;
    }
  }

  private ref struct LsbBitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bitPosition;

    public int Read(int bitCount) {
      var value = 0;
      for (var bit = 0; bit < bitCount; ++bit) {
        if (this._bitPosition >= this._data.Length * 8)
          throw new InvalidDataException("Truncated WAV49 block.");
        value |= ((this._data[this._bitPosition >> 3] >> (this._bitPosition & 7)) & 1) << bit;
        ++this._bitPosition;
      }
      return value;
    }
  }

  private ref struct MsbBitWriter(Span<byte> data) {
    private readonly Span<byte> _data = data;
    private int _bitPosition;
    public readonly int BitsWritten => this._bitPosition;

    public void Write(int value, int bitCount) {
      for (var bit = bitCount - 1; bit >= 0; --bit) {
        if (this._bitPosition >= this._data.Length * 8)
          throw new InvalidOperationException("Raw GSM frame bit writer overflow.");
        if (((value >> bit) & 1) != 0)
          this._data[this._bitPosition >> 3] |= (byte)(1 << (7 - (this._bitPosition & 7)));
        ++this._bitPosition;
      }
    }
  }

  private ref struct LsbBitWriter(Span<byte> data) {
    private readonly Span<byte> _data = data;
    private int _bitPosition;
    public readonly int BitsWritten => this._bitPosition;

    public void Write(int value, int bitCount) {
      for (var bit = 0; bit < bitCount; ++bit) {
        if (this._bitPosition >= this._data.Length * 8)
          throw new InvalidOperationException("WAV49 bit writer overflow.");
        if (((value >> bit) & 1) != 0)
          this._data[this._bitPosition >> 3] |= (byte)(1 << (this._bitPosition & 7));
        ++this._bitPosition;
      }
    }
  }
}
