using System.Buffers.Binary;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Round-trips the WinUpack / Upack stream format against an encoder written
/// straight from the same description the decoder was written from, so the
/// probability layout, the state machine and the range coder are all pinned by
/// something other than the decoder agreeing with itself.
/// </summary>
[TestFixture]
public class WinUpackStreamTests {
  [Test, Category("HappyPath")]
  public void Decompress_LiteralsOnly_RoundTrips() {
    var data = new byte[512];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 7 + (i >> 3));

    var ops = data.Select(b => Op.Literal(b)).ToArray();
    Assert.That(WinUpackStream.Decompress(Encoder.Encode(ops), data.Length), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Decompress_MatchesAndRepeats_RoundTrip() {
    // Every symbol kind the decoder knows: literals, a fresh match, a rep0
    // match, an older-distance rep, a single-byte short rep, and the matched
    // literal mode a match leaves behind.
    var ops = new List<Op>();
    foreach (var b in "the quick brown fox "u8.ToArray())
      ops.Add(Op.Literal(b));
    ops.Add(Op.Match(20, 9));       // repeat "the quick"
    ops.Add(Op.Literal((byte)'!'));
    ops.Add(Op.RepMatch(0, 4));     // reuse the last distance
    ops.Add(Op.Literal((byte)'?'));
    ops.Add(Op.Match(3, 2));
    ops.Add(Op.RepMatch(1, 6));     // reuse an older distance
    ops.Add(Op.ShortRep());
    foreach (var b in "0123456789"u8.ToArray())
      ops.Add(Op.Literal(b));
    ops.Add(Op.Match(48, 40));      // long enough to need a wide position slot
    ops.Add(Op.RepMatch(3, 3));

    var expected = Replay(ops);
    Assert.That(WinUpackStream.Decompress(Encoder.Encode(ops), expected.Length), Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void Decompress_LongDistanceMatch_UsesDirectBitsAndAlignTail() {
    var random = new Random(0x5EED);
    var ops = new List<Op>();
    for (var i = 0; i < 5000; ++i)
      ops.Add(Op.Literal((byte)random.Next(256)));
    ops.Add(Op.Match(4321, 60));
    ops.Add(Op.Match(70, 3));
    ops.Add(Op.Match(3000, 200));

    var expected = Replay(ops);
    Assert.That(WinUpackStream.Decompress(Encoder.Encode(ops), expected.Length), Is.EqualTo(expected));
  }

  [Test, Category("HappyPath")]
  public void UndoBranchFilter_RestoresRelativeCallTarget() {
    // E8 at offset 4 with the packer's encoding: marker byte then a big-endian
    // 24-bit target. The image is mapped at 0x00401000 and the filter bias is
    // always the image start minus four.
    var image = new byte[32];
    image[4] = 0xE8;
    image[5] = 0x2A;
    image[6] = 0x00;
    image[7] = 0x12;
    image[8] = 0x34;

    WinUpackStream.UndoBranchFilter(image, 0x00401000, 0x00400FFC, 1, 0x2A);

    // target 0x001234 + 0x400FFC - (0x401000 + 5) == 0x0000122B
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(5)), Is.EqualTo(0x0000122Bu));
  }

  [Test, Category("EdgeCase")]
  public void UndoBranchFilter_LeavesUntaggedBranchesAlone() {
    var image = new byte[32];
    image[4] = 0xE9;
    image[5] = 0x11; // not the marker
    image[6] = 0x22;
    image[7] = 0x33;
    image[8] = 0x44;
    var untouched = image.ToArray();

    WinUpackStream.UndoBranchFilter(image, 0x00401000, 0x00400FFC, 4, 0x2A);

    Assert.That(image, Is.EqualTo(untouched));
  }

  [Test, Category("EdgeCase")]
  public void UndoBranchFilter_StopsAfterTheStatedCount() {
    var image = new byte[64];
    for (var i = 0; i < 3; ++i) {
      image[8 * i] = 0xE8;
      image[8 * i + 1] = 0x2A;
      image[8 * i + 2] = 0x00;
      image[8 * i + 3] = 0x00;
      image[8 * i + 4] = 0x10;
    }

    WinUpackStream.UndoBranchFilter(image, 0x00401000, 0x00400FFC, 2, 0x2A);

    Assert.Multiple(() => {
      Assert.That(image[1], Is.Not.EqualTo(0x2A), "first entry should have been rewritten");
      Assert.That(image[9], Is.Not.EqualTo(0x2A), "second entry should have been rewritten");
      Assert.That(image[17], Is.EqualTo(0x2A), "third entry is past the stated count");
    });
  }

  [Test, Category("HappyPath")]
  public void WinUpackHandler_DecompressesAPlainHeaderContainer() {
    var ops = new List<Op>();
    foreach (var b in "MZ this is the payload of a synthetic Upack container. "u8.ToArray())
      ops.Add(Op.Literal(b));
    ops.Add(Op.Match(55, 55));
    ops.Add(Op.Match(110, 110));

    var expected = Replay(ops);
    var image = BuildPlainHeaderUpackPe(Encoder.Encode(ops), expected.Length);

    var match = Compression.Lib.ExecutablePackerHandlers.DetectBest(image);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("winupack"));

    var result = match.Handler.Unpack(match.Handler.Parse(image, match.Detection), new());
    var artifact = result.Artifacts.FirstOrDefault(a => a.Name == "decompressed_payload.bin");
    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(Compression.Core.ExecutableUnpacking.ExecutableUnpackLevel.PayloadDecompressed));
      Assert.That(artifact, Is.Not.Null);
      Assert.That(artifact?.Data, Is.EqualTo(expected));
    });
  }

  /// <summary>
  /// Builds the container shape Upack uses when it leaves a conventional PE
  /// header in place: a virtual-only <c>.Upack</c> target, the payload in a
  /// second section, and the parameter table right behind the section table,
  /// opening with its own load address.
  /// </summary>
  private static byte[] BuildPlainHeaderUpackPe(byte[] payload, int imageSize) {
    const int peOffset = 0x40;
    const int optionalOffset = peOffset + 24;
    const int optionalSize = 0xE0;
    const int sectionTable = optionalOffset + optionalSize;
    const int parameters = sectionTable + 80;
    const int rawOffset = 0x400;
    const uint imageBase = 0x00400000;
    const uint imageStart = imageBase + 0x1000;

    var image = new byte[rawOffset + payload.Length];
    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
    "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 22), 0x010F);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset), 0x10B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 16), 0x3000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 28), imageBase);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 32), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 36), 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 60), rawOffset);

    ".Upack\0\0"u8.CopyTo(image.AsSpan(sectionTable));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionTable + 8), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionTable + 12), 0x1000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionTable + 36), 0xE0000060);

    ".rsrc\0\0\0"u8.CopyTo(image.AsSpan(sectionTable + 40));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionTable + 48), 0x2000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionTable + 52), 0x3000);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionTable + 56), (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionTable + 60), rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionTable + 76), 0xE0000060);

    // Self-referential head of the parameter table, then the fields the stub
    // reads relative to it.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(parameters), imageBase + parameters);
    const int table = parameters + 0x0C;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(table + 0x04), imageStart);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(table + 0x1C), imageStart - 4);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(table + 0x2C), imageStart + (uint)imageSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(table + 0x48), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(table + 0x50), imageBase + 0x3000);

    // The stub's filter compare, which is where the per-file marker byte lives.
    new byte[] { 0x8B, 0x07, 0x3C, 0x2A, 0x75, 0xF3 }.CopyTo(image.AsSpan(0x300));

    payload.CopyTo(image.AsSpan(rawOffset));
    return image;
  }

  private static byte[] Replay(IEnumerable<Op> ops) {
    var output = new List<byte>();
    var reps = new int[4] { 1, 1, 1, 1 };
    foreach (var op in ops)
      switch (op.Kind) {
        case OpKind.Literal:
          output.Add(op.Value);
          break;
        case OpKind.Match:
          Copy(output, op.Distance, op.Length);
          reps[3] = reps[2];
          reps[2] = reps[1];
          reps[1] = reps[0];
          reps[0] = op.Distance;
          break;
        case OpKind.ShortRep:
          output.Add(output[^reps[0]]);
          break;
        default:
          var distance = reps[op.RepIndex];
          if (op.RepIndex > 0) {
            if (op.RepIndex == 3)
              reps[3] = reps[2];
            if (op.RepIndex >= 2)
              reps[2] = reps[1];
            reps[1] = reps[0];
            reps[0] = distance;
          }

          Copy(output, distance, op.Length);
          break;
      }

    return [.. output];
  }

  private static void Copy(List<byte> output, int distance, int length) {
    var start = output.Count - distance;
    for (var i = 0; i < length; ++i)
      output.Add(output[start + i]);
  }

  private enum OpKind { Literal, Match, RepMatch, ShortRep }

  private readonly record struct Op(OpKind Kind, byte Value, int Distance, int Length, int RepIndex) {
    public static Op Literal(byte value) => new(OpKind.Literal, value, 0, 0, 0);
    public static Op Match(int distance, int length) => new(OpKind.Match, 0, distance, length, 0);
    public static Op RepMatch(int index, int length) => new(OpKind.RepMatch, 0, 0, length, index);
    public static Op ShortRep() => new(OpKind.ShortRep, 0, 0, 0, 0);
  }

  /// <summary>
  /// Mirror image of the decoder: the same probability slots, the same state
  /// transitions and the same range coder, run forwards.
  /// </summary>
  private sealed class Encoder {
    private const int PROB_COUNT = 7168;
    private const int ALIGN = 0, IS_MATCH = 16, IS_REP = 28, IS_REP_G0 = 40, IS_REP0_LONG = 52;
    private const int IS_REP_G1 = 64, IS_REP_G2 = 76, SPEC_POS = 88, POS_SLOT = 216;
    private const int REP_LEN_CODER = 472, LEN_CODER = 746, LITERAL = 1020;

    private readonly ushort[] _probabilities = new ushort[PROB_COUNT];
    private readonly List<byte> _output = [];
    private readonly List<byte> _plain = [];
    private readonly int[] _reps = [1, 1, 1, 1];
    private ulong _low;
    private uint _range = uint.MaxValue;
    private byte _cache;
    private int _cacheSize = 1;
    private int _state;
    private bool _afterMatch;

    public static byte[] Encode(IEnumerable<Op> ops) {
      var encoder = new Encoder();
      encoder._probabilities.AsSpan().Fill(1 << 10);
      foreach (var op in ops)
        encoder.Write(op);
      return encoder.Finish();
    }

    private void Write(Op op) {
      if (op.Kind == OpKind.Literal) {
        this.EncodeBit(IS_MATCH + this._state, 0);
        this.WriteLiteral(op.Value);
        do
          this._state = this._state >= 3 ? this._state - 3 : 0;
        while (this._state >= 7);
        this._afterMatch = false;
        this._plain.Add(op.Value);
        return;
      }

      this.EncodeBit(IS_MATCH + this._state, 1);
      var matchState = this._state >= 7 ? 11 : 8;
      switch (op.Kind) {
        case OpKind.Match:
          this.EncodeBit(IS_REP + this._state, 0);
          this._reps[3] = this._reps[2];
          this._reps[2] = this._reps[1];
          this._reps[1] = this._reps[0];
          this.WriteLength(LEN_CODER, op.Length - 1);
          this.WriteDistance(op.Distance, op.Length - 1);
          this._reps[0] = op.Distance;
          this._state = matchState - 1;
          this.CopyPlain(op.Distance, op.Length);
          break;
        case OpKind.ShortRep:
          this.EncodeBit(IS_REP + this._state, 1);
          this.EncodeBit(IS_REP_G0 + this._state, 0);
          this.EncodeBit(IS_REP0_LONG + this._state, 0);
          this._state = matchState | 1;
          this._plain.Add(this._plain[^this._reps[0]]);
          break;
        default:
          this.EncodeBit(IS_REP + this._state, 1);
          if (op.RepIndex == 0) {
            this.EncodeBit(IS_REP_G0 + this._state, 0);
            this.EncodeBit(IS_REP0_LONG + this._state, 1);
          } else {
            this.EncodeBit(IS_REP_G0 + this._state, 1);
            this.EncodeBit(IS_REP_G1 + this._state, op.RepIndex == 1 ? 0 : 1);
            if (op.RepIndex >= 2)
              this.EncodeBit(IS_REP_G2 + this._state, op.RepIndex == 2 ? 0 : 1);
            var picked = this._reps[op.RepIndex];
            if (op.RepIndex == 3)
              this._reps[3] = this._reps[2];
            if (op.RepIndex >= 2)
              this._reps[2] = this._reps[1];
            this._reps[1] = this._reps[0];
            this._reps[0] = picked;
          }

          this._state = matchState;
          this.WriteLength(REP_LEN_CODER, op.Length - 1);
          this.CopyPlain(this._reps[0], op.Length);
          break;
      }

      this._afterMatch = true;
    }

    private void CopyPlain(int distance, int length) {
      var start = this._plain.Count - distance;
      for (var i = 0; i < length; ++i)
        this._plain.Add(this._plain[start + i]);
    }

    private void WriteLiteral(byte value) {
      var literalBase = LITERAL + ((this._plain.Count > 0 ? this._plain[^1] : 0) >> 5) * 768;
      var symbol = 1;
      var written = 0;
      if (this._afterMatch) {
        var matchByte = this._plain[^this._reps[0]];
        for (var mask = 0x80; ;) {
          var matchBit = (matchByte & mask) != 0 ? 1 : 0;
          var bit = (value & mask) != 0 ? 1 : 0;
          this.EncodeBit(literalBase + ((matchBit + 1) << 8) + symbol, bit);
          symbol = ((symbol << 1) | bit) & 0xFF;
          ++written;
          mask >>= 1;
          if (mask == 0)
            return;
          if (bit != matchBit)
            break;
        }
      }

      for (var i = 7 - written; i >= 0; --i) {
        var bit = (value >> i) & 1;
        this.EncodeBit(literalBase + symbol, bit);
        symbol = (symbol << 1) | bit;
      }
    }

    private void WriteLength(int probabilityBase, int length) {
      switch (length) {
        case <= 8:
          this.EncodeBit(probabilityBase, 0);
          this.WriteBitTree(probabilityBase + 2, 8, length - 1);
          break;
        case <= 16:
          this.EncodeBit(probabilityBase, 1);
          this.EncodeBit(probabilityBase + 1, 0);
          this.WriteBitTree(probabilityBase + 10, 8, length - 9);
          break;
        default:
          this.EncodeBit(probabilityBase, 1);
          this.EncodeBit(probabilityBase + 1, 1);
          this.WriteBitTree(probabilityBase + 18, 256, length - 17);
          break;
      }
    }

    private void WriteDistance(int distance, int length) {
      var value = (uint)(distance - 1);
      var slot = SlotFor(value);
      this.WriteBitTree(POS_SLOT + Math.Min(length - 1, 3) * 64, 64, (int)slot);
      if (slot < 4)
        return;

      var directBits = (int)(slot >> 1) - 1;
      var baseValue = (2u | (slot & 1)) << directBits;
      var rest = value - baseValue;
      if (directBits <= 5) {
        this.WriteReverseBitTree(SPEC_POS + (int)baseValue, directBits, rest);
        return;
      }

      this.WriteDirectBits(directBits - 4, rest >> 4);
      this.WriteReverseBitTree(ALIGN, 4, rest & 0xF);
    }

    private static uint SlotFor(uint value) {
      if (value < 4)
        return value;
      var bits = 31 - System.Numerics.BitOperations.LeadingZeroCount(value);
      return (uint)(bits << 1) | ((value >> (bits - 1)) & 1);
    }

    private void WriteBitTree(int probabilityBase, int limit, int symbol) {
      var node = 1;
      var total = limit;
      var bits = 0;
      while (total > 1) {
        total >>= 1;
        ++bits;
      }

      for (var i = bits - 1; i >= 0; --i) {
        var bit = (symbol >> i) & 1;
        this.EncodeBit(probabilityBase + node, bit);
        node = (node << 1) | bit;
      }
    }

    private void WriteReverseBitTree(int probabilityBase, int bits, uint value) {
      var forward = 0;
      for (var i = 0; i < bits; ++i)
        forward = (forward << 1) | (int)((value >> i) & 1);
      this.WriteBitTree(probabilityBase, 1 << bits, forward);
    }

    private void WriteDirectBits(int count, uint value) {
      for (var i = count - 1; i >= 0; --i) {
        this._range >>= 1;
        if (((value >> i) & 1) != 0)
          this._low += this._range;
        this.Normalize();
      }
    }

    private void EncodeBit(int index, int bit) {
      var probability = this._probabilities[index];
      var bound = (this._range >> 11) * probability;
      if (bit == 0) {
        this._range = bound;
        this._probabilities[index] = (ushort)(probability + ((2048 - probability) >> 5));
      } else {
        this._low += bound;
        this._range -= bound;
        this._probabilities[index] = (ushort)(probability - (probability >> 5));
      }

      this.Normalize();
    }

    private void Normalize() {
      if (this._range >> 24 != 0)
        return;
      this._range <<= 8;
      this.ShiftLow();
    }

    private void ShiftLow() {
      if (this._low < 0xFF000000UL || this._low > 0xFFFFFFFFUL) {
        var carry = (byte)(this._low >> 32);
        do {
          this._output.Add((byte)(this._cache + carry));
          this._cache = 0xFF;
        } while (--this._cacheSize != 0);
        this._cache = (byte)(this._low >> 24);
      }

      ++this._cacheSize;
      this._low = (this._low << 8) & 0xFFFFFFFFUL;
    }

    private byte[] Finish() {
      for (var i = 0; i < 5; ++i)
        this.ShiftLow();

      // The decoder seeds its code word from four payload bytes rather than
      // five, so the encoder's leading cache byte is not part of the stream.
      return [.. this._output.Skip(1), 0, 0, 0, 0, 0, 0, 0, 0];
    }
  }
}
