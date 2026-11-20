using System.Buffers.Binary;

namespace FileFormat.StuffItX;

/// <summary>
/// Blend, StuffIt X's method 4: a sequence of segments, each coded with whichever of stored,
/// Darkhorse, Cyanide or Brimstone suited it.
/// </summary>
/// <remarks>
/// <para>
/// A segment starts with a six-byte header: 0x77, the coding (0 stored, 1 Darkhorse, 2 Cyanide,
/// 3 Brimstone) and the decoded size as a big-endian 32-bit number; Darkhorse adds its window byte,
/// Brimstone its memory and order bytes, and the coded data follows. Between segments the reader
/// resynchronises byte by byte on the next 0x77 whose coding is at most 3, and when another such
/// marker follows within the next four bytes, takes the first one only if its size is a multiple of
/// 0x2000. Fewer than six bytes left end the stream.
/// </para>
/// <para>Converted from XADMaster's <c>XADStuffItXBlendHandle.m</c> (LGPL-2.1-or-later, see
/// THIRD-PARTY-NOTICE.XADMaster.txt).</para>
/// </remarks>
internal static class StuffItXBlend {
  private const byte Marker = 0x77;

  private static bool IsMarker(ReadOnlySpan<byte> b, int at) => b[at] == Marker && b[at + 1] <= 3;

  /// <summary>Decodes a Blend stream to at most <paramref name="length"/> bytes.</summary>
  public static byte[] Decode(StuffItXInput input, long length) {
    var output = new MemoryStream();
    Span<byte> header = stackalloc byte[6];
    while (output.Length < length) {
      if (input.End - input.Position < 6) break;
      for (var i = 0; i < 6; ++i) header[i] = (byte)input.ReadByte();
      for (;;) {
        if (IsMarker(header, 0)) {
          // a possible later marker: only trust this one if its size looks like a segment size
          if (IsMarker(header, 2) || IsMarker(header, 3) || IsMarker(header, 4)) {
            if ((BinaryPrimitives.ReadUInt32BigEndian(header[2..]) & 0x1FFF) == 0) break;
          } else break;
        }
        header[1..].CopyTo(header);
        header[5] = (byte)input.ReadByte();
      }

      var size = BinaryPrimitives.ReadUInt32BigEndian(header[2..]);
      var want = (long)Math.Min(size, length - output.Length);
      var segment = header[1] switch {
        0 => input.ReadBytes(Math.Min(size, (uint)Math.Max(0, input.End - input.Position))),
        1 => StuffItXDarkhorse.Decode(input, size),
        2 => StuffItXCyanide.Decode(input, size),
        _ => StuffItXBrimstone.Decode(input, size),
      };
      output.Write(segment, 0, (int)Math.Min(segment.Length, want));
      if (segment.Length == 0) break;
    }
    return output.ToArray();
  }
}
