using System.Buffers.Binary;

namespace FileFormat.Dar;

/// <summary>
/// Where a DAR writer puts the archive-level byte stream: the optional escape layer (tape marks)
/// on top of the slice layer (<c>docs/DAR-ON-DISK.md</c>, sections 3 and 6). Bytes go straight
/// through to the current slice; nothing is buffered beyond what the slice stream itself keeps,
/// so an archive of any size is written in constant memory.
/// </summary>
internal sealed class DarArchiveSink : IDisposable {
  private readonly Func<int, Stream> _openSlice;
  private readonly byte[] _sliceHeader;
  private readonly long _firstCapacity;
  private readonly long _capacity;
  private readonly bool _escape;
  private readonly List<Stream> _opened = [];
  private Stream? _slice;
  private long _room;
  private int _matched;
  private bool _finished;

  /// <param name="openSlice">Opens slice <c>n</c> (1-based) for writing. Slice 1 is opened at once;
  /// the others only when data spills into them. Streams for slices after the first are disposed by
  /// the sink; slice 1 belongs to the caller.</param>
  /// <param name="name">The 10-byte internal name and data name every slice header carries.</param>
  /// <param name="sliceSize">Size of every slice file, header and trailer included; 0 writes one slice of any size.</param>
  /// <param name="firstSliceSize">Size of the first slice when it differs from the others; 0 when it does not.</param>
  /// <param name="escape">Whether data passes through the escape layer (tape marks).</param>
  public DarArchiveSink(Func<int, Stream> openSlice, byte[] name, long sliceSize, long firstSliceSize, bool escape) {
    this._openSlice = openSlice;
    this._escape = escape;
    this._sliceHeader = BuildSliceHeader(name, sliceSize, firstSliceSize);
    if (sliceSize <= 0) {
      this._firstCapacity = this._capacity = long.MaxValue;
    } else {
      // Payload room per slice: the slice file minus its header and its one-byte trailer.
      this._capacity = sliceSize - this._sliceHeader.Length - 1;
      this._firstCapacity = (firstSliceSize > 0 ? firstSliceSize : sliceSize) - this._sliceHeader.Length - 1;
      if (this._capacity < 1 || this._firstCapacity < 1)
        throw new ArgumentOutOfRangeException(nameof(sliceSize),
          $"DAR slices must be larger than their {this._sliceHeader.Length + 1}-byte header and trailer.");
    }
    this.OpenNextSlice();
  }

  /// <summary>Bytes the slice header takes for the given slicing; the same on every slice.</summary>
  public static int SliceHeaderLength(long sliceSize, long firstSliceSize)
    => BuildSliceHeader(new byte[DarSliceHeader.NameLength], sliceSize, firstSliceSize).Length;

  /// <summary>The archive-level offset of the next byte: payload bytes written so far, escapes included.</summary>
  public long Position { get; private set; }

  /// <summary>How many slice files have been started.</summary>
  public int SliceCount { get; private set; }

  /// <summary>Writes archive data. In tape-mark mode every occurrence of the escape prefix is
  /// followed by <c>'X'</c>, which the reader drops again.</summary>
  public void Data(ReadOnlySpan<byte> data) {
    if (!this._escape) {
      this.Raw(data);
      return;
    }
    var prefix = DarPrimitives.EscapePrefix;
    var start = 0;
    for (var i = 0; i < data.Length; ++i) {
      var b = data[i];
      if (b == prefix[this._matched]) {
        if (++this._matched < prefix.Length)
          continue;
        this.RawBytes(data[start..(i + 1)]);
        this.RawBytes([DarPrimitives.NotAMark]);
        start = i + 1;
        this._matched = 0;
      } else
        // The prefix bytes are all distinct, so a mismatch can only restart a match on its first byte.
        this._matched = b == prefix[0] ? 1 : 0;
    }
    this.RawBytes(data[start..]);
  }

  /// <summary>Writes an escape mark of the given type (section 6).</summary>
  public void Mark(byte type) {
    this._matched = 0;
    Span<byte> mark = stackalloc byte[6];
    DarPrimitives.EscapePrefix.CopyTo(mark);
    mark[5] = type;
    this.RawBytes(mark);
  }

  /// <summary>Writes bytes that bypass the escape layer: the terminators and the version trailer.</summary>
  public void Raw(ReadOnlySpan<byte> data) {
    this._matched = 0;
    this.RawBytes(data);
  }

  /// <summary>Ends the last slice with the trailer <c>'T'</c>.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._slice!.WriteByte((byte)'T');
    this._slice.Flush();
    this._finished = true;
  }

  /// <inheritdoc/>
  public void Dispose() {
    foreach (var stream in this._opened)
      stream.Dispose();
    this._opened.Clear();
  }

  private void RawBytes(ReadOnlySpan<byte> data) {
    while (!data.IsEmpty) {
      if (this._room == 0)
        this.OpenNextSlice();
      var n = (int)Math.Min(this._room, data.Length);
      this._slice!.Write(data[..n]);
      this._room -= n;
      this.Position += n;
      data = data[n..];
    }
  }

  private void OpenNextSlice() {
    if (this._slice != null) {
      this._slice.WriteByte((byte)'N');
      this._slice.Flush();
      if (this.SliceCount > 1) {
        this._slice.Dispose();
        this._opened.Remove(this._slice);
      }
    }
    ++this.SliceCount;
    var slice = this._openSlice(this.SliceCount);
    if (this.SliceCount > 1)
      this._opened.Add(slice);
    slice.Write(this._sliceHeader);
    this._slice = slice;
    this._room = this.SliceCount == 1 ? this._firstCapacity : this._capacity;
  }

  /// <summary>
  /// The slice header (section 3): magic, name, flag, extension <c>'T'</c> and the TLV list. One
  /// unsliced file says <c>'T'</c> (last slice) and carries the data name only; a sliced set says
  /// <c>'E'</c> on every slice, lets the trailer decide, and records the slice sizes first, the
  /// first-slice size ahead of the others when it differs — dar's order.
  /// </summary>
  private static byte[] BuildSliceHeader(byte[] name, long sliceSize, long firstSliceSize) {
    using var ms = new MemoryStream();
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(u32, DarSliceHeader.SliceMagic);
    ms.Write(u32);
    ms.Write(name);
    ms.WriteByte(sliceSize > 0 ? (byte)'E' : (byte)'T');
    ms.WriteByte((byte)'T');
    var tlvs = new List<(ushort Type, byte[] Value)>();
    if (sliceSize > 0) {
      if (firstSliceSize > 0 && firstSliceSize != sliceSize)
        tlvs.Add((DarSliceHeader.TlvFirstSliceSize, Infinint((ulong)firstSliceSize)));
      tlvs.Add((DarSliceHeader.TlvSliceSize, Infinint((ulong)sliceSize)));
    }
    tlvs.Add((DarSliceHeader.TlvDataName, name));
    DarPrimitives.WriteInfinint(ms, (ulong)tlvs.Count);
    Span<byte> u16 = stackalloc byte[2];
    foreach (var (type, value) in tlvs) {
      BinaryPrimitives.WriteUInt16BigEndian(u16, type);
      ms.Write(u16);
      DarPrimitives.WriteInfinint(ms, (ulong)value.Length);
      ms.Write(value);
    }
    return ms.ToArray();

    static byte[] Infinint(ulong v) {
      using var b = new MemoryStream();
      DarPrimitives.WriteInfinint(b, v);
      return b.ToArray();
    }
  }
}
