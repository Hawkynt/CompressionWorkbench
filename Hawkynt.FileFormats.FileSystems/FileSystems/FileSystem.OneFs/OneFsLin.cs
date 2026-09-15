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
  /// Parses a OneFS LIN in either contiguous lookup form or the three-group
  /// hexadecimal form emitted by <c>isi get -D</c>.
  /// </summary>
  /// <remarks>
  /// The grouped form is interpreted as <c>high32:middle16:low16</c>: one to
  /// eight hexadecimal digits followed by two exactly four-digit groups. The
  /// contiguous form accepts one to sixteen hexadecimal digits. Prefixes such as
  /// <c>0x</c>, signs and other punctuation are rejected.
  /// </remarks>
  public static bool TryParse(ReadOnlySpan<char> text, out OneFsLin lin) {
    lin = default;
    text = text.Trim();
    if (text.IsEmpty)
      return false;

    var firstColon = text.IndexOf(':');
    if (firstColon < 0) {
      if (text.Length > 16
          || !ulong.TryParse(
            text,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out var contiguousValue))
        return false;

      lin = new OneFsLin(contiguousValue);
      return true;
    }

    var remainder = text[(firstColon + 1)..];
    var secondColonRelative = remainder.IndexOf(':');
    if (secondColonRelative < 0)
      return false;
    var secondColon = firstColon + 1 + secondColonRelative;

    var highText = text[..firstColon];
    var middleText = text[(firstColon + 1)..secondColon];
    var lowText = text[(secondColon + 1)..];
    if (highText is { Length: < 1 or > 8 }
        || middleText.Length != 4
        || lowText.Length != 4
        || lowText.IndexOf(':') >= 0)
      return false;

    if (!uint.TryParse(
          highText,
          NumberStyles.AllowHexSpecifier,
          CultureInfo.InvariantCulture,
          out var high)
        || !ushort.TryParse(
          middleText,
          NumberStyles.AllowHexSpecifier,
          CultureInfo.InvariantCulture,
          out var middle)
        || !ushort.TryParse(
          lowText,
          NumberStyles.AllowHexSpecifier,
          CultureInfo.InvariantCulture,
          out var low))
      return false;

    lin = new OneFsLin(((ulong)high << 32) | ((ulong)middle << 16) | low);
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
}
