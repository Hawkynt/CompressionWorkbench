#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.ExePackers;

/// <summary>
/// Restores DOS programs packed by Microsoft EXEPACK (the stand-alone EXEPACK.EXE 3.x/4.x and
/// LINK /EXEPACK).
/// <para>
/// Converted from Deark's <c>modules/exepack.c</c> (MIT; see THIRD-PARTY-NOTICE.Deark.txt).
/// The stub's header sits at CS:0 (IP, CS, a placeholder, the stub size, SP, SS, the unpacked
/// length in paragraphs, and from 18-byte headers on a skip length); the stub code ends with
/// the "Packed file is corrupt" handler, after which comes the relocation table (sixteen
/// count-prefixed groups, one per 0x1000-paragraph step). The image is decoded backwards from
/// its end: 0xB0/0xB1 fill a run of one byte, 0xB2/0xB3 copy stored bytes, each with a 16-bit
/// count, and the odd opcodes end the stream; 0xFF padding may precede the first opcode.
/// </para>
/// </summary>
public sealed class ExePackUnpacker : IDosExeUnpacker {
  public static ExePackUnpacker Instance { get; } = new();

  public string Id => "exepack";
  public string DisplayName => "EXEPACK (DOS exe)";

  private static ReadOnlySpan<byte> Epilog => [0xCD, 0x21, 0xB8, 0xFF, 0x4C, 0xCD, 0x21];
  private static ReadOnlySpan<byte> RelocPointerFollower => [0x0E, 0x1F, 0x8B, 0x1E, 0x04, 0x00, 0xFC, 0x33];

  public string? Detect(ReadOnlySpan<byte> file) {
    var exe = DosExe.TryRead(file);
    if (exe == null || FindEpilog(file, exe) < 0)
      return null;
    var fingerprint = DecoderFingerprint(file, exe, FindEpilog(file, exe));
    return fingerprint switch {
      0x77dc4e4aU => "EXEPACK 3.00/4.00",
      0x7b0bb610U => "LINK 3.60",
      0xae58e006U => "EXEPACK 4.03",
      0xa6a446acU => "EXEPACK 4.05/4.06",
      0x1797940cU => "LINK 5.60",
      _ => "unrecognised decoder",
    };
  }

  /// <summary>
  /// Recognises the stub (no relocations, IP 16/18/20, a known entry fingerprint or SP 128 with
  /// "RB" before the entry point) and returns the position of its error-exit epilog, or -1.
  /// </summary>
  private static long FindEpilog(ReadOnlySpan<byte> file, DosExe exe) {
    if (exe.NumRelocs != 0 || exe.RegIP is not (16 or 18 or 20))
      return -1;
    var known = exe.EntryPointCrcs(file) is 0xa6ea214e6c16ee72UL or 0x4e04abaac5d3b465UL or 0x1f449ca73852e197UL;
    var hasRb = exe.EntryPoint >= 2 && exe.EntryPoint <= file.Length && file[(int)exe.EntryPoint - 2] == 'R' && file[(int)exe.EntryPoint - 1] == 'B';
    if (!known && !(exe.RegSP == 128 && hasRb))
      return -1;
    return Search(file, exe.EntryPoint + 200, 100, Epilog);
  }

  /// <summary>First match of <paramref name="needle"/> lying wholly inside [pos, pos+length), or -1.</summary>
  private static long Search(ReadOnlySpan<byte> file, long pos, long length, ReadOnlySpan<byte> needle) {
    if (pos < 0 || pos >= file.Length)
      return -1;
    var end = Math.Min(file.Length, pos + length);
    var at = file[(int)pos..(int)end].IndexOf(needle);
    return at < 0 ? -1 : pos + at;
  }

  private static uint DecoderFingerprint(ReadOnlySpan<byte> file, DosExe exe, long epilog) {
    var length = epilog - 15 - exe.EntryPoint;
    return length <= 0 ? 0 : Compression.Core.Checksums.Crc32.Compute(file.Slice((int)exe.EntryPoint, (int)length));
  }

