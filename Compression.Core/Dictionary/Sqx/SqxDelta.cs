namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// The adaptive delta filters SQX applies after multimedia blocks and, when a file asks for it,
/// after the LZ output: each value is predicted from earlier ones, and every so often the
/// predictor whose recent errors used the fewest distinct values takes over.
/// </summary>
internal static class SqxDelta {
  /// <summary>Adaptation period at the start of a filter (both formats).</summary>
  public const uint StandardFrequency = 0xFF;
  public const int EightBit = 0;
  public const int SixteenBit = 1;

  internal sealed class ByteFilter {
    private readonly uint[,] _freqs = new uint[6, 256];
    private readonly byte[] _samples = new byte[8];
    private uint _delta;
    private uint _updateFrequency;
    private uint _count;

    public ByteFilter(uint startDelta, uint updateFrequency) {
      this._delta = startDelta;
      this._updateFrequency = updateFrequency;
    }

    public byte Decode(byte c) {
      var cur = this._delta switch {
        0 => c,
        1 => (byte)(this._samples[0] - c),
        2 => (byte)(this._samples[1] - c),
        3 => (byte)(this._samples[2] - c),
        _ => (byte)(this._samples[3] - c),
      };

      ++this._freqs[0, cur];
      for (var i = 1; i <= 4; ++i)
        ++this._freqs[i, (byte)(this._samples[i - 1] - cur)];

      this._samples[3] = this._samples[2];
      this._samples[2] = this._samples[1];
      this._samples[1] = this._samples[0];
      this._samples[0] = cur;

      if ((this._count & this._updateFrequency) == this._updateFrequency) {
        uint smallest = uint.MaxValue, smallestIndex = 0;
        for (var i = 0u; i <= 4; ++i) {
          uint distinct = 0;
          for (var k = 0; k < 256; ++k)
            if (this._freqs[i, k] > 0)
              ++distinct;
          if (i == 0 || distinct < smallest) {
            smallest = distinct;
            smallestIndex = i;
          }
        }

        if (this._delta == smallestIndex) {
          this._updateFrequency += this._updateFrequency + 1;
          this._updateFrequency &= 4095;
        } else
          this._updateFrequency = 0x1FF;

        this._delta = smallestIndex;
        Array.Clear(this._freqs);
      }

      ++this._count;
      return cur;
    }
  }

  internal sealed class WordFilter {
    private readonly uint[,] _high = new uint[4, 256];
    private readonly uint[,] _low = new uint[4, 256];
    private readonly ushort[] _samples = new ushort[8];
    private uint _delta;
    private readonly uint _updateFrequency;
    private uint _count;

    public WordFilter(uint startDelta, uint updateFrequency) {
      this._delta = startDelta;
      this._updateFrequency = updateFrequency;
    }

    public ushort Decode(ushort w) {
      var cur = this._delta switch {
        0 => (ushort)(this._samples[0] - w),
        1 => (ushort)(this._samples[1] - w),
        2 => (ushort)(this._samples[2] - w),
        _ => (ushort)0,
      };

      for (var i = 0; i <= 2; ++i) {
        var err = (ushort)(this._samples[i] - cur);
        ++this._high[i, err >> 8];
        ++this._low[i, err & 0xFF];
      }

      this._samples[2] = this._samples[1];
      this._samples[1] = this._samples[0];
      this._samples[0] = cur;

      if ((this._count & this._updateFrequency) == this._updateFrequency) {
        uint smallest = uint.MaxValue, smallestIndex = 0;
        for (var i = 0u; i <= 2; ++i) {
          uint distinct = 0;
          for (var k = 0; k < 256; ++k) {
            if (this._high[i, k] > 0)
              ++distinct;
            if (this._low[i, k] > 0)
              ++distinct;
          }

          if (i == 0 || distinct < smallest) {
            smallest = distinct;
            smallestIndex = i;
          }
        }

        this._delta = smallestIndex;
        Array.Clear(this._high);
        Array.Clear(this._low);
      }

      ++this._count;
      return cur;
    }
  }

  /// <summary>Byte-interleaved 8-bit filters over <paramref name="buffer"/>; a trailing partial round is left alone.</summary>
  public static void Decode(ByteFilter[] filters, int channels, Span<byte> buffer) {
    var max = buffer.Length / channels * channels;
    for (int count = 0, index = 0; count < max; ++count) {
      buffer[count] = filters[index].Decode(buffer[count]);
      if (++index == channels)
        index = 0;
    }
  }

  /// <summary>16-bit little-endian filter over <paramref name="buffer"/>; a trailing odd byte is left alone.</summary>
  public static void Decode(WordFilter filter, Span<byte> buffer) {
    for (var i = 0; i + 1 < buffer.Length; i += 2) {
      var value = filter.Decode((ushort)(buffer[i] | buffer[i + 1] << 8));
      buffer[i] = (byte)value;
      buffer[i + 1] = (byte)(value >> 8);
    }
  }

  /// <summary>The filter after a multimedia block: fresh state, the block header's start predictor and width.</summary>
  public static void DecodeBlock(uint startDelta, uint coderType, uint channels, Span<byte> buffer) {
    if (channels is 0 or > 4)
      return;
    if (coderType == SixteenBit) {
      Decode(new WordFilter(startDelta, StandardFrequency), buffer);
      return;
    }

    var filters = new ByteFilter[channels];
    for (var i = 0; i < channels; ++i)
      filters[i] = new ByteFilter(startDelta, StandardFrequency);
    Decode(filters, (int)channels, buffer);
  }
}
