#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;

namespace FileSystem.OneFs;

/// <summary>
/// A OneFS Logical Inode Number (LIN).
/// </summary>
/// <remarks>
/// Dell documents a LIN as a 64-bit hexadecimal identifier. OneFS CLI output
/// commonly inserts colons into that hexadecimal value (for example
/// <c>1:2d29:4204</c>), while <c>isi get -L</c> also accepts the contiguous form.
/// Dell additionally documents that the eight-byte LIN field inside OneFS NFS
/// filehandles is little-endian. These public representations are modeled here;
/// this type makes no claim about a proprietary raw-disk inode/tree encoding.
/// </remarks>
public readonly record struct OneFsLin(ulong Value) {

  /// <summary>
  /// Parses a OneFS LIN written as hexadecimal digits, optionally grouped by
  /// colons as shown by <c>isi get -D</c>.
  /// </summary>
  /// <remarks>
  /// Colons are presentation separators only: after removing them the input must
  /// contain between one and sixteen hexadecimal digits. Empty groups, leading or
  /// trailing colons, and other punctuation are rejected.
  /// </remarks>
  public static bool TryParse(ReadOnlySpan<char> text, out OneFsLin lin) {
    lin = default;
    text = text.Trim();
    if (text.IsEmpty)
      return false;

    Span<char> hexadecimal = stackalloc char[16];
    var written = 0;
    var previousWasSeparator = false;
    var sawSeparator = false;

    foreach (var character in text) {
      if (character == ':') {
        if (written == 0 || previousWasSeparator)
          return false;
        previousWasSeparator = true;
        sawSeparator = true;
        continue;
      }

      if (written >= hexadecimal.Length || !Uri.IsHexDigit(character))
        return false;

      hexadecimal[written++] = character;
      previousWasSeparator = false;
    }

    if (written == 0 || previousWasSeparator)
      return false;

    // OneFS CLI examples use a three-group presentation. Avoid accepting an
    // arbitrary colon grammar while still accepting the contiguous lookup form.
    if (sawSeparator && Count(text, ':') != 2)
      return false;

    if (!ulong.TryParse(
          hexadecimal[..written],
          NumberStyles.AllowHexSpecifier,
          CultureInfo.InvariantCulture,
          out var value))
      return false;

    lin = new OneFsLin(value);
    return true;
  }

  /// <summary>
  /// Reads the exact eight-byte little-endian LIN representation Dell documents
  /// inside a OneFS NFS filehandle.
  /// </summary>
  public static OneFsLin FromFileHandleBytes(ReadOnlySpan<byte> bytes) {
    if (bytes.Length != sizeof(ulong))
      throw new ArgumentException("A OneFS filehandle LIN occupies exactly 8 bytes.", nameof(bytes));

    return new OneFsLin(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
  }

  /// <summary>
  /// Writes the exact eight-byte little-endian LIN representation Dell documents
  /// inside a OneFS NFS filehandle.
  /// </summary>
  public void WriteFileHandleBytes(Span<byte> destination) {
    if (destination.Length < sizeof(ulong))
      throw new ArgumentException("A OneFS filehandle LIN requires at least 8 destination bytes.", nameof(destination));

    BinaryPrimitives.WriteUInt64LittleEndian(destination, this.Value);
  }

  /// <summary>
  /// Formats the 16-digit hexadecimal value accepted by <c>isi get -L</c>.
  /// </summary>
  public string ToLookupString()
    => this.Value.ToString("x16", CultureInfo.InvariantCulture);

  /// <summary>
  /// Formats the grouped hexadecimal form observed in Dell <c>isi get -D</c>
  /// output: high 32 bits without leading zeroes, then two four-digit groups.
  /// </summary>
  public string ToDisplayString() {
    var high = (uint)(this.Value >> 32);
    var middle = (ushort)(this.Value >> 16);
    var low = (ushort)this.Value;
    return string.Create(CultureInfo.InvariantCulture, $"{high:x}:{middle:x4}:{low:x4}");
  }

  /// <inheritdoc />
  public override string ToString() => this.ToDisplayString();

  private static int Count(ReadOnlySpan<char> text, char value) {
    var result = 0;
    foreach (var character in text)
      if (character == value)
        ++result;
    return result;
  }
}
