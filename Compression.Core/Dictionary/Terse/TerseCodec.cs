using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Terse;

/// <summary>
/// IBM TERSE (z/OS <c>AMATERSE</c> / <c>TRSMAIN</c>, TERSE/PC): PACK, an LZW variant with
/// least-recently-used dictionary recycling, and SPACK, a pair-tree coder; both send
/// 12-bit codes packed big-endian.
/// </summary>
/// <remarks>
/// <para>
/// Headers: native (<c>01</c> or <c>07</c>, <c>89 69 A5</c>, 16-bit record length; always
/// SPACK) or host (<c>02</c> PACK / <c>05</c> SPACK, record format, 16-bit record length,
/// flags, ratio, block size, 32-bit record length). Codes 1–256 are bytes plus one, 257
/// is a record mark, 0 ends the data. Output follows TerseDecompress's binary mode:
/// fixed records are concatenated, variable records get a 4-byte RDW each.
/// </para>
/// <para>
/// The decoders are converted from the Open Mainframe Project's TerseDecompress
/// (Apache-2.0, see THIRD-PARTY-NOTICE.TerseDecompress.txt). The PACK encoder is original:
/// it replays the decoder's dictionary and LRU updates for every code it emits.
/// </para>
/// </remarks>
public static class TerseCodec {

  private const int TreeSize = 0x1000;
  private const int CodeSize = 257;
  private const int RecordMark = 257;
  private const int EndOfFile = 0;
  private const int None = -1;
  private const int EbcdicSpaceCode = 0x40 + 1;

  /// <summary>Parsed TERSE header.</summary>
  /// <param name="IsHost">Host (z/OS) header rather than native (PC).</param>
  /// <param name="IsSpack">SPACK rather than PACK coding.</param>
  /// <param name="IsVariable">Variable-length records (RECFM=V).</param>
  /// <param name="RecordLength">Logical record length.</param>
  /// <param name="HeaderLength">Bytes before the coded data.</param>
  public readonly record struct Header(bool IsHost, bool IsSpack, bool IsVariable, int RecordLength, int HeaderLength);

  /// <summary>Reads and validates the header.</summary>
  /// <exception cref="InvalidDataException">The header is not a TERSE header.</exception>
  public static Header ReadHeader(ReadOnlySpan<byte> data) {
    if (data.Length < 1) throw new InvalidDataException("TERSE: empty input.");
    switch (data[0]) {
      case 0x01 or 0x07:
        if (data.Length < 6 || data[1] != 0x89 || data[2] != 0x69 || data[3] != 0xA5)
          throw new InvalidDataException("TERSE: invalid native header validation bytes.");
        // Native files are always SPACK-coded (TerseDecompress leaves its SPACK default on).
        return new(false, true, false, BinaryPrimitives.ReadUInt16BigEndian(data[4..]), 6);
      case 0x02 or 0x05: {
        if (data.Length < 12) throw new InvalidDataException("TERSE: truncated host header.");
        var variable = data[1];
        var len1 = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var flags = data[4];
        var ratio = data[5];
        var blockSize = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        var len2 = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        if (len2 < 0) throw new InvalidDataException("TERSE: record length too large.");
        if (variable is not (0 or 1)) throw new InvalidDataException($"TERSE: unknown record format flag {variable:X2}.");
        if (len1 == 0 && len2 == 0) throw new InvalidDataException("TERSE: record length is 0.");
        if (len1 != 0 && len2 != 0 && len1 != len2) throw new InvalidDataException("TERSE: ambiguous record length.");
        if ((flags & 0x04) == 0 && (flags != 0 || ratio != 0 || blockSize != 0))
          throw new InvalidDataException("TERSE: MVS fields set on a non-MVS header.");
        return new(true, data[0] == 0x05, variable == 1, len1 != 0 ? len1 : len2, 12);
      }
      default:
        throw new InvalidDataException($"TERSE: header version {data[0]:X2} not recognised.");
    }
  }

