namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes PMarc <c>-pm2-</c>: LZSS over an 8 KiB window with literals coded against a move-to-front
/// history and two small Huffman trees that are re-sent as the stream grows.
/// </summary>
/// <remarks>
/// <para>The first bit is discarded. A code tree (up to 29 symbols) is read; codes 0-7 select a literal
/// range in the history (<see cref="PmaHistory"/>), codes 8+ a copy whose length is 2-16 directly or a
/// variable-length value, and whose distance uses an offset tree of 5 to 8 symbols. The trees are rebuilt
/// after 1, 2 and 4 KiB of output and then every 4 KiB, each time with a flag bit saying whether a new
/// code tree follows.</para>
/// <para>Sourcing: converted from lhasa's <c>pm2_decoder.c</c> and <c>tree_decode.c</c> (ISC, Simon
/// Howard), tables copied exactly; see THIRD-PARTY-NOTICE.lhasa.txt beside this file. Checked against
/// the <c>-pm2-</c> archives of lhasa's test suite.</para>
/// </remarks>
public static class Pm2Decoder {
  private const int RingSize = 8192;
  private const int CodeTreeElements = 65;
  private const int OffsetTreeElements = 17;
  private const byte LeafFlag = 0x80;

  private static readonly (int Offset, int Bits)[] HistoryDecode =
    [(0, 3), (8, 3), (16, 4), (32, 5), (64, 5), (96, 5), (128, 6), (192, 6)];

  private static readonly (int Offset, int Bits)[] CopyDecode =
    [(17, 3), (25, 3), (33, 5), (65, 6), (129, 7), (256, 0)];

