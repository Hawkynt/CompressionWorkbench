#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.ExePackers;

/// <summary>
/// Restores DOS programs packed by Teddy Matsumoto's DIET (EXE and COM; versions 1.00, 1.00d,
/// 1.02b, 1.10a, 1.20, 1.44, 1.45f).
/// <para>
/// Converted from Deark's <c>modules/diet.c</c> (MIT; see THIRD-PARTY-NOTICE.Deark.txt). From
/// 1.02 on, a "dlz" header gives the flags, the compressed length (20 bits), a CRC-16 of the
/// compressed bytes and the original length (22 bits). The stream is LZ77 over an 8 KiB window
/// with LSB-first flag bits from 16-bit words (refilled before and after every bit); distances
/// are a byte plus 1 to 5 prefix-coded high bits, lengths a unary-then-binary code. For EXE files
/// the decompressed data holds the image first and the original header behind it (its relocation
/// table delta-coded); a COM file DIET turned into an EXE comes back as the COM.
/// </para>
/// </summary>
public sealed class DietUnpacker : IDosExeUnpacker {
  public static DietUnpacker Instance { get; } = new();

  public string Id => "diet";
  public string DisplayName => "DIET (DOS exe/com)";

  private enum Kind { Com, Exe }
  private enum Format { Com100, Com102, Com144, Exe100, Exe102, Exe144, Exe145F }

  private sealed record Identity(Kind Kind, Format Format, long DlzPos, long CmprPos, bool ComToExe);

  private const int MaxOutput = 4194304;

  public string? Detect(ReadOnlySpan<byte> file) => Identify(file) is { } id ? Describe(id) : null;

  private static string Describe(Identity id) => id.Format switch {
    Format.Com100 => "1.00 (com)",
    Format.Com102 => "1.02-1.20 (com)",
    Format.Com144 => "1.44-1.45 (com)",
    Format.Exe100 => "1.00 (exe)",
    Format.Exe102 => "1.02-1.20 (exe)",
    Format.Exe144 => id.ComToExe ? "1.44 (exe from com)" : "1.44 (exe)",
    _ => id.ComToExe ? "1.45 (exe from com)" : "1.45 (exe)",
  };

  private static bool At(ReadOnlySpan<byte> file, long pos, ReadOnlySpan<byte> sig) =>
    pos >= 0 && pos + sig.Length <= file.Length && file.Slice((int)pos, sig.Length).SequenceEqual(sig);

  private static ReadOnlySpan<byte> SigDlz => "dlz"u8;
  private static ReadOnlySpan<byte> Sig9D89 => [0x9D, 0x89];
  private static ReadOnlySpan<byte> SigOld => [0xFD, 0xF3, 0xA5, 0xFC, 0x8B, 0xF7, 0xBF, 0x00];
  private static ReadOnlySpan<byte> Sig8EDB => [0x8E, 0xDB, 0x8E, 0xC0, 0x33, 0xF6, 0x33, 0xFF];

  private static Identity? Identify(ReadOnlySpan<byte> file) {
    if (file.Length < 9)
      return null;
    // COM layouts.
    if (file[0] == 0xBE && At(file, 35, SigDlz) && At(file, 17, SigOld))
      return Dlz(Kind.Com, Format.Com102, 35, false);
    if (file[0] == 0xBF && At(file, 17, SigOld))
      return new(Kind.Com, Format.Com100, 0, 37, false);
    if (file[0] == 0xF9 && At(file, 65, SigDlz) && At(file, 10, Sig9D89))
      return Dlz(Kind.Com, Format.Com144, 65, false);

    if (!((file[0] == 'M' && file[1] == 'Z') || (file[0] == 'Z' && file[1] == 'M')))
      return null;
    var codeStart = 16L * BinaryPrimitives.ReadUInt16LittleEndian(file[8..]);
    long rel;
    if (At(file, codeStart - 32 + 77, Sig8EDB)) rel = 77 - 32;
    else if (At(file, codeStart - 32 + 72, Sig8EDB)) rel = 72 - 32;
    else if (At(file, codeStart - 32 + 52, Sig8EDB)) rel = 52 - 32;
    else if (At(file, codeStart - 32 + 55, Sig8EDB)) rel = 55 - 32;
    else return null;
    // Byte 0x95 here marks LGLZ, a different packer sharing this stub start.
    if (codeStart + rel + 26 < file.Length && file[(int)(codeStart + rel + 26)] == 0x95)
      return null;

    var exe = DosExe.TryRead(file)!;
    var comToExe = exe.EndOfCode >= 13 && exe.EndOfCode - 13 + 2 <= file.Length
      && file[(int)exe.EndOfCode - 13] == 0xED && file[(int)exe.EndOfCode - 12] == 0x55;
    return rel switch {
      77 - 32 => Dlz(Kind.Exe, Format.Exe145F, codeStart - 32 + 108, comToExe),
      72 - 32 => Dlz(Kind.Exe, Format.Exe144, codeStart - 32 + 107, comToExe),
      52 - 32 => Dlz(Kind.Exe, Format.Exe102, codeStart - 32 + 87, false),
      _ => new(Kind.Exe, Format.Exe100, 0, codeStart - 32 + 90, false),
    };
  }

