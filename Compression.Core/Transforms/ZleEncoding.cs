namespace Compression.Core.Transforms;

/// <summary>
/// ZLE, the zero-length encoding ZFS offers as <c>compression=zle</c>: runs of zero
/// bytes collapse to one control byte, everything else is copied in short literal runs.
/// </summary>
/// <remarks>
/// <para>A control byte <c>c</c> below <see cref="LiteralLimit"/> is followed by
/// <c>c + 1</c> literal bytes; any other value stands for <c>c - 63</c> zero bytes. With
/// ZFS's parameter of 64 a literal run is 1-64 bytes and a zero run 1-192 bytes. The
/// encoder ends a literal run in front of two consecutive zeros, so a lone zero inside
/// data stays a literal.</para>
/// <para>Written from <c>docs/ZFS-ON-DISK.md</c>, which records the format as OpenZFS
/// uses it; OpenZFS (CDDL) was read for behaviour only.</para>
/// </remarks>
public static class ZleEncoding {
  /// <summary>The ZFS parameter: control bytes below it start a literal run.</summary>
  public const int LiteralLimit = 64;

  private const int MaxZeroRun = 256 - ZleEncoding.LiteralLimit;

  /// <summary>Encodes <paramref name="data"/>.</summary>
  /// <param name="data">The bytes to encode.</param>
  /// <returns>The ZLE stream.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var output = new byte[data.Length + (data.Length + ZleEncoding.LiteralLimit - 1) / ZleEncoding.LiteralLimit + 1];
    var op = 0;
    var ip = 0;
    while (ip < data.Length) {
      var first = ip;
      var control = op++;
      if (data[ip] == 0) {
        var last = Math.Min(ip + ZleEncoding.MaxZeroRun, data.Length);
        while (ip < last && data[ip] == 0)
          ++ip;
        output[control] = (byte)(ip - first - 1 + ZleEncoding.LiteralLimit);
        continue;
      }

      var end = Math.Min(ip + ZleEncoding.LiteralLimit, data.Length);
      while (ip < end - 1 && (data[ip] | data[ip + 1]) != 0)
        output[op++] = data[ip++];
      if (data[ip] != 0)
        output[op++] = data[ip++];
      output[control] = (byte)(ip - first - 1);
    }

    return output.AsSpan(0, op).ToArray();
  }

  /// <summary>Decodes a whole ZLE stream.</summary>
  /// <param name="data">The stream.</param>
  /// <returns>The decoded bytes.</returns>
  /// <exception cref="InvalidDataException">A literal run reaches past the end of the stream.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream(data.Length * 2);
    var ip = 0;
    Span<byte> zeros = stackalloc byte[ZleEncoding.MaxZeroRun];
    zeros.Clear();
    while (ip < data.Length) {
      var length = data[ip++] + 1;
      if (length <= ZleEncoding.LiteralLimit) {
        if (ip + length > data.Length)
          throw new InvalidDataException("ZLE: literal run past the end of the stream.");
        output.Write(data.Slice(ip, length));
        ip += length;
      } else
        output.Write(zeros[..(length - ZleEncoding.LiteralLimit)]);
    }

    return output.ToArray();
  }

  /// <summary>
  /// Decodes until <paramref name="destination"/> is full, as ZFS does: what follows is
  /// padding and is ignored.
  /// </summary>
  /// <param name="data">The stream, possibly padded.</param>
  /// <param name="destination">Receives exactly its length in decoded bytes.</param>
  /// <exception cref="InvalidDataException">The stream ends before the destination is full.</exception>
  public static void Decode(ReadOnlySpan<byte> data, Span<byte> destination) {
    var ip = 0;
    var op = 0;
    while (op < destination.Length) {
      if (ip >= data.Length)
        throw new InvalidDataException("ZLE: stream ended before the declared length.");
      var length = data[ip++] + 1;
      if (length <= ZleEncoding.LiteralLimit) {
        length = Math.Min(length, destination.Length - op);
        if (ip + length > data.Length)
          throw new InvalidDataException("ZLE: literal run past the end of the stream.");
        data.Slice(ip, length).CopyTo(destination[op..]);
        ip += length;
      } else {
        length = Math.Min(length - ZleEncoding.LiteralLimit, destination.Length - op);
        destination.Slice(op, length).Clear();
      }

      op += length;
    }
  }
}
