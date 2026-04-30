#pragma warning disable CS1591
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Compression.UI.Controls;

namespace Compression.UI.Views;

/// <summary>
/// Read-only IList&lt;HexLineViewModel&gt; that materialises rows on indexer access only.
/// Backing byte buffer is held by reference; no per-row state is allocated up front.
/// WPF's virtualized ListBox calls <see cref="this[int]"/> only for visible rows,
/// so opening a 1 GB binary in hex view is O(viewport) memory instead of O(file).
/// </summary>
internal sealed class LazyHexLines : IList<HexLineViewModel>, IList {
  private readonly byte[] _data;
  private readonly int _bytesPerRow;
  private readonly int _offsetWidth;

  public LazyHexLines(byte[] data, int bytesPerRow) {
    _data = data;
    _bytesPerRow = bytesPerRow > 0 ? bytesPerRow : 16;
    _offsetWidth = data.Length > 0xFFFFFF ? 8 : (data.Length > 0xFFFF ? 6 : 4);
    Count = (int)((data.LongLength + _bytesPerRow - 1) / _bytesPerRow);
  }

  public int Count { get; }
  public bool IsReadOnly => true;
  public bool IsFixedSize => true;
  public bool IsSynchronized => false;
  public object SyncRoot => this;

  public HexLineViewModel this[int index] {
    get {
      if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
      var offset = (long)index * _bytesPerRow;
      var count = (int)Math.Min((long)_bytesPerRow, _data.LongLength - offset);
      var bytes = new HexByte[count];
      for (var i = 0; i < count; i++) {
        var b = _data[offset + i];
        bytes[i] = new HexByte(b, b.ToString("X2"), b is >= 0x20 and < 0x7F ? (char)b : '.');
      }
      return new HexLineViewModel {
        OffsetText = offset.ToString($"X{_offsetWidth}"),
        Bytes = bytes,
        ByteCount = count,
        BytesPerRow = _bytesPerRow,
      };
    }
    set => throw new NotSupportedException();
  }

  // IList non-generic indexer (WPF binding sometimes hits this path).
  object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }

  public IEnumerator<HexLineViewModel> GetEnumerator() {
    for (var i = 0; i < Count; i++) yield return this[i];
  }
  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

  public bool Contains(HexLineViewModel item) => false;
  public int IndexOf(HexLineViewModel item) => -1;
  public void CopyTo(HexLineViewModel[] array, int arrayIndex) {
    for (var i = 0; i < Count; i++) array[arrayIndex + i] = this[i];
  }
  public void CopyTo(Array array, int index) {
    for (var i = 0; i < Count; i++) array.SetValue(this[i], index + i);
  }
  bool IList.Contains(object? value) => false;
  int IList.IndexOf(object? value) => -1;
  int IList.Add(object? value) => throw new NotSupportedException();
  void IList.Insert(int index, object? value) => throw new NotSupportedException();
  void IList.Remove(object? value) => throw new NotSupportedException();
  void IList.RemoveAt(int index) => throw new NotSupportedException();
  void IList.Clear() => throw new NotSupportedException();
  public void Add(HexLineViewModel item) => throw new NotSupportedException();
  public void Clear() => throw new NotSupportedException();
  public void Insert(int index, HexLineViewModel item) => throw new NotSupportedException();
  public bool Remove(HexLineViewModel item) => throw new NotSupportedException();
  public void RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>One decoded text line — the View binds <see cref="Text"/>.</summary>
internal sealed class TextLineViewModel {
  public string Text { get; init; } = "";
}

/// <summary>
/// Lazy line view over an arbitrary byte buffer for the text-mode preview.
/// Pre-scans newline offsets once (O(n) single pass, fast even for hundreds
/// of MB), inserting soft breaks every <see cref="ChunkBytes"/> bytes so
/// no-newline binary blobs don't materialise as one giant line. Each row
/// is decoded on indexer access via the supplied <see cref="Encoding"/>.
/// </summary>
internal sealed class LazyTextLines : IList<TextLineViewModel>, IList {
  private const int ChunkBytes = 4096;
  private readonly byte[] _data;
  private readonly Encoding _encoding;
  // _starts[i] = byte offset of line i. _starts[Count] = _data.Length (sentinel for length calc).
  private readonly long[] _starts;

  public LazyTextLines(byte[] data, Encoding encoding) {
    _data = data;
    _encoding = encoding;
    _starts = ScanLineStarts(data);
    Count = _starts.Length - 1;
  }

  private static long[] ScanLineStarts(byte[] data) {
    var starts = new List<long> { 0 };
    long lastBreak = 0;
    for (long i = 0; i < data.LongLength; i++) {
      var b = data[i];
      if (b == (byte)'\n') {
        starts.Add(i + 1);
        lastBreak = i + 1;
      }
      else if (i - lastBreak >= ChunkBytes) {
        // Force a soft break to keep individual rows bounded for no-newline blobs.
        starts.Add(i);
        lastBreak = i;
      }
    }
    starts.Add(data.LongLength);
    return [.. starts];
  }

  public int Count { get; }
  public bool IsReadOnly => true;
  public bool IsFixedSize => true;
  public bool IsSynchronized => false;
  public object SyncRoot => this;

  public TextLineViewModel this[int index] {
    get {
      if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
      var start = _starts[index];
      var end = _starts[index + 1];
      var len = (int)(end - start);
      // Strip trailing \r\n for display.
      if (len > 0 && _data[start + len - 1] == (byte)'\n') len--;
      if (len > 0 && _data[start + len - 1] == (byte)'\r') len--;
      var text = len > 0 ? _encoding.GetString(_data, (int)start, len) : "";
      return new TextLineViewModel { Text = text };
    }
    set => throw new NotSupportedException();
  }

  object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }

  public IEnumerator<TextLineViewModel> GetEnumerator() {
    for (var i = 0; i < Count; i++) yield return this[i];
  }
  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

  public bool Contains(TextLineViewModel item) => false;
  public int IndexOf(TextLineViewModel item) => -1;
  public void CopyTo(TextLineViewModel[] array, int arrayIndex) {
    for (var i = 0; i < Count; i++) array[arrayIndex + i] = this[i];
  }
  public void CopyTo(Array array, int index) {
    for (var i = 0; i < Count; i++) array.SetValue(this[i], index + i);
  }
  bool IList.Contains(object? value) => false;
  int IList.IndexOf(object? value) => -1;
  int IList.Add(object? value) => throw new NotSupportedException();
  void IList.Insert(int index, object? value) => throw new NotSupportedException();
  void IList.Remove(object? value) => throw new NotSupportedException();
  void IList.RemoveAt(int index) => throw new NotSupportedException();
  void IList.Clear() => throw new NotSupportedException();
  public void Add(TextLineViewModel item) => throw new NotSupportedException();
  public void Clear() => throw new NotSupportedException();
  public void Insert(int index, TextLineViewModel item) => throw new NotSupportedException();
  public bool Remove(TextLineViewModel item) => throw new NotSupportedException();
  public void RemoveAt(int index) => throw new NotSupportedException();
}
