using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Density;

/// <summary>The three density algorithms.</summary>
public enum DensityAlgorithm : byte {
  /// <summary>One 16-bit hash dictionary; a quad is either its hash or itself.</summary>
  Chameleon = 1,

  /// <summary>Two dictionary slots per hash plus one predicted successor per previous quad.</summary>
  Cheetah = 2,

  /// <summary>Two dictionary slots per hash plus five predicted successors per previous quad.</summary>
  Lion = 3,
}

/// <summary>
/// density, Guillaume Voirin's 4-byte-unit compressor, converted from density-rs 0.16.6
/// (https://github.com/g1mv/density, revision <c>58938943bb0dfd85b186551a3d1d36cc286f9c1d</c>,
/// MIT OR Apache-2.0; see <c>THIRD-PARTY-NOTICE.density.txt</c>). Streams match the reference's
/// <c>Chameleon/Cheetah/Lion::encode</c> byte for byte; like the reference, a stream carries no
/// header and ends with its input.
/// </summary>
/// <remarks>
/// Input is split into blocks of 256 (Chameleon), 128 (Cheetah) or 64 (Lion) bytes. A block is a
/// little-endian signature of 8, 8 or 6 bytes with 1, 2 or 3 flag bits per 4-byte quad (least
/// significant first), followed by the quads' codes: a plain quad, a 16-bit hash, or nothing for a
/// predicted quad. Fewer than four trailing bytes are stored plainly. After two blocks in a row
/// that did not shrink, blocks are stored verbatim for a penalty count that the encoder and
/// decoder track identically.
/// </remarks>
public static class DensityCodec {
  private const uint HashMultiplier = 0x9D6EF916;

  /// <summary>Encodes <paramref name="data"/> with the given algorithm.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, DensityAlgorithm algorithm) {
    var model = Model.Create(algorithm);
    var output = new byte[SafeEncodeSize(data.Length, model) + 8];
    var outPos = 0;
    var protection = new Protection();
    for (var start = 0; start < data.Length; start += model.BlockSize) {
      var block = data.Slice(start, Math.Min(model.BlockSize, data.Length - start));
      if (protection.RevertToCopy()) {
        block.CopyTo(output.AsSpan(outPos));
        outPos += block.Length;
        protection.Decay();
        continue;
      }

      var mark = outPos;
      var signature = new Signature(outPos);
      outPos += model.SignatureBytes;
      var i = 0;
      for (; i + 4 <= block.Length; i += 4)
        model.EncodeQuad(BinaryPrimitives.ReadUInt32LittleEndian(block[i..]), output, ref outPos, ref signature);
      // Fewer than four bytes left: stored plainly under an implicit plain flag.
      block[i..].CopyTo(output.AsSpan(outPos));
      outPos += block.Length - i;

      var value = BitConverter.IsLittleEndian ? signature.Value : BinaryPrimitives.ReverseEndianness(signature.Value);
      BitConverter.GetBytes(value).AsSpan(0, model.SignatureBytes).CopyTo(output.AsSpan(signature.Position));
      protection.Update(outPos - mark >= model.BlockSize);
    }