  private static Identity Dlz(Kind kind, Format format, long dlzPos, bool comToExe) =>
    new(kind, format, dlzPos, dlzPos + 11, comToExe);

  public DosUnpacked Unpack(ReadOnlySpan<byte> file) {
    var id = Identify(file) ?? throw new InvalidDataException("DIET: stub not recognised.");

    // Header fields.
    long cmprLength = -1, origLength = -1;
    var flags = 0;
    if (id.DlzPos > 0) {
      if (id.DlzPos + 11 > file.Length)
        throw new InvalidDataException("DIET: header lies past the end of the file.");
      var x = file[(int)id.DlzPos + 3];
      flags = x & 0xF0;
      cmprLength = ((long)(x & 0x0F) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(file[((int)id.DlzPos + 4)..]);
      var y = file[(int)id.DlzPos + 8];
      origLength = ((long)(y & 0xFC) << 14) | BinaryPrimitives.ReadUInt16LittleEndian(file[((int)id.DlzPos + 9)..]);
    } else if (id.Format == Format.Exe100) {
      if (file.Length < 36)
        throw new InvalidDataException("DIET: header is truncated.");
      cmprLength = BinaryPrimitives.ReadUInt32LittleEndian(file[32..]) & 0xFFFFF;
    }
    if ((flags & 0x80) != 0)
      throw new InvalidDataException("DIET: multi-block files are not supported.");
    if (cmprLength >= 0 && id.CmprPos + cmprLength > file.Length)
      throw new InvalidDataException("DIET: compressed data runs past the end of the file.");

    var data = Decompress(file, id.CmprPos, id.Kind == Kind.Exe);

    if (id.Kind == Kind.Com)
      return new(Describe(id), data, "unpacked.com");
    if (id.ComToExe)
      return new(Describe(id), data, "unpacked.com");
    return new(Describe(id), BuildExe(file, id, data, cmprLength, origLength, flags));
  }

  private static byte[] Decompress(ReadOnlySpan<byte> file, long pos, bool isExe) {
    const int Mask = 8191;
    var window = new byte[Mask + 1];
    var windowPos = 0;
    var output = new MemoryStream();
    var bits = 0u;
    var count = 0;

    void Refill(ReadOnlySpan<byte> f) {
      if (pos + 2 > f.Length)
        throw new InvalidDataException("DIET: unexpected end of file during decompression.");
      bits = BinaryPrimitives.ReadUInt16LittleEndian(f[(int)pos..]);
      pos += 2;
      count = 16;
    }

    int Bit(ReadOnlySpan<byte> f) {
      if (count == 0)
        Refill(f);
      var b = (int)(bits & 1);
      bits >>= 1;
      if (--count == 0)
        Refill(f);
      return b;
    }

    byte NextByte(ReadOnlySpan<byte> f) {
      if (pos >= f.Length)
        throw new InvalidDataException("DIET: unexpected end of file during decompression.");
      return f[(int)pos++];
    }

    void Emit(byte b) {
      if (output.Length >= MaxOutput)
        throw new InvalidDataException("DIET: decompressed data exceeds 4 MiB.");
      output.WriteByte(b);
      window[windowPos] = b;
      windowPos = (windowPos + 1) & Mask;
    }

    int MatchLength(ReadOnlySpan<byte> f) {
      for (var n = 1; n <= 4; ++n)
        if (Bit(f) == 1)
          return 2 + n;
      var x1 = Bit(f);
      var x2 = Bit(f);
      if (x1 == 1)
        return 7 + x2;
      if (x2 == 0)
        return 9 + 4 * Bit(f) + 2 * Bit(f) + Bit(f);
      return 17 + NextByte(f);
    }

    while (true) {
      if (Bit(file) == 1) {
        Emit(NextByte(file));
        continue;
      }
      var x2 = Bit(file);
      var v = NextByte(file);
      int distance, length;
      if (x2 == 0) {
        if (Bit(file) == 1) {
          length = 2;
          var a2 = Bit(file);
          var a3 = Bit(file);
          var a4 = Bit(file);
          distance = 2303 - (1024 * a2 + 512 * a3 + 256 * a4 + v);
        } else if (v != 0xFF) {
          length = 2;
          distance = 0xFF - v;
        } else {
          if (Bit(file) == 0)
            break; // stop code
          if (isExe)
            continue; // segment refresh
          throw new InvalidDataException("DIET: segment refresh code in a non-EXE stream.");
        }
      } else {
        var a1 = Bit(file);
        if (Bit(file) == 1)
          distance = 511 - (256 * a1 + v);
        else if (Bit(file) == 1)
          distance = 1023 - (256 * a1 + v);
        else {
          var a4 = Bit(file);
          if (Bit(file) == 1)
            distance = 2047 - (512 * a1 + 256 * a4 + v);
          else {
            var a6 = Bit(file);
            if (Bit(file) == 1)
              distance = 4095 - (1024 * a1 + 512 * a4 + 256 * a6 + v);
            else {
              var a8 = Bit(file);
              distance = 8191 - (2048 * a1 + 1024 * a4 + 512 * a6 + 256 * a8 + v);
            }
          }
        }
        length = MatchLength(file);
      }
      if (distance + 1 > output.Length || length > distance + 1)
        throw new InvalidDataException("DIET: match reaches outside the decompressed data.");
      var from = (windowPos - 1 - distance) & Mask;
      for (var i = 0; i < length; ++i) {
        Emit(window[from]);
        from = (from + 1) & Mask;
      }
    }
    return output.ToArray();
  }

  /// <summary>Copies from the decompressed data, reading zeros past its end (DIET drops trailing zeros).</summary>
  private static void CopyPadded(byte[] data, long pos, long length, Stream to) {
    for (var i = 0L; i < length; ++i) {
      var p = pos + i;
      to.WriteByte(p >= 0 && p < data.Length ? data[p] : (byte)0);
    }
  }

  private static int U16(byte[] data, long pos) =>
    (pos >= 0 && pos < data.Length ? data[pos] : 0) | ((pos + 1 >= 0 && pos + 1 < data.Length ? data[pos + 1] : 0) << 8);

  private static byte[] BuildExe(ReadOnlySpan<byte> file, Identity id, byte[] data, long cmprLength, long origLength, int flags) {
    var exe = DosExe.TryRead(file)!;
    var paramOffset = id.Format switch {
      Format.Exe100 or Format.Exe102 => 53,
      Format.Exe144 => 73,
      _ => 26,
    };
    if (exe.EntryPoint + paramOffset + 2 > file.Length || exe.EntryPoint < 0)
      throw new InvalidDataException("DIET: stub parameters lie past the end of the file.");
    var mzApprox = 16L * BinaryPrimitives.ReadUInt16LittleEndian(file[(int)(exe.EntryPoint + paramOffset)..]);

    long mzPos;
    if (id.Format == Format.Exe100)
      mzPos = FindV100MzPos(file, exe, id.CmprPos + cmprLength, data, mzApprox);
    else {
      if (origLength < 0 || (flags & 0x20) == 0)
        throw new InvalidDataException("DIET: unsupported EXE layout.");
      mzPos = mzApprox + origLength % 16;
      if (U16(data, mzPos) is not (0x5A4D or 0x4D5A))
        throw new InvalidDataException("DIET: original header not found.");
    }

    var header = new MemoryStream();
    CopyPadded(data, mzPos, 28, header);
    var h = header.ToArray();
    h[3] &= 0x01;
    var guest = DosExe.TryRead(h) ?? throw new InvalidDataException("DIET: original header not found.");
    var relocPos = mzPos + guest.RelocTablePos;
    long cdata1, cdata2;
    if (guest.NumRelocs == 0) {
      cdata1 = guest.StartOfCode - 28;
      cdata2 = 0;
    } else {
      cdata1 = guest.RelocTablePos - 28;
      cdata2 = guest.StartOfCode - (guest.RelocTablePos + 4L * guest.NumRelocs);
    }
    if (cdata1 < 0 || cdata2 < 0)
      throw new InvalidDataException("DIET: inconsistent original header.");
    var internalOverlay = Math.Max(0, guest.StartOfCode + mzPos - guest.EndOfCode);

    var output = new MemoryStream();
    output.Write(h);
    CopyPadded(data, mzPos + 28, cdata1, output);
    if (guest.NumRelocs != 0) {
      // Delta-coded table: a word with bit 15 set adds to the previous offset (values from
      // 0xC000 add as a 16-bit two's complement), otherwise it is a new segment and an offset follows.
      var pos = relocPos;
      int segment = 0, offset = 0;
      Span<byte> entry = stackalloc byte[4];
      for (var i = 0; i < guest.NumRelocs; ++i) {
        var n = U16(data, pos);
        pos += 2;
        if ((n & 0x8000) != 0)
          offset = (offset + (n >= 0xC000 ? n : n - 0x8000)) & 0xFFFF;
        else {
          segment = n;
          offset = U16(data, pos);
          pos += 2;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], (ushort)segment);
        output.Write(entry);
      }
      var encodedSize = (flags & 0x20) != 0 ? pos - relocPos : guest.NumRelocs * 4L;
      CopyPadded(data, relocPos + encodedSize, cdata2, output);
    }
    CopyPadded(data, 0, mzPos, output);
    if (exe.OverlayLength > 0 && internalOverlay == 0)
      output.Write(file[(int)exe.EndOfCode..]);
    return output.ToArray();
  }

