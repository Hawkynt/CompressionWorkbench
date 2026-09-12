using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.Lzfse;

/// <summary>
/// LZFSE's fixed-state FSE/tANS primitive.
/// </summary>
/// <remarks>
/// The state transition equations and stream layout follow Apple's public LZFSE
/// reference implementation (BSD-3-Clause), specifically <c>lzfse_fse.[ch]</c>.
/// This is a managed implementation of the published state-machine behaviour;
/// it does not depend on native code.
/// </remarks>
internal static class LzfseFse {
  internal readonly record struct DecoderEntry(byte BitCount, byte Symbol, short Delta);
  internal readonly record struct ValueDecoderEntry(byte TotalBits, byte ValueBits, short Delta, int BaseValue);
  internal readonly record struct EncoderEntry(short Threshold, byte BitCount, short Delta0, short Delta1);

  internal static ushort[] Normalize(ReadOnlySpan<int> counts, int stateCount) {
    if (!BitOperations.IsPow2((uint)stateCount))
      throw new ArgumentOutOfRangeException(nameof(stateCount), "FSE state count must be a power of two.");

    long totalLong = 0;
    foreach (var count in counts) {
      if (count < 0)
        throw new ArgumentOutOfRangeException(nameof(counts), "FSE frequencies cannot be negative.");
      totalLong += count;
    }

    if (totalLong is <= 0 or > uint.MaxValue)
      throw new ArgumentException("FSE requires a non-empty histogram whose sum fits UInt32.", nameof(counts));

    var total = (uint)totalLong;
    var result = new ushort[counts.Length];
    var remaining = stateCount;
    var largestFrequency = 0;
    var largestSymbol = -1;
    var shift = BitOperations.LeadingZeroCount((uint)stateCount) - 1;
    var highPrecisionStep = (1u << 31) / total;

    for (var symbol = 0; symbol < counts.Length; ++symbol) {
      var count = counts[symbol];
      if (count == 0)
        continue;

      var scaled = unchecked((uint)count * highPrecisionStep);
      var frequency = (int)(((scaled >> shift) + 1) >> 1);
      frequency = Math.Max(frequency, 1);
      result[symbol] = checked((ushort)frequency);
      remaining -= frequency;

      if (frequency > largestFrequency) {
        largestFrequency = frequency;
        largestSymbol = symbol;
      }
    }

    if (largestSymbol < 0)
      throw new InvalidOperationException("FSE histogram unexpectedly contains no symbols.");

    if (-remaining < (largestFrequency >> 2)) {
      var adjusted = result[largestSymbol] + remaining;
      if (adjusted <= 0)
        throw new InvalidOperationException("FSE normalization produced a non-positive frequency.");
      result[largestSymbol] = checked((ushort)adjusted);
    } else {
      while (remaining != 0) {
        var changed = false;
        for (var adjustmentShift = 3; adjustmentShift >= 0 && remaining != 0; --adjustmentShift) {
          for (var symbol = 0; symbol < result.Length && remaining != 0; ++symbol) {
            if (result[symbol] <= 1)
              continue;

            var amount = (result[symbol] - 1) >> adjustmentShift;
            if (amount > -remaining)
              amount = -remaining;
            if (amount <= 0)
              continue;

            result[symbol] -= checked((ushort)amount);
            remaining += amount;
            changed = true;
          }
        }

        if (!changed)
          throw new InvalidOperationException("FSE normalization could not fit the requested state count.");
      }
    }

    var sum = 0;
    foreach (var frequency in result)
      sum += frequency;
    if (sum != stateCount)
      throw new InvalidOperationException($"FSE normalized frequency sum is {sum}, expected {stateCount}.");

    return result;
  }

  internal static DecoderEntry[] BuildDecoderTable(ReadOnlySpan<ushort> frequencies, int stateCount) {
    var table = new DecoderEntry[stateCount];
    var stateLeadingZeros = BitOperations.LeadingZeroCount((uint)stateCount);
    var offset = 0;

    for (var symbol = 0; symbol < frequencies.Length; ++symbol) {
      var frequency = frequencies[symbol];
      if (frequency == 0)
        continue;

      if (offset + frequency > stateCount)
        throw new InvalidDataException("LZFSE FSE frequency table exceeds its state space.");

      var k = BitOperations.LeadingZeroCount((uint)frequency) - stateLeadingZeros;
      var split = (2 * stateCount >> k) - frequency;
      for (var j = 0; j < frequency; ++j) {
        int bitCount;
        int delta;
        if (j < split) {
          bitCount = k;
          delta = ((frequency + j) << k) - stateCount;
        } else {
          bitCount = k - 1;
          delta = (j - split) << (k - 1);
        }

        table[offset + j] = new(checked((byte)bitCount), checked((byte)symbol), checked((short)delta));
      }

      offset += frequency;
    }

    if (offset != stateCount)
      throw new InvalidDataException($"LZFSE FSE table contains {offset} states, expected {stateCount}.");

    return table;
  }

