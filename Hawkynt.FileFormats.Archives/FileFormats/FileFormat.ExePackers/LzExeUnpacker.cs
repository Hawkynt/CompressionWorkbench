#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.ExePackers;

/// <summary>
/// Restores DOS programs packed by Fabrice Bellard's LZEXE 0.90, 0.91 and 1.00a. Three stub
/// layouts are told apart and named as Deark names them: "0.90", "0.91" (written by 1.00a) and
/// "0.91e" (written by 0.91).
/// <para>
/// Converted from Deark's <c>modules/lzexe.c</c> and the shared EXE helpers it calls (MIT; see
/// THIRD-PARTY-NOTICE.Deark.txt). The stub is recognised by the CRC-32 of the 64 bytes at its
/// entry point. It keeps a private header at CS:0 (the original IP, CS, SP, SS, the compressed
/// length in paragraphs and two sizes), the compressed relocation table right after its code,
/// and the compressed image directly below the stub. The image is an LZ77 stream over an
/// 8 KiB window with a 16-bit LSB-first flag word; relocations come back paragraph-normalised
/// (segment = address / 16, offset = address mod 16) because LZEXE keeps only their linear
/// address.
/// </para>
/// </summary>
public sealed class LzExeUnpacker : IDosExeUnpacker {
  public static LzExeUnpacker Instance { get; } = new();

  public string Id => "lzexe";
  public string DisplayName => "LZEXE (DOS exe)";

  private enum Version { V090, V091, V091E }

  public string? Detect(ReadOnlySpan<byte> file) => DetectVersion(file) switch {
    Version.V090 => "0.90",
    Version.V091 => "0.91",
    Version.V091E => "0.91e",
    _ => null,
  };

  private static Version? DetectVersion(ReadOnlySpan<byte> file) {
    var exe = DosExe.TryRead(file);
    if (exe == null)
      return null;
    return exe.EntryPointCrcs(file) switch {
      0x4b6802c9cf419437UL => Version.V090,
      0x246655c50ae99574UL => Version.V091,
      0xd8a60f138f680f0cUL => Version.V091E,
      _ => null,
    };
  }

  public DosUnpacked Unpack(ReadOnlySpan<byte> file) {
    var version = DetectVersion(file) ?? throw new InvalidDataException("LZEXE: stub not recognised.");
    var exe = DosExe.TryRead(file)!;
    if (exe.RegCS < 0)
      throw new InvalidDataException("LZEXE: negative CS in the packed header.");

    var hdrPos = exe.StartOfCode + exe.RegCS * 16L;
    if (hdrPos + (version == Version.V090 ? 18 : 14) > file.Length)
      throw new InvalidDataException("LZEXE: private header lies past the end of the file.");
    var h = file[(int)hdrPos..];
    var regIP = BinaryPrimitives.ReadUInt16LittleEndian(h);
    var regCS = BinaryPrimitives.ReadInt16LittleEndian(h[2..]);
    var regSP = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
    var regSS = BinaryPrimitives.ReadInt16LittleEndian(h[6..]);
    var compressedParagraphs = BinaryPrimitives.ReadUInt16LittleEndian(h[8..]);
    var extraParagraphs = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
    var stubLength = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);

    var relocEnd = hdrPos + stubLength;
    if (relocEnd > file.Length)
      throw new InvalidDataException("LZEXE: relocation table runs past the end of the file.");
    var relocs = version == Version.V090
      ? ExePackRelocations.Decode(file, hdrPos + 413, relocEnd)
      : DecodeDeltaRelocations(file, hdrPos + 344, relocEnd);

    var code = Decompress(file, hdrPos - compressedParagraphs * 16L, hdrPos);

    // The packer raised the minimum allocation by the stub's needs; take them off again.
    var minAlloc = exe.MinAlloc;
    var maxAlloc = exe.MaxAlloc;
    if (exe.MaxAlloc != 0) {
      minAlloc = (exe.MinAlloc - (extraParagraphs + (stubLength + 15) / 16 + 9)) & 0xFFFF;
      if (exe.MaxAlloc != 0xFFFF)
        maxAlloc = (exe.MaxAlloc - (exe.MinAlloc - minAlloc)) & 0xFFFF;
    }