  /// <summary>
  /// DIET 1.00 does not record where the original header lands; it is the one "MZ" among the 16
  /// candidate positions whose relocation count (and table position, when given) matches the
  /// parameters the stub keeps after the compressed data.
  /// </summary>
  private static long FindV100MzPos(ReadOnlySpan<byte> file, DosExe exe, long cmprEnd, byte[] data, long approx) {
    if (cmprEnd + 4 > file.Length || BinaryPrimitives.ReadUInt32BigEndian(file[(int)cmprEnd..]) != 0xD1EDFECAU)
      throw new InvalidDataException("DIET: 1.00 stub tail not recognised.");
    var searchLength = (int)Math.Min(exe.EndOfCode - cmprEnd, 1000);
    var region = searchLength > 0 && cmprEnd + searchLength <= file.Length ? file.Slice((int)cmprEnd, searchLength) : [];
    long relocRel = 0, relocCount;
    bool hasParams;
    var at = region.IndexOf((ReadOnlySpan<byte>)[0x5D, 0x0E, 0x1F, 0xBE]);
    if (at >= 0) {
      var p = cmprEnd + at + 4;
      if (p + 5 > file.Length)
        throw new InvalidDataException("DIET: 1.00 stub parameters truncated.");
      relocRel = BinaryPrimitives.ReadUInt16LittleEndian(file[(int)p..]);
      relocCount = BinaryPrimitives.ReadUInt16LittleEndian(file[((int)p + 3)..]);
      hasParams = true;
    } else if (region.IndexOf((ReadOnlySpan<byte>)[0x5D, 0x07, 0x1F, 0x81]) >= 0) {
      relocCount = 0;
      hasParams = false;
    } else
      throw new InvalidDataException("DIET: 1.00 stub parameters not found.");

    for (var i = 0; i < 16; ++i) {
      var p = approx + i;
      var a = p < data.Length && p >= 0 ? data[p] : 0;
      var b = p + 1 < data.Length && p + 1 >= 0 ? data[p + 1] : 0;
      if (!((a == 'M' && b == 'Z') || (a == 'Z' && b == 'M')))
        continue;
      if (U16(data, p + 6) != relocCount)
        continue;
      if (hasParams && i + U16(data, p + 24) != relocRel)
        continue;
      return p;
    }
    throw new InvalidDataException("DIET: original header not found.");
  }
}
