using System.Buffers.Binary;
using System.Text;

namespace FileFormat.PyInstaller;

/// <summary>
/// A minimal reader for the subset of Python's <c>marshal</c> serialization format
/// used by a PYZ archive's table of contents. It decodes lists, tuples, integers,
/// and (interned/short/long) string objects — enough to enumerate the embedded
/// module names — and honours the marshal reference table (FLAG_REF / TYPE_REF).
/// Unknown type codes stop the parse gracefully rather than throwing.
/// </summary>
internal sealed class MarshalReader {
  private const int FlagRef = 0x80;

  private readonly byte[] _data;
  private int _pos;
  private readonly List<object?> _refs = [];

  public MarshalReader(byte[] data) => this._data = data;

  /// <summary>Reads a single marshalled object from the current position.</summary>
  public object? ReadObject() {
    if (this._pos >= this._data.Length)
      return null;

    var code = this._data[this._pos++];
    var flag = (code & FlagRef) != 0;
    var type = (char)(code & ~FlagRef);

    // Reserve a reference slot before reading children so a nested TYPE_REF that
    // points back at this object resolves (even if only to a placeholder).
    var refIndex = -1;
    if (flag) {
      refIndex = this._refs.Count;
      this._refs.Add(null);
    }

    var result = this.ReadValue(type);

    if (refIndex >= 0)
      this._refs[refIndex] = result;

    return result;
  }

  private object? ReadValue(char type) {
    switch (type) {
      case '0': // TYPE_NULL
      case 'N': // TYPE_NONE
      case '.': // TYPE_STOPITER / sentinel
        return null;
      case 'F': // TYPE_FALSE
        return false;
      case 'T': // TYPE_TRUE
        return true;
      case 'i': // TYPE_INT
        return (long)this.ReadInt32();
      case 'I': // TYPE_INT64
        return this.ReadInt64();
      case 'z': // TYPE_SHORT_ASCII
      case 'Z': // TYPE_SHORT_ASCII_INTERNED
        return this.ReadString(this.ReadByte());
      case 'a': // TYPE_ASCII
      case 'A': // TYPE_ASCII_INTERNED
      case 'u': // TYPE_UNICODE
      case 't': // TYPE_INTERNED
      case 's': // TYPE_STRING (bytes)
        return this.ReadString(this.ReadInt32());
      case ')': // TYPE_SMALL_TUPLE
        return this.ReadSequence(this.ReadByte()).ToArray();
      case '(': // TYPE_TUPLE
        return this.ReadSequence(this.ReadInt32()).ToArray();
      case '[': // TYPE_LIST
        return this.ReadSequence(this.ReadInt32());
      case 'r': // TYPE_REF
      case 'R': {
        var i = this.ReadInt32();
        return i >= 0 && i < this._refs.Count ? this._refs[i] : null;
      }
      default:
        // Unknown / unsupported type — stop cleanly.
        this._pos = this._data.Length;
        return null;
    }
  }

  private List<object?> ReadSequence(int count) {
    var list = new List<object?>(count < 0 ? 0 : Math.Min(count, this._data.Length));
    for (var i = 0; i < count && this._pos < this._data.Length; i++)
      list.Add(this.ReadObject());
    return list;
  }

  private byte ReadByte() {
    if (this._pos >= this._data.Length)
      throw new InvalidDataException("Truncated marshal stream.");
    return this._data[this._pos++];
  }

  private int ReadInt32() {
    var span = this.Take(4);
    return BinaryPrimitives.ReadInt32LittleEndian(span);
  }

  private long ReadInt64() {
    var span = this.Take(8);
    return BinaryPrimitives.ReadInt64LittleEndian(span);
  }

  private string ReadString(int length) {
    if (length < 0)
      return "";
    var span = this.Take(length);
    return Encoding.UTF8.GetString(span);
  }

  private ReadOnlySpan<byte> Take(int n) {
    if (n < 0 || this._pos + n > this._data.Length)
      throw new InvalidDataException("Truncated marshal stream.");
    var span = this._data.AsSpan(this._pos, n);
    this._pos += n;
    return span;
  }
}