    return output.AsSpan(0, outPos).ToArray();
  }

  /// <summary>Decodes a stream written with <paramref name="algorithm"/>.</summary>
  /// <param name="data">The whole stream.</param>
  /// <param name="algorithm">The algorithm it was written with (streams do not record it).</param>
  /// <param name="sizeHint">Expected decoded size, used only to size the buffer.</param>
  /// <exception cref="InvalidDataException">The stream is truncated or inconsistent.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data, DensityAlgorithm algorithm, int sizeHint = 0) {
    var model = Model.Create(algorithm);
    var output = new Output(Math.Max(sizeHint, data.Length * 2));
    var reader = new Reader(data);
    var protection = new Protection();
    var iterations = model.BlockSize / model.DecodeUnitSize;

    while (reader.Remaining >= model.SignatureBytes + model.BlockSize) {
      if (protection.RevertToCopy()) {
        output.Push(reader.Read(model.BlockSize));
        protection.Decay();
      } else {
        var mark = reader.Position;
        var signature = model.ReadSignature(ref reader);
        for (var u = 0; u < iterations; ++u)
          model.DecodeUnit(ref reader, ref signature, output);
        protection.Update(reader.Position - mark >= model.BlockSize);
      }
    }

    while (reader.Remaining > 0) {
      if (protection.RevertToCopy()) {
        if (reader.Remaining > model.BlockSize)
          output.Push(reader.Read(model.BlockSize));
        else {
          output.Push(reader.Read(reader.Remaining));
          break;
        }

        protection.Decay();
      } else {
        var mark = reader.Position;
        if (reader.Remaining < model.SignatureBytes)
          throw new InvalidDataException("density stream ends inside a signature.");
        var signature = model.ReadSignature(ref reader);
        var finished = false;
        for (var u = 0; u < iterations && !finished; ++u)
          if (reader.Remaining >= model.DecodeUnitSize)
            model.DecodeUnit(ref reader, ref signature, output);
          else
            finished = model.DecodePartialUnit(ref reader, ref signature, output);
        if (finished)
          break;
        protection.Update(reader.Position - mark >= model.BlockSize);
      }
    }

    return output.ToArray();
  }

  private static int SafeEncodeSize(int size, Model model) {
    var blocks = size / model.BlockSize;
    return size + blocks * model.SignatureBytes + (size % model.BlockSize > 0 ? model.SignatureBytes : 0);
  }

  private static ushort Hash(uint quad) => (ushort)((quad * HashMultiplier) >> 16);

  /// <summary>density's blow-up protection: after two incompressible blocks, copy for a while.</summary>
  private struct Protection() {
    private byte _copyPenalty;
    private byte _copyPenaltyStart = 1;
    private bool _previousIncompressible;
    private ulong _counter;

    public bool RevertToCopy() {
      if ((this._counter & 0xF) == 0 && this._copyPenaltyStart > 1)
        this._copyPenaltyStart >>= 1;
      ++this._counter;
      return this._copyPenalty > 0;
    }

    public void Decay() {
      --this._copyPenalty;
      if (this._copyPenalty == 0)
        ++this._copyPenaltyStart;
    }

    public void Update(bool incompressible) {
      if (incompressible) {
        if (this._previousIncompressible)
          this._copyPenalty = this._copyPenaltyStart;
        this._previousIncompressible = true;
      } else
        this._previousIncompressible = false;
    }
  }

  private struct Signature(int position) {
    public readonly int Position = position;
    public ulong Value;
    private int _shift;

    public void Push(ulong bits, int count) {
      this.Value |= bits << this._shift;
      this._shift += count;
    }

    public ulong Read(int count) {
      var bits = this.Value & ((1UL << count) - 1);
      this.Value >>= count;
      return bits;
    }
  }

  private ref struct Reader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; private set; }

    public readonly int Remaining => this._data.Length - this.Position;

    public ReadOnlySpan<byte> Read(int count) {
      if (count > this.Remaining)
        throw new InvalidDataException("density stream is truncated.");
      var span = this._data.Slice(this.Position, count);
      this.Position += count;
      return span;
    }

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(this.Read(4));

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(this.Read(2));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(this.Read(8));

    public void Rewind(int count) => this.Position -= count;
  }

  private sealed class Output(int capacity) {
    private byte[] _buffer = new byte[Math.Max(16, capacity)];
    private int _length;

    public void Push(ReadOnlySpan<byte> bytes) {
      if (this._length + bytes.Length > this._buffer.Length)
        Array.Resize(ref this._buffer, Math.Max(this._buffer.Length * 2, this._length + bytes.Length));
      bytes.CopyTo(this._buffer.AsSpan(this._length));
      this._length += bytes.Length;
    }

    public void PushQuad(uint quad) {
      Span<byte> bytes = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(bytes, quad);
      this.Push(bytes);
    }

    public byte[] ToArray() => this._buffer.AsSpan(0, this._length).ToArray();
  }

  private abstract class Model {
    public abstract int BlockSize { get; }
    public abstract int SignatureBytes { get; }
    public abstract int DecodeUnitSize { get; }

    public static Model Create(DensityAlgorithm algorithm) => algorithm switch {
      DensityAlgorithm.Chameleon => new Chameleon(),
      DensityAlgorithm.Cheetah => new Cheetah(),
      DensityAlgorithm.Lion => new Lion(),
      _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown density algorithm."),
    };

    public abstract void EncodeQuad(uint quad, byte[] output, ref int outPos, ref Signature signature);

    public virtual Signature ReadSignature(ref Reader reader) => new(0) { Value = reader.ReadUInt64() };

    public abstract void DecodeUnit(ref Reader reader, ref Signature signature, Output output);

    public abstract bool DecodePartialUnit(ref Reader reader, ref Signature signature, Output output);

    protected static void WriteQuad(byte[] output, ref int outPos, uint quad) {
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos), quad);
      outPos += 4;
    }

    protected static void WriteHash(byte[] output, ref int outPos, ushort hash) {
      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), hash);
      outPos += 2;
    }

    /// <summary>
    /// Returns true when the stream ends here: a plain flag with no quad left, after copying any
    /// one to three trailing bytes.
    /// </summary>
    protected static bool PlainTail(ref Reader reader, Output output) {
      switch (reader.Remaining) {
        case 0:
          return true;
        case < 4:
          output.Push(reader.Read(reader.Remaining));
          return true;
        default:
          return false;
      }
    }
  }

  private sealed class Chameleon : Model {
    private readonly uint[] _map = new uint[1 << 16];

    public override int BlockSize => 256;
    public override int SignatureBytes => 8;
    public override int DecodeUnitSize => 8;

    public override void EncodeQuad(uint quad, byte[] output, ref int outPos, ref Signature signature) {
      var hash = Hash(quad);
      if (this._map[hash] != quad) {
        signature.Push(0, 1);
        WriteQuad(output, ref outPos, quad);
        this._map[hash] = quad;
      } else {
        signature.Push(1, 1);
        WriteHash(output, ref outPos, hash);
      }
    }

    private uint Plain(ref Reader reader) {
      var quad = reader.ReadUInt32();
      this._map[Hash(quad)] = quad;
      return quad;
    }

    private uint Map(ref Reader reader) => this._map[reader.ReadUInt16()];

    public override void DecodeUnit(ref Reader reader, ref Signature signature, Output output) {
      uint a, b;
      switch (signature.Read(2)) {
        case 0:
          a = this.Plain(ref reader);
          b = this.Plain(ref reader);
          break;
        case 1:
          a = this.Map(ref reader);
          b = this.Plain(ref reader);
          break;
        case 2:
          a = this.Plain(ref reader);
          b = this.Map(ref reader);
          break;
        default:
          a = this.Map(ref reader);
          b = this.Map(ref reader);
          break;
      }

      output.PushQuad(a);
      output.PushQuad(b);
    }

    public override bool DecodePartialUnit(ref Reader reader, ref Signature signature, Output output) {
      for (var i = 0; i < 2; ++i) {
        uint quad;
        if (signature.Read(1) == 0) {
          if (PlainTail(ref reader, output))
            return true;
          quad = this.Plain(ref reader);
        } else
          quad = this.Map(ref reader);
        output.PushQuad(quad);
      }

      return false;
    }
  }

  private sealed class Cheetah : Model {
    private readonly uint[] _chunkA = new uint[1 << 16];
    private readonly uint[] _chunkB = new uint[1 << 16];
    private readonly uint[] _next = new uint[1 << 16];
    private ushort _lastHash;

    public override int BlockSize => 128;
    public override int SignatureBytes => 8;
    public override int DecodeUnitSize => 4;

    public override void EncodeQuad(uint quad, byte[] output, ref int outPos, ref Signature signature) {
      var hash = Hash(quad);
      if (this._next[this._lastHash] != quad) {
        if (this._chunkA[hash] != quad) {
          if (this._chunkB[hash] != quad) {
            signature.Push(0, 2);
            WriteQuad(output, ref outPos, quad);
          } else {
            signature.Push(2, 2);
            WriteHash(output, ref outPos, hash);
          }

          this._chunkB[hash] = this._chunkA[hash];
          this._chunkA[hash] = quad;
        } else {
          signature.Push(1, 2);
          WriteHash(output, ref outPos, hash);
        }

        this._next[this._lastHash] = quad;
      } else
        signature.Push(3, 2);

      this._lastHash = hash;
    }

    private (ushort Hash, uint Quad) Decode(ulong flag, ref Reader reader) {
      ushort hash;
      uint quad;
      switch (flag) {
        case 0:
          quad = reader.ReadUInt32();
          hash = Hash(quad);
          this._chunkB[hash] = this._chunkA[hash];
          this._chunkA[hash] = quad;
          this._next[this._lastHash] = quad;
          break;
        case 1:
          hash = reader.ReadUInt16();
          quad = this._chunkA[hash];
          this._next[this._lastHash] = quad;
          break;
        case 2:
          hash = reader.ReadUInt16();
          quad = this._chunkB[hash];
          this._chunkB[hash] = this._chunkA[hash];
          this._chunkA[hash] = quad;
          this._next[this._lastHash] = quad;
          break;
        default:
          quad = this._next[this._lastHash];
          hash = Hash(quad);
          break;
      }

      return (hash, quad);
    }

    public override void DecodeUnit(ref Reader reader, ref Signature signature, Output output) {
      var (hash, quad) = this.Decode(signature.Read(2), ref reader);
      this._lastHash = hash;
      output.PushQuad(quad);
    }

    public override bool DecodePartialUnit(ref Reader reader, ref Signature signature, Output output) {
      var flag = signature.Read(2);
      if (flag == 0 && PlainTail(ref reader, output))
        return true;
      var (hash, quad) = this.Decode(flag, ref reader);
      this._lastHash = hash;
      output.PushQuad(quad);
      return false;
    }
  }

  private sealed class Lion : Model {
    private readonly uint[] _chunkA = new uint[1 << 16];
    private readonly uint[] _chunkB = new uint[1 << 16];
    private readonly uint[][] _next = [new uint[1 << 16], new uint[1 << 16], new uint[1 << 16], new uint[1 << 16], new uint[1 << 16]];
    private ushort _lastHash;

    public override int BlockSize => 64;
    public override int SignatureBytes => 6;
    public override int DecodeUnitSize => 4;

    /// <summary>Moves prediction <paramref name="depth"/> to the front, pushing the others back.</summary>
    private void Promote(int depth, uint quad) {
      for (var i = depth; i > 0; --i)
        this._next[i][this._lastHash] = this._next[i - 1][this._lastHash];
      this._next[0][this._lastHash] = quad;
    }

    public override void EncodeQuad(uint quad, byte[] output, ref int outPos, ref Signature signature) {
      var hash = Hash(quad);
      var last = this._lastHash;
      if (this._next[0][last] == quad)
        signature.Push(1, 3);
      else if (this._next[1][last] == quad) {
        signature.Push(2, 3);
        this.Promote(1, quad);
      } else if (this._next[2][last] == quad) {
        signature.Push(3, 3);
        this.Promote(2, quad);
      } else if (this._next[3][last] == quad) {
        signature.Push(4, 3);
        this.Promote(3, quad);
      } else if (this._next[4][last] == quad) {
        signature.Push(5, 3);
        this.Promote(4, quad);
      } else if (this._chunkA[hash] == quad) {
        signature.Push(6, 3);
        WriteHash(output, ref outPos, hash);
        this.Promote(4, quad);
      } else {
        if (this._chunkB[hash] != quad) {
          signature.Push(0, 3);
          WriteQuad(output, ref outPos, quad);
        } else {
          signature.Push(7, 3);
          WriteHash(output, ref outPos, hash);
        }

        this._chunkB[hash] = this._chunkA[hash];
        this._chunkA[hash] = quad;
        this.Promote(4, quad);
      }

      this._lastHash = hash;
    }

    public override Signature ReadSignature(ref Reader reader) {
      if (reader.Remaining < 8) {
        var bytes = reader.Read(6);
        ulong value = 0;
        for (var i = 5; i >= 0; --i)
          value = value << 8 | bytes[i];
        return new Signature(0) { Value = value };
      }

      var full = reader.ReadUInt64();
      reader.Rewind(2);
      return new Signature(0) { Value = full & 0x0000FFFFFFFFFFFFUL };
    }

    private (ushort Hash, uint Quad) Decode(ulong flag, ref Reader reader) {
      var last = this._lastHash;
      uint quad;
      switch (flag) {
        case 0:
          quad = reader.ReadUInt32();
          var plainHash = Hash(quad);
          this._chunkB[plainHash] = this._chunkA[plainHash];
          this._chunkA[plainHash] = quad;
          this.Promote(4, quad);
          return (plainHash, quad);
        case 6: {
          var hash = reader.ReadUInt16();
          quad = this._chunkA[hash];
          this.Promote(4, quad);
          return (hash, quad);
        }
        case 7: {
          var hash = reader.ReadUInt16();
          quad = this._chunkB[hash];
          this._chunkB[hash] = this._chunkA[hash];
          this._chunkA[hash] = quad;
          this.Promote(4, quad);
          return (hash, quad);
        }
        case 2 or 3 or 4 or 5: {
          var depth = (int)flag - 1;
          quad = this._next[depth][last];
          this.Promote(depth, quad);
          return (Hash(quad), quad);
        }
        default:
          quad = this._next[0][last];
          return (Hash(quad), quad);
      }
    }

    public override void DecodeUnit(ref Reader reader, ref Signature signature, Output output) {
      var (hash, quad) = this.Decode(signature.Read(3), ref reader);
      this._lastHash = hash;
      output.PushQuad(quad);
    }

    public override bool DecodePartialUnit(ref Reader reader, ref Signature signature, Output output) {
      var flag = signature.Read(3);
      if (flag == 0 && PlainTail(ref reader, output))
        return true;
      var (hash, quad) = this.Decode(flag, ref reader);
      this._lastHash = hash;
      output.PushQuad(quad);
      return false;
    }
  }
}
