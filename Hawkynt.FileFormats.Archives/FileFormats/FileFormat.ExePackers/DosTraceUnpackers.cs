#pragma warning disable CS1591
using System.Buffers.Binary;
namespace FileFormat.ExePackers;

/// <summary>
/// A DOS packer restored by running its own stub in the emulator (<see cref="DosTracer"/>).
/// Only stubs recognised by <see cref="Variant"/> are taken: each recognised layout was checked
/// against original programs packed by the real packer.
/// </summary>
public abstract class DosTraceUnpacker : IDosExeUnpacker {
  public abstract string Id { get; }
  public abstract string DisplayName { get; }

  /// <summary>Stub bytes in front of a routine the stub unpacks behind the image and runs.</summary>
  protected virtual int RoutinePrefix => 0;

  /// <summary>The stub layout at the entry point of an MZ file, or null.</summary>
  private protected abstract string? Variant(byte[] entry, DosExe exe);

  public virtual string? Detect(ReadOnlySpan<byte> file) {
    var exe = DosExe.TryRead(file);
    return exe == null ? null : this.Variant(DosExe.ReadPadded(file, exe.EntryPoint, 64), exe);
  }

  public DosUnpacked Unpack(ReadOnlySpan<byte> file) {
    var variant = this.Detect(file) ?? throw new InvalidDataException($"{this.DisplayName}: stub not recognised.");
    var result = DosTracer.Unpack(file, routinePrefix: this.RoutinePrefix);
    var executable = result.IsCom ? result.Executable : this.Complete(file, result.Executable);
    return new(variant, executable, result.IsCom ? "unpacked.com" : "unpacked.exe");
  }

  /// <summary>Packer-specific corrections of the traced EXE (fields the stub keeps elsewhere).</summary>
  private protected virtual byte[] Complete(ReadOnlySpan<byte> packed, byte[] unpacked) => unpacked;

  /// <summary>Pattern of byte values (chars 0-255) where '?' matches anything.</summary>
  protected static bool Match(byte[] mem, int pos, string pattern) {
    if (pos < 0 || pos + pattern.Length > mem.Length)
      return false;
    for (var i = 0; i < pattern.Length; ++i)
      if (pattern[i] != '?' && mem[pos + i] != pattern[i])
        return false;
    return true;
  }
}

/// <summary>aPACK 1.00 (Joergen Ibsen) EXE stubs: the standard one, the one without DS/ES saving (-s) and the tiny one (-t).</summary>
public sealed class ApackUnpacker : DosTraceUnpacker {
  public static ApackUnpacker Instance { get; } = new();
  public override string Id => "apack";
  public override string DisplayName => "aPACK (DOS exe)";

  private protected override string? Variant(byte[] entry, DosExe exe) {
    const string Body = "\x8c\xcb\xba??\x03\xda\xfc\x33\xf6\x33\xff\x4b\x8e\xdb\x8d\x87??\x8e\xc0\xb9\x08\x00\xf3\xa5";
    if (Match(entry, 0, "\x1e\x06" + Body)) return "1.00";
    if (Match(entry, 0, Body)) return "1.00 (-s)";
    if (Match(entry, 0, "\x1e\x06\x0e\x1f\x0e\x07\xbe??\xbf??\x8b\xcf\xfc\x57\xf3\xa4\xc3")) return "1.00 (tiny, -t)";
    if (Match(entry, 0, "\x1e\x06\x8c\xc8\x8e\xd8\x05??\x8e\xc0\x50\xbe??\x33\xff\xfc\xb2\x80\xbd")) return "1.00 (no code mover, -m)";
    return null;
  }
}

/// <summary>ExeLITE (EXE compression of the ELITE tool).</summary>
public sealed class ExeLiteUnpacker : DosTraceUnpacker {
  public static ExeLiteUnpacker Instance { get; } = new();
  public override string Id => "exelite";
  public override string DisplayName => "ExeLITE (DOS exe)";