  internal static ValueDecoderEntry[] BuildValueDecoderTable(
      ReadOnlySpan<ushort> frequencies,
      int stateCount,
      ReadOnlySpan<byte> extraBits,
      ReadOnlySpan<int> baseValues) {
    if (frequencies.Length != extraBits.Length || frequencies.Length != baseValues.Length)
      throw new ArgumentException("FSE value-table arrays must have the same length.");

    var table = new ValueDecoderEntry[stateCount];
    var stateLeadingZeros = BitOperations.LeadingZeroCount((uint)stateCount);
    var offset = 0;

    for (var symbol = 0; symbol < frequencies.Length; ++symbol) {
      var frequency = frequencies[symbol];
      if (frequency == 0)
        continue;
      if (offset + frequency > stateCount)
        throw new InvalidDataException("LZFSE FSE value frequency table exceeds its state space.");

      var k = BitOperations.LeadingZeroCount((uint)frequency) - stateLeadingZeros;
      var split = (2 * stateCount >> k) - frequency;
      var valueBits = extraBits[symbol];
      for (var j = 0; j < frequency; ++j) {
        int stateBits;
        int delta;
        if (j < split) {
          stateBits = k;
          delta = ((frequency + j) << k) - stateCount;
        } else {
          stateBits = k - 1;
          delta = (j - split) << (k - 1);
        }

        table[offset + j] = new(
          checked((byte)(stateBits + valueBits)),
          valueBits,
          checked((short)delta),
          baseValues[symbol]);
      }

      offset += frequency;
    }

    if (offset != stateCount)
      throw new InvalidDataException($"LZFSE FSE value table contains {offset} states, expected {stateCount}.");

    return table;
  }

  internal static EncoderEntry[] BuildEncoderTable(ReadOnlySpan<ushort> frequencies, int stateCount) {
    var table = new EncoderEntry[frequencies.Length];
    var stateLeadingZeros = BitOperations.LeadingZeroCount((uint)stateCount);
    var offset = 0;

    for (var symbol = 0; symbol < frequencies.Length; ++symbol) {
      var frequency = frequencies[symbol];
      if (frequency == 0)
        continue;

      var k = BitOperations.LeadingZeroCount((uint)frequency) - stateLeadingZeros;
      var threshold = (frequency << k) - stateCount;
      var delta0 = offset - frequency + (stateCount >> k);
      var delta1 = k == 0 ? 0 : offset - frequency + (stateCount >> (k - 1));
      table[symbol] = new(
        checked((short)threshold),
        checked((byte)k),
        checked((short)delta0),
        checked((short)delta1));
      offset += frequency;
    }

    if (offset != stateCount)
      throw new InvalidDataException($"LZFSE FSE encoder table contains {offset} states, expected {stateCount}.");

    return table;
  }

  internal static byte Decode(ref ushort state, ReadOnlySpan<DecoderEntry> table, ref InputBitStream input) {
    if (state >= table.Length)
      throw new InvalidDataException("LZFSE FSE decoder state is out of range.");
    var entry = table[state];
    var bits = input.Pull(entry.BitCount);
    state = checked((ushort)(entry.Delta + (int)bits));
    return entry.Symbol;
  }

  internal static int DecodeValue(ref ushort state, ReadOnlySpan<ValueDecoderEntry> table, ref InputBitStream input) {
    if (state >= table.Length)
      throw new InvalidDataException("LZFSE FSE value decoder state is out of range.");
    var entry = table[state];
    var bits = input.Pull(entry.TotalBits);
    state = checked((ushort)(entry.Delta + (int)(bits >> entry.ValueBits)));
    var mask = entry.ValueBits == 0 ? 0UL : (1UL << entry.ValueBits) - 1;
    return entry.BaseValue + checked((int)(bits & mask));
  }

  internal static void Encode(ref ushort state, ReadOnlySpan<EncoderEntry> table, byte symbol, ref OutputBitStream output) {
    if (symbol >= table.Length)
      throw new InvalidOperationException($"LZFSE FSE symbol {symbol} is not representable by this table.");
    var entry = table[symbol];
    var current = state;
    var useFullWidth = current >= entry.Threshold;
    var bitCount = useFullWidth ? entry.BitCount : entry.BitCount - 1;
    var delta = useFullWidth ? entry.Delta0 : entry.Delta1;
    if (bitCount < 0)
      throw new InvalidOperationException("LZFSE FSE encoder selected an invalid negative bit count.");

    var mask = bitCount == 0 ? 0u : (1u << bitCount) - 1;
    output.Push(bitCount, current & mask);
    state = checked((ushort)(delta + (current >> bitCount)));
  }

