#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Onnx;

/// <summary>
/// Minimal read-only Protocol Buffers (proto3) decoder. Handles the four
/// wire types relevant to ONNX: <c>Varint</c>, <c>Fixed64</c>, <c>LengthDelimited</c>,
/// and <c>Fixed32</c>. Group wire types (3 and 4) are deprecated in proto3 and
/// not emitted by the ONNX serializer, but we tolerate them during a scan.
/// </summary>
/// <remarks>
/// Pure read-only, no allocations for scalar reads. Advancing past truncated
/// tails sets <see cref="AtEnd"/>; callers check this after each <see cref="ReadTag"/>.
/// Format reference: Google's <c>google.protobuf</c> wire-format spec and
/// <c>onnx.proto</c> at <c>https://github.com/onnx/onnx/blob/main/onnx/onnx.proto</c>.
/// </remarks>
public ref struct ProtobufReader {

  public const int WireVarint = 0;
  public const int WireFixed64 = 1;
  public const int WireLengthDelimited = 2;
  public const int WireStartGroup = 3;   // proto2 groups, deprecated
  public const int WireEndGroup = 4;     // proto2 groups, deprecated
  public const int WireFixed32 = 5;

  private ReadOnlySpan<byte> _data;
  private int _pos;

  public ProtobufReader(ReadOnlySpan<byte> data) {
    this._data = data;
    this._pos = 0;
  }

  public readonly int Position => this._pos;
  public readonly int Remaining => this._data.Length - this._pos;
  public readonly bool AtEnd => this._pos >= this._data.Length;

  /// <summary>Reads a tag (field number + wire type); returns false at EOF.</summary>
  public bool ReadTag(out int fieldNumber, out int wireType) {
    if (this.AtEnd) { fieldNumber = 0; wireType = 0; return false; }
    var tag = this.ReadVarint();
    fieldNumber = (int)(tag >> 3);
    wireType = (int)(tag & 0x07);
    return true;
  }

  /// <summary>Reads an unsigned LEB128 varint (up to 10 bytes).</summary>
  public ulong ReadVarint() {
    ulong result = 0;
    var shift = 0;
    while (this._pos < this._data.Length) {
      var b = this._data[this._pos++];
      result |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0) return result;
      shift += 7;
      if (shift >= 70) throw new InvalidDataException("protobuf: varint exceeds 10 bytes.");
    }
    throw new InvalidDataException("protobuf: truncated varint.");
  }

  public int ReadInt32() => (int)this.ReadVarint();
  public long ReadInt64() => (long)this.ReadVarint();

  public uint ReadFixed32() {
    if (this._pos + 4 > this._data.Length) throw new InvalidDataException("protobuf: truncated fixed32.");
    var v = BinaryPrimitives.ReadUInt32LittleEndian(this._data[this._pos..]);
    this._pos += 4;
    return v;
  }

  public ulong ReadFixed64() {
    if (this._pos + 8 > this._data.Length) throw new InvalidDataException("protobuf: truncated fixed64.");
    var v = BinaryPrimitives.ReadUInt64LittleEndian(this._data[this._pos..]);
    this._pos += 8;
    return v;
  }

  /// <summary>Reads a length-delimited chunk; returns a span pointing into the underlying buffer.</summary>
  public ReadOnlySpan<byte> ReadBytes() {
    var len = (int)this.ReadVarint();
    if (len < 0 || this._pos + len > this._data.Length)
      throw new InvalidDataException($"protobuf: length-delimited field overruns stream (len={len}, remaining={this._data.Length - this._pos}).");
    var s = this._data.Slice(this._pos, len);
    this._pos += len;
    return s;
  }

  public string ReadString() => System.Text.Encoding.UTF8.GetString(this.ReadBytes());

  /// <summary>Skips one wire-format value (tag already consumed).</summary>
  public void SkipField(int wireType) {
    switch (wireType) {
      case WireVarint: this.ReadVarint(); break;
      case WireFixed64: this._pos += 8; break;
      case WireLengthDelimited:
        var len = (int)this.ReadVarint();
        if (len < 0 || this._pos + len > this._data.Length)
          throw new InvalidDataException("protobuf: skip length overruns stream.");
        this._pos += len;
        break;
      case WireFixed32: this._pos += 4; break;
      case WireStartGroup: this.SkipGroup(); break;
      case WireEndGroup: break; // caller handles group termination
      default: throw new InvalidDataException($"protobuf: unknown wire type {wireType}.");
    }
  }

  private void SkipGroup() {
    while (!this.AtEnd) {
      if (!this.ReadTag(out _, out var wt)) return;
      if (wt == WireEndGroup) return;
      this.SkipField(wt);
    }
  }

  /// <summary>
  /// Quick plausibility check: true if the buffer parses as at least one valid
  /// wire-format (tag, value) pair without overrun. Used for soft format detection.
  /// </summary>
  public static bool LooksLikeProtobuf(ReadOnlySpan<byte> data, int maxFieldsToScan = 4) {
    if (data.IsEmpty) return false;
    var reader = new ProtobufReader(data);
    try {
      for (var i = 0; i < maxFieldsToScan && !reader.AtEnd; i++) {
        if (!reader.ReadTag(out var fn, out var wt)) return false;
        if (fn <= 0 || fn > 536870911) return false;
        if (wt > 5) return false;
        reader.SkipField(wt);
      }
      return true;
    } catch { return false; }
  }
}