  private protected override string? Variant(byte[] entry, DosExe exe) =>
    Match(entry, 0, "\xfc\x8c\xc8\x01\x06??\xba??\x03\xc2\x8b\xd8\x05??\x8e\xdb\x8e\xc0\x33\xff\x33\xf6\xb9\x08\x00\xf3\xa5") ? "2.00" : null;
}

/// <summary>
/// TINYPROG (Tranzoa). Recognised as Deark's src/fmtutil-exe.c recognises it: no relocations,
/// a jump over the user data, a short jump, then the stub's fixed opening (MIT; see
/// THIRD-PARTY-NOTICE.Deark.txt).
/// </summary>
public sealed class TinyProgUnpacker : DosTraceUnpacker {
  public static TinyProgUnpacker Instance { get; } = new();
  public override string Id => "tinyprog";
  public override string DisplayName => "TINYPROG (DOS exe)";

  private protected override string? Variant(byte[] entry, DosExe exe) => null;

  /// <summary>TINYPROG's jumps reach past the 64 entry bytes, so it reads the file itself.</summary>
  public override string? Detect(ReadOnlySpan<byte> file) => DetectTiny(file);

  internal static string? DetectTiny(ReadOnlySpan<byte> file) {
    var exe = DosExe.TryRead(file);
    if (exe == null || exe.NumRelocs != 0)
      return null;
    var pos = exe.EntryPoint;
    var head = DosExe.ReadPadded(file, pos, 3);
    if (head[0] != 0xE9)
      return null;
    pos += 3 + (head[1] | (head[2] << 8));
    var jump = DosExe.ReadPadded(file, pos, 2);
    if (jump[0] != 0xEB)
      return null;
    pos += 2 + (sbyte)jump[1];
    var sig = "\x50\xbe\x05\x01\x03\x36\x01\x01\x8c\xd2\x8c\xd8\x03\x44";
    var at = DosExe.ReadPadded(file, pos, 8 + sig.Length);
    if (Match(at, 0, sig)) return "early (1990)";
    if (Match(at, 8, sig)) return "3.x";
    return null;
  }
}

/// <summary>
/// WWPACK 3.05 (Rafal Wierzbicki, Piotr Warezak), files made with the P command. Recognised as
/// Deark's src/fmtutil-exe.c recognises it (MIT; see THIRD-PARTY-NOTICE.Deark.txt): no
/// relocations, a 32-byte header, an entry point after the image start and the stub's opening
/// bytes. Its stub unpacks a relocation routine right behind the image and runs it; one data byte
/// of the stub sits in front of that routine (measured on files whose image ends at different
/// paragraph offsets), so it is not part of the image.
/// </summary>
public sealed class WwpackUnpacker : DosTraceUnpacker {
  public static WwpackUnpacker Instance { get; } = new();
  public override string Id => "wwpack";
  public override string DisplayName => "WWPACK (DOS exe)";
  protected override int RoutinePrefix => 1;

  private protected override string? Variant(byte[] entry, DosExe exe) =>
    exe.NumRelocs == 0 && exe.StartOfCode == 32 && exe.EntryPoint != exe.StartOfCode
      && Match(entry, 0, "\xb8??\x8c\xca\x03\xd0\x8c\xc9\x81\xc1") ? "3.0x" : null;
}

/// <summary>
/// PGMPAK 0.15 (Todor Todorov). The packed file asks DOS to load it high (zero minimum and
/// maximum allocation); the stub unpacks the program at the bottom of its memory, gives it a
/// child PSP and jumps to it. Measured on programs packed by PGMPAK 0.15 whose images end at
/// different paragraph offsets: the decompressor writes one zero byte past the image; the
/// program's minimum and maximum allocation are stored in front of its SS, SP, IP and CS near the
/// end of the packed image; the file ends with <c>"\0PGMPAK 0.15"</c>, which is the packer's, not
/// the program's.
/// </summary>
public sealed class PgmpakUnpacker : DosTraceUnpacker {
  public static PgmpakUnpacker Instance { get; } = new();
  public override string Id => "pgmpak";
  public override string DisplayName => "PGMPAK (DOS exe)";