  public DosUnpacked Unpack(ReadOnlySpan<byte> file) {
    var exe = DosExe.TryRead(file) ?? throw new InvalidDataException("EXEPACK: not an MZ executable.");
    var epilog = FindEpilog(file, exe);
    if (epilog < 0)
      throw new InvalidDataException("EXEPACK: stub not recognised.");

    // Stub header at CS:0, as long as IP says.
    var hdrPos = exe.StartOfCode + exe.RegCS * 16L;
    var hdrSize = exe.RegIP;
    if (hdrPos < 0 || hdrPos + hdrSize > file.Length)
      throw new InvalidDataException("EXEPACK: stub header lies outside the file.");
    var h = file[(int)hdrPos..];
    var regIP = BinaryPrimitives.ReadUInt16LittleEndian(h);
    var regCS = BinaryPrimitives.ReadInt16LittleEndian(h[2..]);
    var exepackSize = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
    var p = hdrSize == 20 ? 10 : 8;
    var regSP = BinaryPrimitives.ReadUInt16LittleEndian(h[p..]);
    var regSS = BinaryPrimitives.ReadInt16LittleEndian(h[(p + 2)..]);
    var destLength = BinaryPrimitives.ReadUInt16LittleEndian(h[(p + 4)..]);
    var skipLength = hdrSize >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(h[(p + 6)..]) : 1;

    var decoderLength = DecoderLength(file, exe, epilog);
    var relocs = ExePackRelocations.Decode(file, exe.EntryPoint + decoderLength, hdrPos + exepackSize);
    var code = DecompressBackwards(file, exe.StartOfCode, 16L * (exe.RegCS - skipLength + 1), 16L * (destLength - skipLength + 1));

    var output = Write(file, exe, relocs, code, regSS, regSP, regIP, regCS);
    return new(this.Detect(file)!, output);
  }

  /// <summary>
  /// Distance from the entry point to the relocation table: read from the pointer that precedes
  /// the stub's "push cs / pop ds / mov bx,[4]" sequence, or else from the error-message pointer
  /// before the epilog plus the 22-byte message.
  /// </summary>
  private static long DecoderLength(ReadOnlySpan<byte> file, DosExe exe, long epilog) {
    long length;
    var found = Search(file, exe.EntryPoint + 100, 100, RelocPointerFollower);
    if (found >= 2)
      length = BinaryPrimitives.ReadUInt16LittleEndian(file[((int)found - 2)..]) - exe.RegIP;
    else
      length = BinaryPrimitives.ReadUInt16LittleEndian(file[((int)epilog - 2)..]) + 22 - exe.RegIP;
    return length is >= 180 and <= 400
      ? length
      : throw new InvalidDataException("EXEPACK: relocation table not found.");
  }

  private static byte[] DecompressBackwards(ReadOnlySpan<byte> file, long start, long compressedLength, long uncompressedLength) {
    if (compressedLength < 0 || uncompressedLength < 0 || start + compressedLength > file.Length)
      throw new InvalidDataException("EXEPACK: compressed image lies outside the file.");
    var buffer = new byte[Math.Max(compressedLength, uncompressedLength)];
    file.Slice((int)start, (int)compressedLength).CopyTo(buffer);
    var src = compressedLength;
    var dst = uncompressedLength;
    while (true) {
      if (src < 1)
        throw new InvalidDataException("EXEPACK: compressed image ends without a final opcode.");
      var opcode = buffer[--src];
      if (opcode is >= 0xB0 and <= 0xB3) {
        if (src < 2)
          throw new InvalidDataException("EXEPACK: truncated opcode.");
        var count = buffer[--src] << 8;
        count |= buffer[--src];
        if (dst < count)
          throw new InvalidDataException("EXEPACK: output overflows the image.");
        if (opcode <= 0xB1) {
          if (src < 1)
            throw new InvalidDataException("EXEPACK: truncated opcode.");
          var fill = buffer[--src];
          for (var i = 0; i < count; ++i)
            buffer[--dst] = fill;
        } else {
          if (src < count)
            throw new InvalidDataException("EXEPACK: stored run reaches past the start.");
          for (var i = 0; i < count; ++i)
            buffer[--dst] = buffer[--src];
        }
        if ((opcode & 1) != 0)
          break;
      } else if (!(opcode == 0xFF && dst == uncompressedLength))
        throw new InvalidDataException("EXEPACK: invalid opcode.");
    }
    return buffer[..(int)uncompressedLength];
  }

