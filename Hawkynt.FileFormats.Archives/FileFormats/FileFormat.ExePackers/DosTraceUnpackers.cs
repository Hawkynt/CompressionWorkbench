#pragma warning disable CS1591
namespace FileFormat.ExePackers;

/// <summary>
/// A DOS packer restored by running its own stub in the emulator (<see cref="DosTracer"/>).
/// Only stubs recognised by <see cref="Variant"/> are taken: each recognised layout was checked
/// against original programs packed by the real packer.
/// </summary>
public abstract class DosTraceUnpacker : IDosExeUnpacker {
  public abstract string Id { get; }
  public abstract string DisplayName { get; }

  /// <summary>The stub layout at the entry point of an MZ file, or null.</summary>
  private protected abstract string? Variant(byte[] entry, DosExe exe);

  public virtual string? Detect(ReadOnlySpan<byte> file) {
    var exe = DosExe.TryRead(file);
    return exe == null ? null : this.Variant(DosExe.ReadPadded(file, exe.EntryPoint, 64), exe);
  }

  public DosUnpacked Unpack(ReadOnlySpan<byte> file) {
    var variant = this.Detect(file) ?? throw new InvalidDataException($"{this.DisplayName}: stub not recognised.");
    var result = DosTracer.Unpack(file);
    return new(variant, result.Executable, result.IsCom ? "unpacked.com" : "unpacked.exe");
  }

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

/// <summary>AXE 2.2 (System Enhancement Associates).</summary>
public sealed class AxeUnpacker : DosTraceUnpacker {
  public static AxeUnpacker Instance { get; } = new();
  public override string Id => "axe";
  public override string DisplayName => "AXE (DOS exe)";

  private protected override string? Variant(byte[] entry, DosExe exe) =>
    Match(entry, 0, "\xfc\xbc??\x0e\x1f\xa3??\xe8??\xa1??\x8b\x1e??\x2b\xc3\x8e\xc0\xb1\x03\xd3\xe3") ? "2.2" : null;
}
