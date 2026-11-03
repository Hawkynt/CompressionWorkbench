// Implemented from docs/CTW-ON-DISK.md; see CtwCoder.cs.
using System.Buffers.Binary;

namespace FileFormat.Ctw;

/// <summary>
/// Files of the CTW 0.1 compression utility (Franken &amp; Peeters, TU Eindhoven, 2002): a 12-byte
/// header with the model parameters, then the log-domain arithmetic code of the input.
/// </summary>
public static class CtwStream {
  internal const int HeaderSize = 12;
  private const int Half = 4096;

  /// <summary>Compresses with the reference tool's default parameters.</summary>
  public static void Compress(Stream input, Stream output) => Compress(input, output, new CtwSettings());

  /// <summary>Compresses with the given parameters.</summary>
  public static void Compress(Stream input, Stream output, CtwSettings settings) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    output.Write(Encode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), settings));
  }

  /// <summary>Decompresses a CTW 0.1 file.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    output.Write(Decode(buffer.ToArray()));
  }

  internal static byte[] Encode(ReadOnlySpan<byte> data, CtwSettings settings) {
    settings.Validate();
    var output = new List<byte>(data.Length / 2 + 64);
    Span<byte> header = stackalloc byte[HeaderSize];
    WriteHeader(header, settings, data.Length);
    output.AddRange(header.ToArray());

    var coder = new Encoder(output);
    var model = new CtwModel(settings, data.Length);
    var prefix = Math.Min(model.PrefixLength, data.Length);
    for (var i = 0; i < prefix; ++i) {
      model.AddPrefixByte(i, data[i]);
      for (var bit = 7; bit >= 0; --bit)
        coder.Encode(Half, (data[i] >> bit) & 1);
    }

    if (prefix < data.Length) {
      model.Start(data[prefix - 1]);
      for (var i = prefix; i < data.Length; ++i) {
        var value = data[i];
        for (var phase = 0; phase < 8; ++phase) {
          var bit = (value >> (7 - phase)) & 1;
          var (step, likely) = model.Predict(phase, value);
          coder.Encode(step, bit != likely ? 1 : 0);
          model.Update(bit);
        }

        model.EndByte(value);
      }
    }

    coder.Finish();
    return [.. output];
  }

  internal static byte[] Decode(byte[] file) {
    var (settings, length) = ReadHeader(file);
    var output = new byte[length];
    var decoder = new Decoder(file, HeaderSize);
    var model = new CtwModel(settings, length);
    var prefix = Math.Min(model.PrefixLength, length);
    for (var i = 0; i < prefix; ++i) {
      var value = 0;
      for (var bit = 0; bit < 8; ++bit)
        value = value << 1 | decoder.Decode(Half);
      output[i] = (byte)value;
      model.AddPrefixByte(i, (byte)value);
    }

    if (prefix < length) {
      model.Start(output[prefix - 1]);
      for (var i = prefix; i < length; ++i) {
        var value = 0;
        for (var phase = 0; phase < 8; ++phase) {
          var (step, likely) = model.Predict(phase, (byte)value);
          var bit = decoder.Decode(step) == 1 ? 1 - likely : likely;
          model.Update(bit);
          value |= bit << (7 - phase);
        }

        output[i] = (byte)value;
        model.EndByte((byte)value);
      }
    }

    return output;
  }

  private static void WriteHeader(Span<byte> header, CtwSettings settings, int length) {
    BinaryPrimitives.WriteUInt16BigEndian(header, HeaderSize);
    header[2] = 0;
    header[3] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)length);
    header[8] = (byte)settings.TreeDepth;
    header[9] = (byte)settings.Probes;
    header[10] = (byte)(settings.NodeCountLog2 | (settings.StrictPruning ? 32 : 0) | (settings.RootWeighting ? 64 : 0) | (settings.ZeroRedundancy ? 128 : 0));
    header[11] = (byte)((settings.FileBufferLog2 - 9) | settings.MaxLogBetaLog2 << 4);
  }

  internal static (CtwSettings Settings, int Length) ReadHeader(ReadOnlySpan<byte> file) {
    if (file.Length < HeaderSize)
      throw new InvalidDataException("CTW: file shorter than its 12-byte header");
    var headerSize = BinaryPrimitives.ReadUInt16BigEndian(file);
    if (file[2] != 0)
      throw new InvalidDataException($"CTW: file version {file[2]} is not 0");
    if (headerSize != HeaderSize)
      throw new NotSupportedException($"CTW: a {headerSize}-byte header is not the 12-byte layout of version 0.1");
    var length = BinaryPrimitives.ReadUInt32BigEndian(file[4..]);
    if (length > int.MaxValue)
      throw new NotSupportedException($"CTW: a {length}-byte payload is larger than this decoder handles");
    var settings = new CtwSettings(
      TreeDepth: file[8],
      Probes: file[9],
      NodeCountLog2: file[10] & 31,
      FileBufferLog2: (file[11] & 15) + 9,
      MaxLogBetaLog2: file[11] >> 4,
      StrictPruning: (file[10] & 32) != 0,
      RootWeighting: (file[10] & 64) != 0,
      ZeroRedundancy: (file[10] & 128) != 0);
    settings.Validate();
    return (settings, (int)length);
  }

  // ── the log-domain arithmetic coder ([EIDMA] ch. 6) ─────────────────────────

  private const int Entries = 4096;
  private const int AccumulatorBits = 13;
  private const int AccumulatorMask = (1 << AccumulatorBits) - 1;
  private const int DelayBits = 24;
  private const int DelayMask = (1 << DelayBits) - 1;

  private sealed class Encoder(List<byte> output) {
    private int _delay;
    private int _accumulator;
    private int _pointer;

    private int _skip = DelayBits; // the first 24 bits out of the delay register are always zero
    private int _pending;
    private int _pendingBits;

    private void Put(int value, int bits) {
      for (var i = bits - 1; i >= 0; --i) {
        if (_skip > 0) {
          --_skip;
          continue;
        }

        _pending = _pending << 1 | ((value >> i) & 1);
        if (++_pendingBits == 8) {
          output.Add((byte)_pending);
          _pending = 0;
          _pendingBits = 0;
        }
      }
    }

    private void Shift(int count) {
      var fromAccumulator = _accumulator >> (AccumulatorBits - count);
      _accumulator = (_accumulator << count) & AccumulatorMask;
      Put(_delay >> (DelayBits - count), count);
      _delay = ((_delay << count) & DelayMask) | fromAccumulator;
    }

    /// <summary>Codes <paramref name="lessLikely"/> (1 = the less likely bit) at step <paramref name="step"/>.</summary>
    public void Encode(int step, int lessLikely) {
      if (_pointer >= Entries) {
        var shifts = _pointer / Entries;
        Shift(shifts);
        _pointer -= shifts * Entries;
      }

      while (_delay == DelayMask)
        Shift(1);

      var target = _pointer + step;
      if (lessLikely == 0) {
        _pointer = target;
        return;
      }

      if (target < Entries) {
        Add(2 * CtwTables.Exp[target]);
        _pointer = CtwTables.Log[CtwTables.Exp[_pointer] - CtwTables.Exp[target]];
      } else {
        target -= Entries;
        Add(CtwTables.Exp[target]);
        _pointer = CtwTables.Log[2 * CtwTables.Exp[_pointer] - CtwTables.Exp[target]] + Entries;
      }
    }

    private void Add(int value) {
      _accumulator += value;
      if (_accumulator > AccumulatorMask) {
        _accumulator &= AccumulatorMask;
        ++_delay;
      }
    }

    public void Finish() {
      Put(_delay, DelayBits);
      Put(_accumulator, AccumulatorBits);
      if (_pendingBits > 0)
        output.Add((byte)(_pending << (8 - _pendingBits)));
    }
  }

  private sealed class Decoder {
    private readonly byte[] _input;
    private int _bitPosition;

    private int _delay;
    private int _accumulator;
    private int _pointer;
    private int _codeDelay;
    private int _codeAccumulator;

    public Decoder(byte[] input, int start) {
      _input = input;
      _bitPosition = start * 8;
      _codeAccumulator = Pull(AccumulatorBits);
    }

    private int Pull(int bits) {
      var value = 0;
      for (var i = 0; i < bits; ++i) {
        var index = _bitPosition >> 3;
        var bit = index < _input.Length ? (_input[index] >> (7 - (_bitPosition & 7))) & 1 : 0;
        value = value << 1 | bit;
        ++_bitPosition;
      }

      return value;
    }

    private void Shift(int count) {
      var fromAccumulator = _accumulator >> (AccumulatorBits - count);
      _accumulator = (_accumulator << count) & AccumulatorMask;
      _delay = ((_delay << count) & DelayMask) | fromAccumulator;

      var fromCode = _codeAccumulator >> (AccumulatorBits - count);
      _codeAccumulator = ((_codeAccumulator << count) & AccumulatorMask) | Pull(count);
      _codeDelay = ((_codeDelay << count) & DelayMask) | fromCode;
    }

    /// <summary>Decodes one decision: 1 when the less likely bit was coded.</summary>
    public int Decode(int step) {
      if (_pointer >= Entries) {
        var shifts = _pointer / Entries;
        Shift(shifts);
        _pointer -= shifts * Entries;
      }

      while (_delay == DelayMask)
        Shift(1);

      var target = _pointer + step;
      int thresholdAccumulator, add;
      if (target < Entries)
        add = 2 * CtwTables.Exp[target];
      else
        add = CtwTables.Exp[target - Entries];

      thresholdAccumulator = _accumulator + add;
      var thresholdDelay = _delay;
      if (thresholdAccumulator > AccumulatorMask) {
        thresholdAccumulator &= AccumulatorMask;
        ++thresholdDelay;
      }

      var lessLikely = _codeDelay > thresholdDelay || (_codeDelay == thresholdDelay && _codeAccumulator >= thresholdAccumulator);
      if (!lessLikely) {
        _pointer = target;
        return 0;
      }

      _accumulator = thresholdAccumulator;
      _delay = thresholdDelay;
      _pointer = target < Entries
        ? CtwTables.Log[CtwTables.Exp[_pointer] - CtwTables.Exp[target]]
        : CtwTables.Log[2 * CtwTables.Exp[_pointer] - CtwTables.Exp[target - Entries]] + Entries;
      return 1;
    }
  }
}
