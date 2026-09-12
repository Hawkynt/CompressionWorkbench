#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Wbn;

/// <summary>
/// Minimal RFC 8949 CBOR walker — enough to recognise major types, read item headers,
/// and skip over arbitrary items so callers can locate specific positions inside a
/// well-formed bundle. Indefinite-length encodings, tag chaining, and break codes are
/// handled. Floats and unsupported simple values are skipped without interpretation.
/// The walker never throws on an unsupported type — it returns false so the caller can
/// downgrade to partial parsing.
/// </summary>
public static class CborWalker {

  /// <summary>One decoded CBOR header. <see cref="MajorType"/> is in 0..7. <see cref="Value"/> is the argument: positive integers, lengths, or simple values. Indefinite lengths are signalled by <see cref="IsIndefinite"/>.</summary>
  public readonly record struct Header(byte MajorType, ulong Value, bool IsIndefinite);

  /// <summary>
  /// Reads one CBOR header. Returns false at EOF or on malformed leading-byte argument.
  /// </summary>
  public static bool TryReadHeader(Stream s, out Header header) {
    ArgumentNullException.ThrowIfNull(s);
    header = default;
    var ib = s.ReadByte();
    if (ib < 0) return false;
    var initial = (byte)ib;
    var major = (byte)(initial >> 5);
    var info = (byte)(initial & 0x1F);

    if (info < 24) {
      header = new Header(major, info, IsIndefinite: false);
      return true;
    }

    switch (info) {
      case 24: {
        var b = s.ReadByte();
        if (b < 0) return false;
        header = new Header(major, (ulong)b, IsIndefinite: false);
        return true;
      }
      case 25: {
        Span<byte> buf = stackalloc byte[2];
        if (!ReadExact(s, buf)) return false;
        header = new Header(major, BinaryPrimitives.ReadUInt16BigEndian(buf), IsIndefinite: false);
        return true;
      }
      case 26: {
        Span<byte> buf = stackalloc byte[4];
        if (!ReadExact(s, buf)) return false;
        header = new Header(major, BinaryPrimitives.ReadUInt32BigEndian(buf), IsIndefinite: false);
        return true;
      }
      case 27: {
        Span<byte> buf = stackalloc byte[8];
        if (!ReadExact(s, buf)) return false;
        header = new Header(major, BinaryPrimitives.ReadUInt64BigEndian(buf), IsIndefinite: false);
        return true;
      }
      case 31: {
        // Indefinite length is only valid for byte strings, text strings, arrays, and maps.
        // For major type 7 (info=31), this is the break stop-code; the caller treats it
        // as "end of indefinite container" rather than a real header.
        header = new Header(major, 0, IsIndefinite: true);
        return true;
      }
      default:
        return false;
    }
  }

  /// <summary>
  /// Skips one complete CBOR item starting at the current stream position. Returns false
  /// if the item is malformed or truncated; on false, the stream position is undefined.
  /// </summary>
  public static bool TrySkip(Stream s, int depth = 0) {
    if (depth > 256) return false;
    if (!TryReadHeader(s, out var h)) return false;
    return SkipBody(s, h, depth);
  }

  /// <summary>Reads one CBOR text string. Returns null on type mismatch, malformed input, or truncation. Indefinite-length text strings are concatenated.</summary>
  public static string? TryReadTextString(Stream s) {
    if (!TryReadHeader(s, out var h)) return null;
    if (h.MajorType != WbnConstants.MajorTypeTextString) return null;
    return ReadStringBody(s, h, isText: true);
  }

  /// <summary>Reads one CBOR byte string. Returns null on type mismatch, malformed input, or truncation. Indefinite-length byte strings are concatenated.</summary>
  public static byte[]? TryReadByteString(Stream s) {
    if (!TryReadHeader(s, out var h)) return null;
    if (h.MajorType != WbnConstants.MajorTypeByteString) return null;
    return TryReadByteStringRaw(s, h);
  }

  /// <summary>Reads one CBOR byte string body with raw bytes preserved. Use when the caller already consumed the header.</summary>
  public static byte[]? TryReadByteStringRaw(Stream s, in Header h) {
    if (h.MajorType != WbnConstants.MajorTypeByteString) return null;
    if (!h.IsIndefinite) return ReadDefiniteBytes(s, h.Value);

    using var ms = new MemoryStream();
    while (true) {
      if (!TryReadHeader(s, out var inner)) return null;
      if (inner.MajorType == WbnConstants.MajorTypeSimpleOrFloat && inner.IsIndefinite) break;
      if (inner.MajorType != WbnConstants.MajorTypeByteString || inner.IsIndefinite) return null;
      var chunk = ReadDefiniteBytes(s, inner.Value);
      if (chunk == null) return null;
      ms.Write(chunk, 0, chunk.Length);
    }
    return ms.ToArray();
  }