  internal ref struct InputBitStream {
    private readonly ReadOnlySpan<byte> _buffer;
    private ulong _accumulator;
    private int _bitCount;
    private int _pointer;

    internal InputBitStream(ReadOnlySpan<byte> buffer, int initialBits) {
      if (initialBits is < -7 or > 0)
        throw new InvalidDataException($"LZFSE FSE initial bit count {initialBits} is outside [-7,0].");

      this._buffer = buffer;

      // The stream is read backwards from its end, priming the accumulator with the last word.
      // Apple's decoder always takes a whole eight bytes and is free to reach in front of the payload
      // for them, because there it sits inside the block buffer. A managed slice has nothing in front
      // of it, so a payload shorter than the word is primed with everything it has instead: the same
      // bits, minus the reach. A block whose literals or matches are few enough produces exactly such
      // a payload, and refusing it would make those blocks undecodable.
      var wordBytes = initialBits != 0 ? 8 : 7;
      var primeBytes = Math.Min(buffer.Length, wordBytes);

      this._accumulator = 0;
      for (var i = 0; i < primeBytes; ++i)
        this._accumulator |= (ulong)buffer[buffer.Length - primeBytes + i] << (i * 8);

      this._pointer = buffer.Length - primeBytes;
      this._bitCount = primeBytes * 8 + initialBits;

      if (this._bitCount < 0)
        throw new InvalidDataException("LZFSE FSE bitstream is too short for its initial state.");

      if (this._accumulator >> this._bitCount != 0)
        throw new InvalidDataException("LZFSE FSE bitstream has non-zero padding bits.");
    }

    internal void Flush() {
      var bitsToLoad = (63 - this._bitCount) & ~7;
      if (bitsToLoad == 0)
        return;

      var bytesToLoad = bitsToLoad >> 3;
      var newPointer = this._pointer - bytesToLoad;
      if (newPointer < 0) {
        while (bytesToLoad-- > 0 && this._pointer > 0) {
          --this._pointer;
          this._accumulator = (this._accumulator << 8) | this._buffer[this._pointer];
          this._bitCount += 8;
        }
        return;
      }

      ulong incoming = 0;
      for (var i = 0; i < bytesToLoad; ++i)
        incoming |= (ulong)this._buffer[newPointer + i] << (i * 8);

      this._pointer = newPointer;
      this._accumulator = (this._accumulator << bitsToLoad) | incoming;
      this._bitCount += bitsToLoad;
    }

    internal ulong Pull(int count) {
      if (count <= 0)
        return 0;
      if (count > this._bitCount)
        throw new InvalidDataException("LZFSE FSE bitstream is truncated.");

      this._bitCount -= count;
      var result = this._accumulator >> this._bitCount;
      this._accumulator &= Mask(this._bitCount);
      return result;
    }
  }

  internal struct OutputBitStream {
    private ulong _accumulator;
    private int _bitCount;
    private List<byte>? _bytes;

    internal readonly int ByteCount => this._bytes?.Count ?? 0;

    internal void Push(int count, ulong value) {
      if (count <= 0)
        return;
      if (count > 56 || this._bitCount + count > 63)
        throw new InvalidOperationException("LZFSE FSE output accumulator overflow.");

      this._accumulator |= (value & Mask(count)) << this._bitCount;
      this._bitCount += count;
    }

    internal void Flush() {
      var bitsToFlush = this._bitCount & ~7;
      var bytesToFlush = bitsToFlush >> 3;
      if (bytesToFlush == 0)
        return;

      this._bytes ??= [];
      for (var i = 0; i < bytesToFlush; ++i) {
        this._bytes.Add((byte)this._accumulator);
        this._accumulator >>= 8;
      }
      this._bitCount -= bitsToFlush;
    }

    internal int Finish() {
      if (this._bitCount == 0)
        return 0;
      this._bytes ??= [];
      this._bytes.Add((byte)this._accumulator);
      var result = this._bitCount - 8;
      this._accumulator = 0;
      this._bitCount = 0;
      return result;
    }

    internal readonly byte[] ToArray() => this._bytes?.ToArray() ?? [];
  }

  private static ulong Mask(int bitCount) => bitCount switch {
    <= 0 => 0,
    >= 64 => ulong.MaxValue,
    _ => (1UL << bitCount) - 1,
  };
}
