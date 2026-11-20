using System.Buffers.Binary;
using System.Text;

namespace FileFormat.InnoSetup;

/// <summary>
/// Little-endian reader over a decoded setup-0 block, with the Inno Setup primitives:
/// length-prefixed strings, one-byte enums and packed flag sets.
/// </summary>
/// <remarks>
/// Values outside an enum's range and flag bits beyond the known ones are counted in
/// <see cref="Warnings"/>; a header that parses with warnings is the signal (as in innoextract)
/// that a different layout of an ambiguous version should be tried.
/// </remarks>
internal sealed class InnoCursor(byte[] data) {
  private int _pos;

  public int Warnings { get; set; }

  /// <summary>Gets or sets whether three-byte flag sets carry a padding byte (32-bit installers; 16-bit ones do not).</summary>
  public bool PadFlagSets { get; init; } = true;

  /// <summary>Gets or sets the width of a Delphi <c>Integer</c>: 2 bytes in 16-bit installers, 4 otherwise.</summary>
  public int IntegerSize { get; init; } = 4;

  /// <summary>A Delphi <c>Integer</c>: 16 or 32 bits wide depending on the installer (<see cref="IntegerSize"/>).</summary>
  public int Int() => this.IntegerSize == 2 ? this.I16() : this.I32();

  /// <summary>An unsigned Delphi <c>Integer</c>-sized value: 16 or 32 bits wide depending on the installer.</summary>
  public uint UInt() => this.IntegerSize == 2 ? this.U16() : this.U32();
  public int Position => this._pos;
  public bool AtEnd => this._pos >= data.Length;

  private ReadOnlySpan<byte> Take(int n) {
    if (n < 0 || this._pos + n > data.Length)
      throw new InvalidDataException("Inno Setup header data ended early.");
    var span = data.AsSpan(this._pos, n);
    this._pos += n;
    return span;
  }

  public byte U8() => this.Take(1)[0];
  public bool Bool() => this.U8() != 0;
  public short I16() => BinaryPrimitives.ReadInt16LittleEndian(this.Take(2));
  public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(this.Take(2));
  public int I32() => BinaryPrimitives.ReadInt32LittleEndian(this.Take(4));
  public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(this.Take(4));
  public long I64() => BinaryPrimitives.ReadInt64LittleEndian(this.Take(8));
  public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(this.Take(8));
  public byte[] Bytes(int n) => this.Take(n).ToArray();
  public void Skip(int n) => this.Take(n);

  /// <summary>A 32-bit length followed by that many bytes.</summary>
  public byte[] BinString() {
    var n = this.I32();
    if (n < 0)
      throw new InvalidDataException("Negative Inno Setup string length.");
    return this.Bytes(n);
  }

  public void SkipString() => this.Skip(this.CheckedLength());

  private int CheckedLength() {
    var n = this.I32();
    if (n < 0)
      throw new InvalidDataException("Negative Inno Setup string length.");
    return n;
  }

  /// <summary>A string in the installer's text encoding (UTF-16LE for Unicode installers).</summary>
  public string Text(Encoding encoding) => encoding.GetString(this.BinString());

  /// <summary>A one-byte enum with <paramref name="count"/> known values.</summary>
  public int Enum(int count) {
    var v = this.U8();
    if (v >= count)
      ++this.Warnings;
    return v;
  }

  /// <summary>
  /// A set of <paramref name="count"/> flags known at compile time: one byte per eight flags,
  /// with three-byte sets padded to four.
  /// </summary>
  public ulong Flags(int count, bool? pad32 = null) {
    var bytes = (count + 7) / 8;
    ulong value = 0;
    for (var i = 0; i < bytes; ++i)
      value |= (ulong)this.U8() << (8 * i);
    if (bytes == 3 && (pad32 ?? this.PadFlagSets))
      this.U8();
    if (count < 64 && (value >> count) != 0)
      ++this.Warnings;
    return value;
  }

  /// <summary>Starts reading a flag set whose members depend on the version.</summary>
  public FlagReader BeginFlags(bool? pad32 = null) => new(this, pad32 ?? this.PadFlagSets);

  /// <summary>Reads the flags of a version-dependent set one declaration at a time.</summary>
  internal sealed class FlagReader(InnoCursor cursor, bool pad32) {
    private int _bit;
    private int _bytes;
    private byte _current;

    /// <summary>Declares the next flag and returns whether it is set.</summary>
    public bool Next() {
      if (this._bit == 0) {
        this._current = cursor.U8();
        ++this._bytes;
      }
      var set = (this._current & (1 << this._bit)) != 0;
      this._bit = (this._bit + 1) & 7;
      return set;
    }

    /// <summary>Declares a flag only when <paramref name="present"/>; absent flags read as false.</summary>
    public bool NextIf(bool present) => present && this.Next();

    /// <summary>Finishes the set (three-byte sets carry a padding byte).</summary>
    public void End() {
      if (this._bytes == 3 && pad32)
        cursor.U8();
    }
  }
}