  private static bool SkipBody(Stream s, Header h, int depth) {
    switch (h.MajorType) {
      case WbnConstants.MajorTypeUnsignedInt:
      case WbnConstants.MajorTypeNegativeInt:
        return !h.IsIndefinite;
      case WbnConstants.MajorTypeByteString:
      case WbnConstants.MajorTypeTextString:
        if (h.IsIndefinite) {
          while (true) {
            if (!TryReadHeader(s, out var inner)) return false;
            if (inner.MajorType == WbnConstants.MajorTypeSimpleOrFloat && inner.IsIndefinite) return true;
            if (inner.MajorType != h.MajorType || inner.IsIndefinite) return false;
            if (!SkipDefiniteBytes(s, inner.Value)) return false;
          }
        }
        return SkipDefiniteBytes(s, h.Value);
      case WbnConstants.MajorTypeArray: {
        if (h.IsIndefinite) {
          while (true) {
            if (!PeekIsBreak(s, out var isBreak)) return false;
            if (isBreak) return true;
            if (!TrySkip(s, depth + 1)) return false;
          }
        }
        var count = h.Value;
        for (ulong i = 0; i < count; i++)
          if (!TrySkip(s, depth + 1))
            return false;
        return true;
      }
      case WbnConstants.MajorTypeMap: {
        if (h.IsIndefinite) {
          while (true) {
            if (!PeekIsBreak(s, out var isBreak)) return false;
            if (isBreak) return true;
            if (!TrySkip(s, depth + 1)) return false;
            if (!TrySkip(s, depth + 1)) return false;
          }
        }
        var pairs = h.Value;
        for (ulong i = 0; i < pairs; i++) {
          if (!TrySkip(s, depth + 1)) return false;
          if (!TrySkip(s, depth + 1)) return false;
        }
        return true;
      }
      case WbnConstants.MajorTypeTag:
        if (h.IsIndefinite) return false;
        return TrySkip(s, depth + 1);
      case WbnConstants.MajorTypeSimpleOrFloat:
        if (h.IsIndefinite) return false;
        return true;
      default:
        return false;
    }
  }

  private static bool PeekIsBreak(Stream s, out bool isBreak) {
    isBreak = false;
    var origin = s.Position;
    var b = s.ReadByte();
    if (b < 0) return false;
    if (b == WbnConstants.BreakStopCode) {
      isBreak = true;
      return true;
    }
    s.Position = origin;
    return true;
  }

  private static string? ReadStringBody(Stream s, Header h, bool isText) {
    if (!h.IsIndefinite) {
      var bytes = ReadDefiniteBytes(s, h.Value);
      if (bytes == null) return null;
      return DecodeString(bytes, isText);
    }

    using var ms = new MemoryStream();
    while (true) {
      if (!TryReadHeader(s, out var inner)) return null;
      if (inner.MajorType == WbnConstants.MajorTypeSimpleOrFloat && inner.IsIndefinite) break;
      if (inner.MajorType != h.MajorType || inner.IsIndefinite) return null;
      var chunk = ReadDefiniteBytes(s, inner.Value);
      if (chunk == null) return null;
      ms.Write(chunk, 0, chunk.Length);
    }
    return DecodeString(ms.ToArray(), isText);
  }

  private static string DecodeString(byte[] raw, bool isText)
    => isText ? Encoding.UTF8.GetString(raw) : Encoding.Latin1.GetString(raw);

  private static byte[]? ReadDefiniteBytes(Stream s, ulong length) {
    if (length > int.MaxValue) return null;
    var len = (int)length;
    var remaining = s.CanSeek ? s.Length - s.Position : long.MaxValue;
    if (len > remaining) return null;
    var buf = new byte[len];
    var read = 0;
    while (read < len) {
      var n = s.Read(buf, read, len - read);
      if (n <= 0) return null;
      read += n;
    }
    return buf;
  }

  private static bool SkipDefiniteBytes(Stream s, ulong length) {
    if (length > long.MaxValue) return false;
    var len = (long)length;
    if (s.CanSeek) {
      var remaining = s.Length - s.Position;
      if (len > remaining) return false;
      s.Position += len;
      return true;
    }
    var buf = new byte[Math.Min(len, 4096)];
    var left = len;
    while (left > 0) {
      var want = (int)Math.Min(left, buf.Length);
      var n = s.Read(buf, 0, want);
      if (n <= 0) return false;
      left -= n;
    }
    return true;
  }

  private static bool ReadExact(Stream s, Span<byte> buf) {
    var read = 0;
    while (read < buf.Length) {
      var n = s.Read(buf[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }
}
