namespace FileFormat.Uharc;

/// <summary>
/// UHARC's LZ78 stream (methods 2..7, <c>-mw</c>), as described in docs/UHARC-ON-DISK.md section
/// "LZ78": an LZW dictionary of <c>2^(method+8)</c> phrases, MSB-first codes in truncated binary,
/// a literal/phrase flag once codes are wider than 9 bits, a code for long byte runs and a
/// pruning step when the dictionary fills.
/// </summary>
internal sealed class UharcLz78Decoder {

  private const int None = 0x100;
  private const int ResetCode = 0x100;
  private const int RunCode = 0x101;
  private const int Roots = 0x102;

  private readonly byte[] _source;
  private readonly int _end;
  private int _position;
  private uint _bitBuffer;
  private int _bitCount;

  // Per phrase: last byte, parent phrase (or a sibling link while pruning), first child while pruning.
  private readonly byte[] _last;
  private readonly int[] _parent;
  private readonly int[] _child;
  private readonly byte[] _scratch;
  private readonly int _capacity;

  private UharcLz78Decoder(byte[] source, int offset, int length, int method) {
    this._source = source;
    this._position = offset;
    this._end = offset + length;
    this._capacity = 1 << (method + 8);
    this._last = new byte[this._capacity];
    this._parent = new int[this._capacity];
    this._child = new int[this._capacity];
    this._scratch = new byte[this._capacity];
  }

  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize) {
    if (method is < UharcMethods.Lz78First or > UharcMethods.Lz78Last)
      throw new ArgumentOutOfRangeException(nameof(method));

    return new UharcLz78Decoder(packed, offset, length, method).Run(unpackedSize);
  }

  private uint Bits(int count) {
    if (count == 0)
      return 0;

    while (count > this._bitCount) {
      if (this._position >= this._end)
        throw new InvalidDataException("UHARC: LZ78 stream is truncated.");
      this._bitBuffer = (this._bitBuffer << 8) | this._source[this._position++];
      this._bitCount += 8;
    }

    this._bitCount -= count;
    return (this._bitBuffer >> this._bitCount) & (uint)((1L << count) - 1);
  }

  private void ResetDictionary() {
    for (var i = 0; i < Roots; ++i) {
      this._child[i] = None;
      this._parent[i] = None;
      this._last[i] = (byte)i;
    }
  }

  private byte[] Run(int unpackedSize) {
    var output = new byte[unpackedSize];
    var written = 0;
    this.ResetDictionary();
    var next = RunCode;
    var previous = None;
    uint literalCredit = 0;
    var width = 9;

    while (written < unpackedSize) {
      // Codes are truncated binary over [0, next]: the top values carry one extra bit.
      int code;
      var span = 1 << width;
      var threshold = span - (next + 1);
      if (literalCredit != 0 && width != 9) {
        if (this.Bits(1) != 0) {
          var v = (int)this.Bits(width - 1);
          if (v + 1 >= threshold && this.Bits(1) != 0)
            v = span - 1 - v;
          literalCredit >>= 1;
          code = v + 256;
        } else {
          code = (int)this.Bits(8);
          literalCredit += 15;
        }
      } else {
        var v = (int)this.Bits(width - 1);
        if (v + 1 >= threshold && this.Bits(1) != 0)
          v = span - 1 - v;
        if (v < 256 && width != 9)
          literalCredit = 1;
        code = v;
      }

      if (code > next)
        continue;

      if (code == ResetCode) {
        this.ResetDictionary();
        next = RunCode;
        previous = None;
        literalCredit = 0;
        width = 9;
        continue;
      }

      if (code == RunCode) {
        long count;
        if (this.Bits(1) != 0)
          count = this.Bits(8);
        else if (this.Bits(1) != 0)
          count = this.Bits(12) + 0x100L;
        else if (this.Bits(1) != 0)
          count = this.Bits(16) + 0x1100L;
        else
          count = this.Bits(16) + ((long)this.Bits(16) << 16) + 0x1100L;
        count += 0x100;
        var value = (byte)this.Bits(8);
        if (count > unpackedSize - written)
          throw new InvalidDataException("UHARC: LZ78 run passes the end of the stream.");
        output.AsSpan(written, (int)count).Fill(value);
        written += (int)count;
        continue;
      }

      var known = code < next || previous == None;
      var phrase = known ? code : previous;
      var start = this._capacity;
      var node = phrase;
      var guard = 0;
      while (this._parent[node] != None) {
        if (start == 0 || ++guard > this._capacity)
          throw new InvalidDataException("UHARC: LZ78 phrase chain is corrupt.");
        this._scratch[--start] = this._last[node];
        node = this._parent[node];
      }

      var first = this._last[node];
      this._scratch[--start] = first;
      var length = this._capacity - start + (known ? 0 : 1);
      if (length > unpackedSize - written)
        throw new InvalidDataException("UHARC: LZ78 phrase passes the end of the stream.");
      this._scratch.AsSpan(start).CopyTo(output.AsSpan(written));
      written += this._capacity - start;
      if (!known)
        output[written++] = first;

      if (previous != None && next < this._capacity) {
        this._child[next] = None;
        this._last[next] = first;
        this._parent[next] = previous;
      }

      ++next;
      if (1 << width == next)
        ++width;
      previous = code;
      if (next == this._capacity) {
        next = this.Prune();
        while (width > 1 && 1 << (width - 1) >= next)
          --width;
        previous = None;
        if (next > 0)
          --next;
      }
    }

    return output;
  }

  /// <summary>
  /// Drops every phrase that is no other phrase's prefix and renumbers the rest; returns the
  /// first free code. During this step <see cref="_child"/> holds a first-child link (0 = free,
  /// <see cref="None"/> = none) and <see cref="_parent"/> a sibling link.
  /// </summary>
  private int Prune() {
    var free = this._capacity;
    for (var v = this._capacity - 1; v >= Roots; --v) {
      var parent = this._parent[v];
      if (parent < this._capacity && this._child[parent] == None)
        this._child[parent] = 0;

      if (this._child[v] == None) {
        this._child[v] = 0;
        free = Math.Min(free, v);
      } else {
        if (this._child[v] == 0)
          this._child[v] = None;
        if (parent < this._capacity) {
          if (this._child[parent] == 0)
            this._child[parent] = None;
          this._parent[v] = this._child[parent];
          this._child[parent] = v;
        }
      }
    }

    for (var i = 0; i < Roots; ++i)
      if (this._child[i] == 0)
        this._child[i] = None;

    // Pull the surviving phrases down into the freed codes, in list order.
    for (var i = 0; i < free && i < this._capacity && free <= this._capacity; ++i) {
      this.Relocate(ref this._child[i], ref free);
      this.Relocate(ref this._parent[i], ref free);
    }

    // Child and sibling lists back to parent links.
    for (var p = 0; p < free; ++p) {
      var current = this._child[p];
      this._child[p] = None;
      var guard = 0;
      while (current != None && current < this._capacity && guard++ <= this._capacity + 16) {
        var sibling = this._parent[current];
        this._parent[current] = p;
        current = sibling;
      }
    }

    return free;
  }

  private void Relocate(ref int link, ref int free) {
    var target = link;
    if (target == None || target <= free || target >= this._capacity || free >= this._capacity)
      return;

    this._child[free] = this._child[target];
    this._parent[free] = this._parent[target];
    this._last[free] = this._last[target];
    link = free;
    this._child[target] = 0;
    do
      ++free;
    while (free < this._capacity && this._child[free] != 0);
  }
}