  /// <summary>
  /// Rebuilds the file: the original header area before the relocation table is kept, the
  /// relocations go where the packed file's table was (at least at 28), followed by the custom
  /// header data EXEPACK moved down when it is not zeroed; the image is aligned to 16 bytes
  /// unless the packed header was the plain 512-byte one. The minimum allocation keeps the
  /// total memory of the packed program.
  /// </summary>
  private static byte[] Write(ReadOnlySpan<byte> file, DosExe exe, byte[] relocs, byte[] code,
      short regSS, ushort regSP, ushort regIP, short regCS) {
    long finalRelocPos, relocPosField, cdataPos, cdataLength = 0;
    var alignment = 16L;
    var validLayout = relocs.Length == 0
      ? exe.RelocTablePos <= exe.StartOfCode
      : exe.RelocTablePos >= 28 && exe.RelocTablePos <= exe.StartOfCode;
    if (!validLayout) {
      finalRelocPos = relocPosField = cdataPos = 28;
    } else {
      relocPosField = exe.RelocTablePos;
      finalRelocPos = Math.Max(28, exe.RelocTablePos);
      cdataPos = finalRelocPos;
      var maxLength = Math.Max(0, exe.StartOfCode - cdataPos - relocs.Length % 16);
      var nonZero = cdataPos < file.Length && file.Slice((int)cdataPos, (int)Math.Min(maxLength, file.Length - cdataPos)).ContainsAnyExcept((byte)0);
      cdataLength = nonZero ? maxLength : 0;
    }
    var startOfCode = (finalRelocPos + relocs.Length + cdataLength + alignment - 1) / alignment * alignment;
    var fileSize = startOfCode + code.Length;

    var packedMemory = (exe.EndOfCode - exe.StartOfCode + exe.MinAlloc * 16L + 15) & ~15L;
    var minAlloc = packedMemory >= code.Length ? (packedMemory - code.Length + 15) / 16 : 0;

    var overlay = exe.OverlayLength > 0 ? file[(int)exe.EndOfCode..] : [];
    var output = new byte[fileSize + overlay.Length];
    var o = output.AsSpan();
    BinaryPrimitives.WriteUInt16LittleEndian(o, 0x5A4D);
    BinaryPrimitives.WriteUInt16LittleEndian(o[2..], (ushort)(fileSize % 512));
    BinaryPrimitives.WriteUInt16LittleEndian(o[4..], (ushort)((fileSize + 511) / 512));
    BinaryPrimitives.WriteUInt16LittleEndian(o[6..], (ushort)(relocs.Length / 4));
    BinaryPrimitives.WriteUInt16LittleEndian(o[8..], (ushort)(startOfCode / 16));
    BinaryPrimitives.WriteUInt16LittleEndian(o[10..], (ushort)minAlloc);
    BinaryPrimitives.WriteUInt16LittleEndian(o[12..], (ushort)exe.MaxAlloc);
    BinaryPrimitives.WriteInt16LittleEndian(o[14..], regSS);
    BinaryPrimitives.WriteUInt16LittleEndian(o[16..], regSP);
    BinaryPrimitives.WriteUInt16LittleEndian(o[20..], regIP);
    BinaryPrimitives.WriteInt16LittleEndian(o[22..], regCS);
    BinaryPrimitives.WriteUInt16LittleEndian(o[24..], (ushort)relocPosField);
    if (finalRelocPos > 28)
      DosExe.ReadPadded(file, 28, (int)(finalRelocPos - 28)).CopyTo(o[28..]);
    relocs.CopyTo(o[(int)finalRelocPos..]);
    if (cdataLength > 0)
      DosExe.ReadPadded(file, cdataPos, (int)cdataLength).CopyTo(o[(int)(finalRelocPos + relocs.Length)..]);
    code.CopyTo(o[(int)startOfCode..]);
    overlay.CopyTo(o[(int)fileSize..]);
    return output;
  }
}