  /// <summary>Decodes a complete TERSE file to its binary-mode content.</summary>
  /// <exception cref="InvalidDataException">The header or the code stream is malformed.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    var header = ReadHeader(data);
    var sink = new RecordSink(header.IsVariable, header.RecordLength);
    var codes = new CodeReader(data[header.HeaderLength..]);
    if (header.IsSpack) DecodeSpack(ref codes, sink);
    else DecodePack(ref codes, sink);
    return sink.Finish();
  }

  // ── output ───────────────────────────────────────────────────────────

  /// <summary>TerseDecompress's binary-mode PutChar/endRecord.</summary>
  private sealed class RecordSink(bool variable, int recordLength) {
    private readonly MemoryStream _output = new();
    private readonly MemoryStream _record = new(Math.Max(16, Math.Min(recordLength, 1 << 16)));

    public void Put(int x) {
      if (x == 0) return;
      if (x == RecordMark) {
        if (variable) this.EndRecord();
        return; // fixed records: record marks are dropped
      }
      this._record.WriteByte((byte)(x - 1));
    }

    private void EndRecord() {
      if (variable) {
        Span<byte> rdw = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(rdw, ((int)this._record.Length + 4) << 16);
        this._output.Write(rdw);
      }
      this._record.Position = 0;
      this._record.CopyTo(this._output);
      this._record.SetLength(0);
    }

    public byte[] Finish() {
      if (this._record.Length > 0) this.EndRecord();
      return this._output.ToArray();
    }
  }

  /// <summary>12-bit codes, big-endian, two codes per three bytes.</summary>
  private ref struct CodeReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;
    private int _saved = -1;

    public int Next() {
      if (this._saved < 0) {
        if (this._pos >= this._data.Length) return EndOfFile;
        if (this._pos + 1 >= this._data.Length)
          throw new InvalidDataException("TERSE: data ends inside a 12-bit code.");
        int b1 = this._data[this._pos++], b2 = this._data[this._pos++];
        this._saved = b2 & 0x0F;
        return (b1 << 4) | (b2 >> 4);
      }
      if (this._pos >= this._data.Length) return EndOfFile; // the half byte was padding
      var code = (this._saved << 8) | this._data[this._pos++];
      this._saved = -1;
      return code;
    }
  }

  // ── PACK ─────────────────────────────────────────────────────────────

  /// <summary>Dictionary + LRU state shared by the PACK decoder and encoder.</summary>
  private sealed class PackState {
    public readonly int[] Father = new int[TreeSize];
    public readonly int[] CharExt = new int[TreeSize];
    public readonly int[] Backward = new int[TreeSize];
    public readonly int[] Forward = new int[TreeSize];
    public int X;

    public PackState() {
      // Nodes 258..4095 start as runs of EBCDIC spaces, each one longer than the last.
      var h2 = EbcdicSpaceCode;
      for (var h1 = 258; h1 < TreeSize; ++h1) {
        this.Father[h1] = h2;
        this.CharExt[h1] = EbcdicSpaceCode;
        h2 = h1;
      }
      for (var h1 = 258; h1 < TreeSize - 1; ++h1) {
        this.Backward[h1 + 1] = h1;
        this.Forward[h1] = h1 + 1;
      }
      this.Backward[0] = TreeSize - 1;
      this.Forward[0] = 258;
      this.Backward[258] = 0;
      this.Forward[TreeSize - 1] = 0;
    }

    /// <summary>
    /// Takes the least recently used node, consumes code <paramref name="d"/> (reporting
    /// the string's characters to <paramref name="emit"/>), and links the taken node as
    /// "string of d" + (next string's first character, filled in by the next step).
    /// </summary>
    public int Step(int d, Action<int>? emit, out int recycled) {
      var y = this.Backward[0];
      var q = this.Backward[y];
      this.Backward[0] = q;
      this.Forward[q] = 0;
      var h = y;
      var p = 0;
      var depth = 0;
      while (d > 257) {
        if (++depth > TreeSize) throw new InvalidDataException("TERSE: dictionary cycle.");
        q = this.Forward[d];
        var r = this.Backward[d];
        this.Forward[r] = q;
        this.Backward[q] = r;
        this.Forward[d] = h;
        this.Backward[h] = d;
        h = d;
        var e = this.Father[d];
        this.Father[d] = p;
        p = d;
        d = e;
      }
      q = this.Forward[0];
      this.Forward[y] = q;
      this.Backward[q] = y;
      this.Forward[0] = h;
      this.Backward[h] = 0;
      this.CharExt[this.X] = d;
      var root = d;
      emit?.Invoke(d);
      this.X = y;
      while (p != 0) {
        var e = this.Father[p];
        emit?.Invoke(this.CharExt[p]);
        this.Father[p] = d;
        d = p;
        p = e;
      }
      this.Father[y] = d;
      recycled = y;
      return root;
    }
  }

  private static void DecodePack(ref CodeReader codes, RecordSink sink) {
    var state = new PackState();
    Action<int> put = sink.Put;
    var d = codes.Next();
    while (d != EndOfFile) {
      if (d >= TreeSize) throw new InvalidDataException("TERSE: code out of range.");
      state.Step(d, put, out _);
      d = codes.Next();
    }
  }

  /// <summary>
  /// Encodes <paramref name="data"/> as a host-header PACK file holding one fixed-length
  /// record (RECFM=FB, LRECL = the data length), the shape AMATERSE gives a binary dataset.
  /// </summary>
  public static byte[] EncodePack(ReadOnlySpan<byte> data) {
    var state = new PackState();
    // (father, charCode) -> node, mirroring the decoder's pointer structure.
    var children = new Dictionary<int, int>(TreeSize * 2);
    static int Key(int father, int ch) => (father << 9) | ch;
    var owner = new int[TreeSize]; // key currently registered for each node, or -1
    Array.Fill(owner, -1);
    for (var n = 258; n < TreeSize; ++n) {
      var k = Key(state.Father[n], state.CharExt[n]);
      children[k] = n;
      owner[n] = k;
    }

    var output = new List<byte>(data.Length + 16) { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    var lrecl = Math.Max(1, data.Length);
    output.AddRange([(byte)(lrecl >> 24), (byte)(lrecl >> 16), (byte)(lrecl >> 8), (byte)lrecl]);
    var pending = -1;
    void Put(int code) {
      if (pending < 0) {
        output.Add((byte)(code >> 4));
        pending = code & 0x0F;
      } else {
        output.Add((byte)((pending << 4) | (code >> 8)));
        output.Add((byte)code);
        pending = -1;
      }
    }

    var pos = 0;
    var previousNode = -1; // node taken last step; its extension is this step's first char
    while (pos < data.Length) {
      var first = data[pos] + 1;
      if (previousNode >= 0) {
        var k = Key(state.Father[previousNode], first);
        children[k] = previousNode;
        owner[previousNode] = k;
      }

      // Longest dictionary string at pos.
      var code = first;
      var length = 1;
      while (pos + length < data.Length &&
             children.TryGetValue(Key(code, data[pos + length] + 1), out var child)) {
        code = child;
        ++length;
      }

      // The node the decoder recycles now loses its old meaning.
      var lru = state.Backward[0];
      if (owner[lru] >= 0 && children.TryGetValue(owner[lru], out var mapped) && mapped == lru)
        children.Remove(owner[lru]);
      owner[lru] = -1;

      state.Step(code, null, out var taken);
      previousNode = taken;
      Put(code);
      pos += length;
    }
    Put(EndOfFile);
    if (pending >= 0) output.Add((byte)(pending << 4));
    return [.. output];
  }

  // ── SPACK ────────────────────────────────────────────────────────────

  private struct TreeRecord {
    public int Left, Right, NextCount, Back;
  }

  private static void DecodeSpack(ref CodeReader codes, RecordSink sink) {
    var tree = new TreeRecord[TreeSize + 1];
    for (var i = 0; i <= CodeSize; ++i) {
      tree[i].Left = None;
      tree[i].Right = i;
    }
    for (var i = CodeSize + 1; i <= TreeSize - 1; ++i) {
      tree[i].NextCount = i + 1;
      tree[i].Left = None;
      tree[i].Right = None;
    }
    tree[TreeSize].NextCount = None;
    tree[0].NextCount = 0;
    tree[0].Back = 0;
    for (var i = 1; i <= CodeSize; ++i) tree[i].NextCount = None;
    var treeAvail = CodeSize + 1;
    tree[TreeSize - 1].NextCount = None;

    var stack = new int[0x800];
    void PutChars(int x) {
      var head = 0;
      while (true) {
        while (x > CodeSize) {
          if (++head >= stack.Length) throw new InvalidDataException("TERSE: SPACK tree too deep.");
          stack[head] = tree[x].Right;
          x = tree[x].Left;
        }
        if (x < 0) throw new InvalidDataException("TERSE: SPACK code refers to an unused node.");
        sink.Put(x);
        if (head == 0) break;
        x = stack[head--];
      }
    }
    void LruAdd(int next) {
      var back = tree[0].Back;
      tree[next].NextCount = 0;
      tree[0].Back = next;
      tree[next].Back = back;
      tree[back].NextCount = next;
    }
    void DeleteRef(int dref) {
      if (tree[dref].NextCount == -1) LruAdd(dref);
      else ++tree[dref].NextCount;
    }
    void LruKill() {
      var p = tree[0].NextCount;
      var q = tree[p].NextCount;
      var r = tree[p].Back;
      tree[q].Back = r;
      tree[r].NextCount = q;
      DeleteRef(tree[p].Left);
      DeleteRef(tree[p].Right);
      tree[p].NextCount = treeAvail;
      treeAvail = p;
    }
    void BumpRef(int bref) {
      if (tree[bref].NextCount < 0) --tree[bref].NextCount;
      else {
        var forwards = tree[bref].NextCount;
        var prev = tree[bref].Back;
        tree[prev].NextCount = forwards;
        tree[forwards].Back = prev;
        tree[bref].NextCount = -1;
      }
    }

    var h = codes.Next();
    if (h == EndOfFile) return;
    if (h >= TreeSize) throw new InvalidDataException("TERSE: code out of range.");
    PutChars(h);
    var g = codes.Next();
    while (g != EndOfFile) {
      if (g >= TreeSize) throw new InvalidDataException("TERSE: code out of range.");
      if (treeAvail == None) LruKill();
      PutChars(g);
      var n = treeAvail;
      treeAvail = tree[n].NextCount;
      tree[n].Left = h;
      tree[n].Right = g;
      BumpRef(h);
      BumpRef(g);
      LruAdd(n);
      h = g;
      g = codes.Next();
    }
  }
}

/// <summary>TERSE PACK as a building block: host-header PACK in, binary content out.</summary>
public sealed class TersePackBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_TersePack";
  /// <inheritdoc/>
  public string DisplayName => "IBM TERSE PACK";
  /// <inheritdoc/>
  public string Description => "IBM TERSE PACK: 12-bit LZW with least-recently-used dictionary recycling";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => TerseCodec.EncodePack(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => TerseCodec.Decode(data);
}
