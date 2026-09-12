#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Numpy;

/// <summary>
/// WORM writer for the NumPy NPY array serialization format (NEP 1). Emits a
/// v1 file: 6-byte magic + 2-byte version (1.0) + u16 little-endian header
/// length + ASCII Python-dict header (padded with spaces + trailing newline
/// so the preamble + header is a multiple of 64 bytes) + raw array payload.
/// </summary>
public static class NpyWriter {

  /// <summary>Default dtype string used when no explicit type is supplied.</summary>
  public const string DefaultDtype = "|u1";

  /// <summary>
  /// Writes an NPY file from <paramref name="payload"/> with the supplied
  /// dtype/shape header. When <paramref name="shape"/> is null, a 1-D shape
  /// matching the payload's element count is inferred from the dtype's
  /// item-size.
  /// </summary>
  /// <param name="output">Destination stream — content is appended at the current position.</param>
  /// <param name="payload">Raw array bytes laid out in C-order (or Fortran-order when <paramref name="fortranOrder"/> is true).</param>
  /// <param name="dtype">NumPy dtype string (default "|u1", a uint8 array).</param>
  /// <param name="shape">Comma-separated tuple, e.g. "(3, 4)" or "(8,)". Null infers a 1-D shape.</param>
  /// <param name="fortranOrder">True if the payload is in column-major order.</param>
  public static void Write(
      Stream output,
      ReadOnlySpan<byte> payload,
      string dtype = DefaultDtype,
      string? shape = null,
      bool fortranOrder = false) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(dtype);
    if (string.IsNullOrEmpty(dtype))
      throw new ArgumentException("dtype must be non-empty.", nameof(dtype));

    var resolvedShape = shape ?? InferShape(payload.Length, dtype);

    var dict = string.Create(CultureInfo.InvariantCulture,
      $"{{'descr': '{EscapeDtype(dtype)}', 'fortran_order': {(fortranOrder ? "True" : "False")}, 'shape': {resolvedShape}, }}");

    const int preamble = 10; // 6 magic + 2 version + 2 header-len
    var targetLen = ((preamble + dict.Length + 1 + 63) / 64) * 64;
    var pad = targetLen - preamble - dict.Length - 1;
    var headerText = dict + new string(' ', pad) + "\n";
    var headerBytes = Encoding.ASCII.GetBytes(headerText);

    Span<byte> framing = stackalloc byte[preamble];
    framing[0] = 0x93;
    framing[1] = (byte)'N'; framing[2] = (byte)'U'; framing[3] = (byte)'M'; framing[4] = (byte)'P'; framing[5] = (byte)'Y';
    framing[6] = 1; framing[7] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(framing[8..], (ushort)headerBytes.Length);

    output.Write(framing);
    output.Write(headerBytes);
    if (payload.Length > 0) output.Write(payload);
  }

  /// <summary>Convenience: writes an NPY file from a byte array. See span overload for parameter docs.</summary>
  public static void Write(
      Stream output,
      byte[] payload,
      string dtype = DefaultDtype,
      string? shape = null,
      bool fortranOrder = false) {
    ArgumentNullException.ThrowIfNull(payload);
    Write(output, (ReadOnlySpan<byte>)payload, dtype, shape, fortranOrder);
  }

  private static string InferShape(int payloadLength, string dtype) {
    var itemSize = DtypeItemSize(dtype);
    var count = itemSize > 0 ? payloadLength / itemSize : payloadLength;
    return $"({count.ToString(CultureInfo.InvariantCulture)},)";
  }

  /// <summary>
  /// Returns the on-disk byte size of one element for the small set of NumPy
  /// dtype shorthands we need to infer 1-D shapes. Returns 1 for unknown
  /// codes so the writer never throws — the caller can always supply an
  /// explicit shape when the dtype is exotic.
  /// </summary>
  private static int DtypeItemSize(string dtype) {
    if (string.IsNullOrEmpty(dtype)) return 1;
    // Skip leading endian/order prefix (<, >, =, |).
    var i = 0;
    if (dtype[0] is '<' or '>' or '=' or '|') i = 1;
    if (i >= dtype.Length) return 1;
    var typeChar = dtype[i];
    var sizePart = dtype[(i + 1)..];
    if (int.TryParse(sizePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
      return n;
    return typeChar switch {
      'b' or 'B' or 'u' or 'i' => 1,
      'h' or 'H' => 2,
      'l' or 'L' or 'f' => 4,
      'q' or 'Q' or 'd' => 8,
      _ => 1,
    };
  }

  private static string EscapeDtype(string dtype) {
    // dtype tokens are ASCII and never contain single quotes; defensive escape
    // for the rare custom-dtype case so the dict literal remains parseable.
    return dtype.Replace("'", "''");
  }
}
