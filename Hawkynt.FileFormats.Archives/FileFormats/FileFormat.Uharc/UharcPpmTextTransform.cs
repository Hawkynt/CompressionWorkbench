namespace FileFormat.Uharc;

/// <summary>
/// The output side of PPM's text mode (docs/UHARC-ON-DISK.md, "PPM", Text transform): model
/// symbols are complemented bytes, and in a text stream, up to the length its header gives,
/// bytes 0x80 and above stand for short strings, a capital-letter prefix, or an escape that
/// selects one byte from a table.
/// </summary>
internal sealed class UharcPpmTextTransform {

  private const byte Literal = 0xC8;
  private const byte EscapeClass = 0x64;
  private const byte CapitalClass = 0x63;

  private int _pending;
  private int _escapeKind;

  /// <summary>Writes the expansion of one model symbol; returns the new output position.</summary>
  /// <param name="symbol">The model symbol.</param>
  /// <param name="written">Bytes written so far.</param>
  /// <param name="transformEnd">Output position from which bytes pass unchanged.</param>
  /// <param name="output">The output buffer.</param>
  public int Emit(byte symbol, int written, long transformEnd, byte[] output) {
    var value = (byte)~symbol;

    void Put(byte b) {
      if (written >= output.Length)
        throw new InvalidDataException("UHARC: PPM text transform runs past the end of the stream.");
      output[written++] = b;
    }

    if (this._pending == 1) {
      this._pending = 0;
      Put(UharcPpmTables.TransformEscape[(this._escapeKind - 0xA6) * 11 + value & 0x7FFFFFFF]);
      return written;
    }

    if (written >= transformEnd || (value < 0x80 && this._pending == 0)) {
      Put(value);
      return written;
    }

    var kind = UharcPpmTables.TransformClass[value];
    if (kind >= Literal) {
      Put((byte)(value - this._pending));
      this._pending = 0;
      return written;
    }

    if (kind == EscapeClass) {
      this._escapeKind = value;
      this._pending = 1;
      return written;
    }

    if (kind == CapitalClass) {
      this._pending = 0x20;
      return written;
    }

    var length = UharcPpmTables.TransformStringLength[kind];
    var offset = (int)UharcPpmTables.TransformStringOffset[kind];
    if (this._pending != 0) {
      Put((byte)((byte)~UharcPpmTables.TransformStrings[offset] - 0x20));
      ++offset;
      --length;
      this._pending = 0;
    }

    for (var i = 0; i < length; ++i)
      Put((byte)~UharcPpmTables.TransformStrings[offset + i]);
    return written;
  }
}
