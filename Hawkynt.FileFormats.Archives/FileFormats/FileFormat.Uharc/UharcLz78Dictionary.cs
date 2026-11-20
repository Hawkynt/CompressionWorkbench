namespace FileFormat.Uharc;

/// <summary>
/// The phrase dictionary of UHARC's LZ78 stream (docs/UHARC-ON-DISK.md, "LZ78"), shared by the
/// decoder and the encoder so both number, extend and prune phrases identically.
/// </summary>
internal sealed class UharcLz78Dictionary {

  /// <summary>No phrase: the parent of a root, the previous code after a reset or a pruning.</summary>
  public const int None = 0x100;
  public const int ResetCode = 0x100;
  public const int RunCode = 0x101;
  public const int Roots = 0x102;

  // Per phrase: last byte, parent phrase (or a sibling link while pruning), first child while pruning.
  private readonly byte[] _last;
  private readonly int[] _parent;
  private readonly int[] _child;
  private readonly byte[] _scratch;

  public UharcLz78Dictionary(int method) {
    if (method is < UharcMethods.Lz78First or > UharcMethods.Lz78Last)
      throw new ArgumentOutOfRangeException(nameof(method));

    this.Capacity = 1 << (method + 8);
    this._last = new byte[this.Capacity];
    this._parent = new int[this.Capacity];
    this._child = new int[this.Capacity];
    this._scratch = new byte[this.Capacity];
    this.Reset();
  }

  /// <summary>Number of phrase codes, <c>2^(method+8)</c>.</summary>
  public int Capacity { get; }

  /// <summary>The next phrase number; codes up to it are valid.</summary>
  public int Next { get; private set; }

  /// <summary>Current code width in bits.</summary>
  public int Width { get; private set; }

  /// <summary>The previous code, or <see cref="None"/> right after a reset or a pruning.</summary>
  public int Previous { get; private set; }

  /// <summary>Last byte of phrase <paramref name="code"/>.</summary>
  public byte LastByte(int code) => this._last[code];

  /// <summary>Parent of phrase <paramref name="code"/>, <see cref="None"/> for a byte.</summary>
  public int Parent(int code) => this._parent[code];

  /// <summary>Empties the dictionary: only the 256 bytes remain.</summary>
  public void Reset() {
    for (var i = 0; i < Roots; ++i) {
      this._child[i] = None;
      this._parent[i] = None;
      this._last[i] = (byte)i;
    }

    this.Next = RunCode;
    this.Previous = None;
    this.Width = 9;
  }

  /// <summary>
  /// The bytes <paramref name="code"/> stands for: its phrase, or for the code <see cref="Next"/>
  /// the previous phrase plus its own first byte. The span is valid until the next call.
  /// </summary>
  public ReadOnlySpan<byte> Expand(int code) {
    var known = code < this.Next || this.Previous == None;
    var phrase = known ? code : this.Previous;
    var start = this.Capacity;
    var node = phrase;
    var guard = 0;
    while (this._parent[node] != None) {
      if (start <= 1 || ++guard > this.Capacity)
        throw new InvalidDataException("UHARC: LZ78 phrase chain is corrupt.");
      this._scratch[--start] = this._last[node];
      node = this._parent[node];
    }

    var first = this._last[node];
    this._scratch[--start] = first;
    if (known)
      return this._scratch.AsSpan(start);

    // code == Next: the previous phrase and its own first byte; the scratch has room in front.
    var length = this.Capacity - start;
    this._scratch.AsSpan(start, length).CopyTo(this._scratch.AsSpan(start - 1));
    this._scratch[this.Capacity - 1] = first;
    return this._scratch.AsSpan(start - 1, length + 1);
  }

  /// <summary>
  /// Accounts for phrase <paramref name="code"/> whose first byte is <paramref name="first"/>:
  /// extends the previous phrase by it, advances the numbering and prunes a full dictionary.
  /// </summary>
  /// <returns><see langword="true"/> when the dictionary was pruned.</returns>
  public bool Advance(int code, byte first) {
    var next = this.Next;
    if (this.Previous != None && next < this.Capacity) {
      this._child[next] = None;
      this._last[next] = first;
      this._parent[next] = this.Previous;
    }

    ++next;
    if (1 << this.Width == next)
      ++this.Width;
    this.Previous = code;
    if (next != this.Capacity) {
      this.Next = next;
      return false;
    }

    next = this.Prune();
    while (this.Width > 1 && 1 << (this.Width - 1) >= next)
      --this.Width;
    this.Previous = None;
    if (next > 0)
      --next;
    this.Next = next;
    return true;
  }

  /// <summary>
  /// Drops every phrase that is no other phrase's prefix and renumbers the rest; returns the
  /// first free code. During this step <see cref="_child"/> holds a first-child link (0 = free,
  /// <see cref="None"/> = none) and <see cref="_parent"/> a sibling link.
  /// </summary>
  private int Prune() {
    var free = this.Capacity;
    for (var v = this.Capacity - 1; v >= Roots; --v) {
      var parent = this._parent[v];
      if (parent < this.Capacity && this._child[parent] == None)
        this._child[parent] = 0;

      if (this._child[v] == None) {
        this._child[v] = 0;
        free = Math.Min(free, v);
      } else {
        if (this._child[v] == 0)
          this._child[v] = None;
        if (parent < this.Capacity) {
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
    for (var i = 0; i < free && i < this.Capacity && free <= this.Capacity; ++i) {
      this.Relocate(ref this._child[i], ref free);
      this.Relocate(ref this._parent[i], ref free);
    }

    // Child and sibling lists back to parent links.
    for (var p = 0; p < free; ++p) {
      var current = this._child[p];
      this._child[p] = None;
      var guard = 0;
      while (current != None && current < this.Capacity && guard++ <= this.Capacity + 16) {
        var sibling = this._parent[current];
        this._parent[current] = p;
        current = sibling;
      }
    }

    return free;
  }

  private void Relocate(ref int link, ref int free) {
    var target = link;
    if (target == None || target <= free || target >= this.Capacity || free >= this.Capacity)
      return;

    this._child[free] = this._child[target];
    this._parent[free] = this._parent[target];
    this._last[free] = this._last[target];
    link = free;
    this._child[target] = 0;
    do
      ++free;
    while (free < this.Capacity && this._child[free] != 0);
  }
}