  private protected override string? Variant(byte[] entry, DosExe exe) =>
    exe is { MinAlloc: 0, MaxAlloc: 0 }
      && Match(entry, 0, "\x1e\x17\x50\xb4\x30\xcd\x21\x3c\x02\x73\x04\xb4\x4c\xcd\x21\xfc\xbe??\xbf??\xe8") ? "0.15" : null;

  private protected override byte[] Complete(ReadOnlySpan<byte> packed, byte[] unpacked) {
    var exe = DosExe.TryRead(unpacked)!;
    var packedExe = DosExe.TryRead(packed)!;
    var start = (int)exe.StartOfCode;
    var end = (int)exe.EndOfCode;
    if (end - start < 2)
      throw new InvalidDataException("PGMPAK: restored image is empty.");
    var image = unpacked.AsSpan(start, end - start - 1);
    var relocTable = unpacked.AsSpan(exe.RelocTablePos, exe.NumRelocs * 4);
    for (var k = 0; k < exe.NumRelocs; ++k) {
      var at = BinaryPrimitives.ReadUInt16LittleEndian(relocTable[(k * 4 + 2)..]) * 16 + BinaryPrimitives.ReadUInt16LittleEndian(relocTable[(k * 4)..]);
      if (at + 2 > image.Length)
        throw new InvalidDataException("PGMPAK: a relocation lies in the byte past the image.");
    }

    // The stored record: minimum, maximum allocation, SS, SP, IP, CS.
    Span<byte> key = stackalloc byte[8];
    BinaryPrimitives.WriteInt16LittleEndian(key, exe.RegSS);
    BinaryPrimitives.WriteUInt16LittleEndian(key[2..], exe.RegSP);
    BinaryPrimitives.WriteUInt16LittleEndian(key[4..], exe.RegIP);
    BinaryPrimitives.WriteInt16LittleEndian(key[6..], exe.RegCS);
    var tailStart = (int)Math.Max(packedExe.StartOfCode + 4, Math.Min(packedExe.EndOfCode, packed.Length) - 64);
    var tail = packed[tailStart..(int)Math.Min(packedExe.EndOfCode, packed.Length)];
    var hit = tail.LastIndexOf(key);
    if (hit < 4)
      throw new InvalidDataException("PGMPAK: stored allocation not found.");
    var minAlloc = BinaryPrimitives.ReadUInt16LittleEndian(tail[(hit - 4)..]);
    var maxAlloc = BinaryPrimitives.ReadUInt16LittleEndian(tail[(hit - 2)..]);
    return DosExe.Build(relocTable, image, minAlloc, maxAlloc, exe.RegSS, exe.RegSP, exe.RegIP, exe.RegCS, OverlayAfterTrailer(packed, packedExe));
  }

  // Bytes behind the image other than PGMPAK's own trailer are the program's overlay.
  private static ReadOnlySpan<byte> OverlayAfterTrailer(ReadOnlySpan<byte> packed, DosExe exe) {
    var overlay = packed[(int)Math.Min(exe.EndOfCode, packed.Length)..];
    ReadOnlySpan<byte> trailer = "\0PGMPAK 0.15"u8;
    return overlay.StartsWith(trailer) ? overlay[trailer.Length..] : overlay;
  }
}

/// <summary>AXE 2.2 (System Enhancement Associates).</summary>
public sealed class AxeUnpacker : DosTraceUnpacker {
  public static AxeUnpacker Instance { get; } = new();
  public override string Id => "axe";
  public override string DisplayName => "AXE (DOS exe)";

  private protected override string? Variant(byte[] entry, DosExe exe) =>
    Match(entry, 0, "\xfc\xbc??\x0e\x1f\xa3??\xe8??\xa1??\x8b\x1e??\x2b\xc3\x8e\xc0\xb1\x03\xd3\xe3") ? "2.2" : null;
}