  /// <summary>Decodes <paramref name="originalSize"/> bytes of <c>-pm2-</c> data.</summary>
  /// <exception cref="InvalidDataException">The stream is damaged.</exception>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    ArgumentNullException.ThrowIfNull(compressed);
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);
    return new State(compressed, originalSize).Run();
  }

  private enum Rebuild { Unbuilt, Build1, Build2, Build3, Continuing }

  private sealed class State(byte[] compressed, int originalSize) {
    private readonly LhaBitInput _bits = new(compressed);
    private readonly byte[] _output = new byte[originalSize];
    private readonly byte[] _ring = Filled(RingSize, (byte)' ');
    private readonly PmaHistory _history = new();
    private readonly byte[] _codeTree = Filled(CodeTreeElements, LeafFlag);
    private readonly byte[] _offsetTree = Filled(OffsetTreeElements, LeafFlag);
    private Rebuild _treeState = Rebuild.Unbuilt;
    private int _rebuildRemaining;
    private bool _needOffsetTree;
    private int _ringPos;
    private int _outPos;

    private static byte[] Filled(int length, byte value) {
      var a = new byte[length];
      Array.Fill(a, value);
      return a;
    }

    public byte[] Run() {
      if (this._output.Length == 0)
        return this._output;
      this._bits.ReadBit();  // the first bit is discarded
      this.RebuildTrees();
      while (this._outPos < this._output.Length) {
        if (this._bits.Exhausted)
          throw new InvalidDataException("-pm2- stream ends before the member is complete.");
        var code = ReadFromTree(this._bits, this._codeTree);
        if (code < 8)
          this.Output(this._history.Find(Variable(HistoryDecode, code)));
        else
          this.Copy(code - 8);
      }
      return this._output;
    }

    private int Variable((int Offset, int Bits)[] table, int header) {
      if (header >= table.Length)
        throw new InvalidDataException("-pm2- code is out of range.");
      return table[header].Offset + this._bits.ReadBits(table[header].Bits);
    }

    private void Output(byte b) {
      this._ring[this._ringPos] = b;
      this._ringPos = (this._ringPos + 1) % RingSize;
      if (this._outPos < this._output.Length)
        this._output[this._outPos] = b;
      ++this._outPos;
      this._history.Update(b);
      if (--this._rebuildRemaining == 0)
        this.RebuildTrees();
    }

    private void Copy(int code) {
      var count = code < 15 ? code + 2 : this.Variable(CopyDecode, code - 15);
      int offset;
      if (code == 0)
        offset = this._bits.ReadBits(6);
      else if (code < 20) {
        var val = ReadFromTree(this._bits, this._offsetTree);
        offset = val == 0 ? this._bits.ReadBits(6) : (1 << (val + 5)) + this._bits.ReadBits(val + 5);
      } else
        offset = 0;
      var start = this._ringPos + RingSize - 1 - offset;
      for (var i = 0; i < count; ++i)
        this.Output(this._ring[(start + i) % RingSize]);
    }

    private void RebuildTrees() {
      switch (this._treeState) {
        case Rebuild.Unbuilt:
          this.ReadCodeTree();
          this.ReadOffsetTree(5);
          this._treeState = Rebuild.Build1;
          this._rebuildRemaining = 1024;
          break;
        case Rebuild.Build1:
          this.ReadOffsetTree(6);
          this._treeState = Rebuild.Build2;
          this._rebuildRemaining = 1024;
          break;
        case Rebuild.Build2:
          this.ReadOffsetTree(7);
          this._treeState = Rebuild.Build3;
          this._rebuildRemaining = 2048;
          break;
        case Rebuild.Build3:
          if (this._bits.ReadBit() == 1)
            this.ReadCodeTree();
          this.ReadOffsetTree(8);
          this._treeState = Rebuild.Continuing;
          this._rebuildRemaining = 4096;
          break;
        default:
          if (this._bits.ReadBit() == 1) {
            this.ReadCodeTree();
            this.ReadOffsetTree(8);
          }
          this._rebuildRemaining = 4096;
          break;
      }
    }

    private void ReadCodeTree() {
      var numCodes = this._bits.ReadBits(5);
      var minCodeLength = this._bits.ReadBits(3);
      if (numCodes > 29)
        throw new InvalidDataException("-pm2- code tree has more than 29 codes.");
      this._needOffsetTree = numCodes >= 10 && !(numCodes == 29 && minCodeLength == 0);
      if (minCodeLength == 0) {
        this._codeTree[0] = (byte)((numCodes - 1) | LeafFlag);
        return;
      }
      var lengthBits = this._bits.ReadBits(3);
      var lengths = new byte[numCodes];
      for (var i = 0; i < numCodes; ++i) {
        var val = this._bits.ReadBits(lengthBits);
        lengths[i] = val == 0 ? (byte)0 : (byte)(minCodeLength + val - 1);
      }
      BuildTree(this._codeTree, lengths);
    }

    private void ReadOffsetTree(int numOffsets) {
      if (!this._needOffsetTree)
        return;
      var lengths = new byte[numOffsets];
      int numCodes = 0, single = 0;
      for (var off = 0; off < numOffsets; ++off) {
        lengths[off] = (byte)this._bits.ReadBits(3);
        if (lengths[off] != 0) {
          single = off;
          ++numCodes;
        }
      }
      if (numCodes == 1) {
        this._offsetTree[0] = (byte)(single | LeafFlag);
        return;
      }
      BuildTree(this._offsetTree, lengths);
    }
  }

  /// <summary>Builds a canonical tree breadth-first: shorter codes take the earlier slots at each depth.</summary>
  internal static void BuildTree(byte[] tree, byte[] lengths) {
    int nextEntry = 0, allocated = 1, codeLength = 0;
    bool remaining;
    do {
      // Expand every open slot into a branch with two children, one level deeper.
      var newNodes = (allocated - nextEntry) * 2;
      if (allocated + newNodes <= tree.Length) {
        var end = allocated;
        while (nextEntry < end) {
          tree[nextEntry] = (byte)allocated;
          allocated += 2;
          ++nextEntry;
        }
      }
      ++codeLength;
      remaining = false;
      for (var i = 0; i < lengths.Length; ++i) {
        if (lengths[i] == codeLength) {
          if (nextEntry >= allocated)
            continue;  // over-subscribed: the reference ignores the code
          tree[nextEntry++] = (byte)(i | LeafFlag);
        } else if (lengths[i] > codeLength)
          remaining = true;
      }
    } while (remaining);
  }

  internal static int ReadFromTree(LhaBitInput bits, byte[] tree) {
    var code = tree[0];
    while ((code & LeafFlag) == 0) {
      var index = code + bits.ReadBit();
      if (index >= tree.Length)
        throw new InvalidDataException("-pm2- tree walk leaves the tree.");
      code = tree[index];
    }
    return code & ~LeafFlag;
  }
}