    var overlay = file[(int)Math.Min(exe.EndOfCode, file.Length)..];
    var output = DosExe.Build(relocs, code, minAlloc, maxAlloc, regSS, regSP, regIP, regCS, overlay);
    return new(this.Detect(file)!, output);
  }

  /// <summary>
  /// LZEXE 0.91's relocation stream: each entry advances a linear address by a byte delta;
  /// a zero byte escapes to a 16-bit delta, where 0 means "advance 0xFFF0" and 1 ends the table.
  /// </summary>
  private static byte[] DecodeDeltaRelocations(ReadOnlySpan<byte> file, long pos, long end) {
    var output = new MemoryStream();
    Span<byte> entry = stackalloc byte[4];
    var address = 0u;
    var count = 0;
    while (true) {
      if (pos >= end || count > 65535)
        throw new InvalidDataException("LZEXE: relocation table is not terminated.");
      var delta = (uint)file[(int)pos++];
      if (delta == 0) {
        if (pos + 2 > end)
          throw new InvalidDataException("LZEXE: relocation table is truncated.");
        var wide = BinaryPrimitives.ReadUInt16LittleEndian(file[(int)pos..]);
        pos += 2;
        if (wide == 0) {
          address += 0xFFF0;
          continue;
        }
        if (wide == 1)
          break;
        delta = wide;
      }
      address += delta;
      BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)(address & 0x0F));
      BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], (ushort)(address >> 4));
      output.Write(entry);
      ++count;
    }
    return output.ToArray();
  }

  /// <summary>
  /// The LZ77 stream: flag bits come LSB-first from 16-bit little-endian words (the next word
  /// is fetched as soon as the previous one is used up). 1 = literal byte; 00xx = match of
  /// length xx+2 at distance 256 - next byte; 01 = a 16-bit pair holding a 13-bit distance and,
  /// in its low 3 bits, length - 2, or when those are 0 a third byte: 0 ends the stream, 1 is a
  /// segment-boundary marker, anything else is length - 1.
  /// </summary>
  private static byte[] Decompress(ReadOnlySpan<byte> file, long start, long end) {
    if (start < 0 || start > end)
      throw new InvalidDataException("LZEXE: compressed image lies outside the file.");
    const int WindowSize = 8192, Mask = WindowSize - 1;
    var window = new byte[WindowSize];
    var windowPos = 0;
    var output = new MemoryStream();
    var pos = start;
    var bits = 0u;
    var bitCount = 0;

    byte NextByte(ReadOnlySpan<byte> f) {
      if (pos >= f.Length)
        throw new InvalidDataException("LZEXE: compressed image runs past the end of the file.");
      return f[(int)pos++];
    }

    void Refill(ReadOnlySpan<byte> f) {
      if (pos + 2 > end)
        throw new InvalidDataException("LZEXE: compressed image runs into the stub.");
      bits = BinaryPrimitives.ReadUInt16LittleEndian(f[(int)pos..]);
      pos += 2;
      bitCount = 16;
    }

    uint NextBit(ReadOnlySpan<byte> f) {
      if (bitCount == 0)
        Refill(f);
      var bit = bits & 1;
      bits >>= 1;
      if (--bitCount == 0)
        Refill(f);
      return bit;
    }

    void Emit(byte b) {
      output.WriteByte(b);
      window[windowPos] = b;
      windowPos = (windowPos + 1) & Mask;
    }

    while (true) {
      if (NextBit(file) == 1) {
        Emit(NextByte(file));
        continue;
      }
      int distance, length;
      if (NextBit(file) == 0) {
        var hi = NextBit(file);
        var lo = NextBit(file);
        length = (int)((hi << 1) + lo + 2);
        distance = 0xFF - NextByte(file);
      } else {
        var low = NextByte(file);
        var high = NextByte(file);
        distance = 0x1FFF - (((high & 0xF8) << 5) | low);
        if ((high & 0x07) == 0) {
          var extra = NextByte(file);
          if (extra == 0)
            break;
          if (extra == 1)
            continue;
          length = extra + 1;
        } else
          length = (high & 0x07) + 2;
      }
      var from = (windowPos - 1 - distance) & Mask;
      for (var i = 0; i < length; ++i) {
        Emit(window[from]);
        from = (from + 1) & Mask;
      }
    }
    return output.ToArray();
  }
}

/// <summary>
/// The relocation table layout of Microsoft EXEPACK (also used by LZEXE 0.90): sixteen groups,
/// one per 64 KiB segment step of 0x1000 paragraphs, each a 16-bit count followed by that many
/// 16-bit offsets.
/// </summary>
internal static class ExePackRelocations {
  public static byte[] Decode(ReadOnlySpan<byte> file, long pos, long end) {
    var output = new MemoryStream();
    Span<byte> entry = stackalloc byte[4];
    var total = 0;
    for (var segment = 0; segment < 0x10000; segment += 0x1000) {
      if (pos + 2 > end)
        throw new InvalidDataException("EXEPACK relocation table is truncated.");
      var count = BinaryPrimitives.ReadUInt16LittleEndian(file[(int)pos..]);
      pos += 2;
      for (var i = 0; i < count; ++i) {
        if (pos + 2 > end || total >= 65535)
          throw new InvalidDataException("EXEPACK relocation table is truncated.");
        entry[0] = file[(int)pos];
        entry[1] = file[(int)pos + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], (ushort)segment);
        output.Write(entry);
        pos += 2;
        ++total;
      }
    }
    return output.ToArray();
  }
}
