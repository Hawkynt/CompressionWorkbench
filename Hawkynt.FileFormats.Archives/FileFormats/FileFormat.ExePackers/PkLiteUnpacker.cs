#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;

namespace FileFormat.ExePackers;

/// <summary>
/// Restores DOS programs packed by PKWARE's PKLITE (EXE and COM, versions 1.00 to 2.01 including
/// "extra" and "large" compression, the 1.20 variants, and the 1990 betas).
/// <para>
/// Converted from Deark's <c>modules/pklite.c</c> (MIT; see THIRD-PARTY-NOTICE.Deark.txt). PKLITE
/// keeps its parameters in the machine code of its stub, so the stub is walked component by
/// component (intro, optional descrambler, copier, decompressor) by matching known byte
/// patterns. The image is an LZ77 stream over an 8 KiB window: flag bits LSB-first from 16-bit
/// words, match lengths and offset high bits from fixed prefix codes, literals optionally XORed
/// with the bit count left in the flag word ("extra" compression). The relocation table and an
/// 8-byte footer (SS, SP, CS, IP) follow the image; files without extra compression also keep a
/// copy of the original header, so their restored form is the original byte for byte.
/// </para>
/// </summary>
public sealed class PkLiteUnpacker : IDosExeUnpacker {
  public static PkLiteUnpacker Instance { get; } = new();

  public string Id => "pklite";
  public string DisplayName => "PKLITE (DOS exe/com)";

  private const int EpBytesLength = 1000;
  private const int MaxRelocs = 65535;
  private const int MaxOutput = 1048576;

  public string? Detect(ReadOnlySpan<byte> file) {
    if (IsMz(file)) {
      var exe = DosExe.TryRead(file);
      if (exe == null)
        return null;
      var entry = AdjustedEntryPoint(file, exe, out _);
      var ep = DosExe.ReadPadded(file, entry, EpBytesLength);
      return DetectIntroClass(ep, 0xFF) == 0 ? null : VersionString(ReadU16(file, 28)) + " (exe)";
    }
    return file.Length <= 65280 && DetectComQuick(file, out var verPos)
      ? VersionString(ReadU16(file, verPos)) + " (com)"
      : null;
  }

  public DosUnpacked Unpack(ReadOnlySpan<byte> file) {
    if (IsMz(file))
      return this.UnpackExe(file);
    if (file.Length > 65280 || !DetectComQuick(file, out var verPos))
      throw new InvalidDataException("PKLITE: stub not recognised.");
    return UnpackCom(file, verPos);
  }

  private static bool IsMz(ReadOnlySpan<byte> file) =>
    file.Length >= 2 && ((file[0] == 'M' && file[1] == 'Z') || (file[0] == 'Z' && file[1] == 'M'));

  private static int ReadU16(ReadOnlySpan<byte> file, long pos) =>
    pos >= 0 && pos + 2 <= file.Length ? BinaryPrimitives.ReadUInt16LittleEndian(file[(int)pos..]) : 0;

  private static int ReadU16(byte[] mem, long pos) =>
    pos >= 0 && pos + 2 <= mem.Length ? BinaryPrimitives.ReadUInt16LittleEndian(mem.AsSpan((int)pos)) : 0;

  /// <summary>The version word: low 12 bits the version, 0x1000 extra, 0x2000 large, 0x4000 load-high.</summary>
  private static string VersionString(int info) {
    var v = info & 0x0FFF;
    return string.Create(CultureInfo.InvariantCulture,
      $"{v >> 8}.{v & 0xFF:D2}{((info & 0x4000) != 0 ? "/h" : "")}{((info & 0x2000) != 0 ? "/l" : "/s")}{((info & 0x1000) != 0 ? "/e" : "")}");
  }

  // ── stub recognition ───────────────────────────────────────────────

  /// <summary>
  /// Byte-pattern match; the pattern is a string of byte values (each char 0-255) where '?'
  /// (0x3F) matches anything.
  /// </summary>
  private static bool Match(byte[] mem, long pos, string pattern) {
    if (pos < 0 || pos + pattern.Length > mem.Length)
      return false;
    for (var i = 0; i < pattern.Length; ++i)
      if (pattern[i] != '?' && mem[pos + i] != pattern[i])
        return false;
    return true;
  }

  /// <summary>First position in [start, end) where the whole pattern fits and matches, or -1.</summary>
  private static long Search(byte[] mem, long start, long end, string pattern) {
    if (start < 0) start = 0;
    if (end > mem.Length) end = mem.Length;
    for (var p = start; p + pattern.Length <= end; ++p)
      if (Match(mem, p, pattern))
        return p;
    return -1;
  }

  /// <summary>
  /// Classifies the entry-point code: 100 (v1.00-1.05), 112 (v1.12-1.15), 150 (v1.50-2.01),
  /// 250 (patched by UN2PACK), 90/91 (betas), 251 (Megalite); 0 when none.
  /// flags: 1 = normal versions, 2 = betas, 4 = Megalite.
  /// </summary>
  private static int DetectIntroClass(byte[] ep, int flags) {
    if ((flags & 1) != 0) {
      if (Match(ep, 0, "\xb8??\xba??\x8c\xdb\x03\xd8\x3b")) return 100;
      if (Match(ep, 0, "\xb8??\xba??\x05\x00\x00\x3b\x06")) return 112;
      if (Match(ep, 0, "\x50\xb8??\xba??\x05\x00\x00\x3b")) return 150;
      if (Match(ep, 0, "\x9c\xba?\x00\x2d?\x00\x81\xe1?\x00\x81")) return 250;
    }
    if ((flags & 2) != 0) {
      if (Match(ep, 0, "\x2e\x8c\x1e??\x8b\x1e\x02\x00\x8c\xda\x81")) return 90;
      if (Match(ep, 0, "\x2e\x8c\x1e??\xfc\x8c\xc8\x2e\x2b\x06")) return 91;
    }
    if ((flags & 4) != 0) {
      if (Match(ep, 0, "\xb8??\xba??\x05\x00\x00\x3b\x2d")) return 251;
    }
    return 0;
  }

  /// <summary>
  /// A "PKLAXFIX" startup patch sits in the last 17 bytes and jumps to the real stub; the
  /// unpacker starts from the patched-over entry point and treats the patch as outside the data.
  /// </summary>
  private static long AdjustedEntryPoint(ReadOnlySpan<byte> file, DosExe exe, out long patchPos) {
    patchPos = -1;
    var pos = exe.EndOfCode - 17;
    if (exe.EntryPoint != pos || pos < 0 || pos + 15 > file.Length || file[(int)pos + 8] != 0x05 || file[(int)pos + 12] != 0xB8)
      return exe.EntryPoint;
    patchPos = pos;
    var cs = BinaryPrimitives.ReadInt16LittleEndian(file[((int)pos + 9)..]);
    var ip = BinaryPrimitives.ReadUInt16LittleEndian(file[((int)pos + 13)..]);
    return exe.StartOfCode + cs * 16L + ip;
  }

  // ── EXE ────────────────────────────────────────────────────────────

  private sealed class State {
    public required byte[] Ep;
    public long StartOfCode;
    public long EntryPoint;
    public long EndOfCode;
    public bool DataBeforeDecoder;
    public int IntroClass;
    public int InitialKey;
    public long Position2;
    public bool Scrambled;
    public int ScrambleMethod; // 1 = xor, 2 = add
    public long ScrambledWordCount;
    public long PosOfLastScrambledWord;
    public long CopierPos;
    public long DecompressorPos;
    public long ApproxEndOfDecompressor;
    public Params P = new();

    public long IpToEpRel(long ip) => Math.Max(0, this.StartOfCode + (ip - 0x100) - this.EntryPoint);

    public long FollowJump(long pos) => pos >= EpBytesLength ? 0 : pos + 1 + this.Ep[pos];
  }

  private sealed class Params {
    public long DataPos;
    public int Extra; // 0 = no, 1 = yes, 2 = the "v1.23" variant (literals inverted)
    public bool Large;
    public bool V120;
    public byte OffsetXorKey;
  }

  private DosUnpacked UnpackExe(ReadOnlySpan<byte> file) {
    var exe = DosExe.TryRead(file) ?? throw new InvalidDataException("PKLITE: not an MZ executable.");
    var entry = AdjustedEntryPoint(file, exe, out var patchPos);
    var s = new State {
      Ep = DosExe.ReadPadded(file, entry, EpBytesLength),
      StartOfCode = exe.StartOfCode,
      EntryPoint = entry,
      EndOfCode = exe.EndOfCode,
    };
    var introFmt = DetectIntroClass(s.Ep, 0xFF);
    if (introFmt == 0)
      throw new InvalidDataException("PKLITE: stub not recognised.");
    var reported = ReadU16(file, 28);

    AnalyzeIntro(s, introFmt);
    AnalyzeDescrambler(s);
    if (s.Scrambled)
      Descramble(s);
    AnalyzeCopier(s);
    AnalyzeDecompressor(s);
    if (s.P.DataPos == 0)
      throw new InvalidDataException("PKLITE: compressed data not located.");

    long dataAreaEnd;
    if (s.DataBeforeDecoder) {
      s.ApproxEndOfDecompressor = (patchPos >= 0 ? patchPos : exe.EndOfCode) - s.EntryPoint;
      dataAreaEnd = s.EntryPoint;
    } else {
      s.ApproxEndOfDecompressor = s.P.DataPos - s.EntryPoint;
      dataAreaEnd = patchPos >= 0 ? exe.EntryPoint : exe.EndOfCode;
    }
    DetectExtra(s);
    DetectLargeAndV120(s);
    DetectObfuscatedOffsets(s);

    var (code, dataEnd) = Decompress(file, s.P, s.StartOfCode, (reported & 0x0FFF) == 0x100);

    var relocLength = dataAreaEnd - 8 - dataEnd;
    var (relocs, relocEnd) = s.P.Extra != 0
      ? ReadLongRelocs(file, dataEnd, relocLength, s.ScrambleMethod == 2)
      : ReadShortRelocs(file, dataEnd, relocLength);

    var footerCapacity = dataAreaEnd - relocEnd;
    if (footerCapacity < 8 || relocEnd + 8 > file.Length)
      throw new InvalidDataException("PKLITE: register footer missing.");
    var footer = (
      SS: BinaryPrimitives.ReadInt16LittleEndian(file[(int)relocEnd..]),
      SP: BinaryPrimitives.ReadUInt16LittleEndian(file[((int)relocEnd + 2)..]),
      CS: BinaryPrimitives.ReadInt16LittleEndian(file[((int)relocEnd + 4)..]),
      IP: BinaryPrimitives.ReadUInt16LittleEndian(file[((int)relocEnd + 6)..]));

    var header = ReadOriginalHeader(file, exe, s.P, footer, relocs.Length / 4)
      ?? ReconstructHeader(file, exe, s.DataBeforeDecoder, footer, relocs.Length / 4, code.Length);
    var output = WriteExe(file, exe, header, relocs, code, s.IntroClass is IntroBeta or IntroBetaLoadHigh);
    return new(VersionString(reported) + " (exe)", output);
  }

  private const int IntroBeta = 8, IntroBetaLoadHigh = 9, Intro100 = 10, Intro112 = 12, Intro114 = 14,
    Intro150 = 50, IntroUn2Pack = 100, IntroMegalite = 101;

  private static void AnalyzeIntro(State s, int introFmt) {
    var ep = s.Ep;
    if (Match(ep, 0, "\xb8??\xba"))
      s.InitialKey = ReadU16(ep, 4);
    else if (Match(ep, 0, "\x50\xb8??\xba"))
      s.InitialKey = ReadU16(ep, 5);

    switch (introFmt) {
      case 90:
        s.IntroClass = IntroBeta;
        s.DataBeforeDecoder = true;
        break;
      case 91:
        s.IntroClass = IntroBetaLoadHigh;
        s.DataBeforeDecoder = true;
        break;
      case 100:
        s.IntroClass = Intro100;
        s.Position2 = 16;
        break;
      case 112 when ep[13] == 0x73:
        s.IntroClass = Intro112;
        s.Position2 = 15;
        break;
      case 112 when ep[13] == 0x72:
        s.IntroClass = Intro114;
        s.Position2 = s.FollowJump(14);
        break;
      case 150 when ep[14] == 0x72:
        s.IntroClass = Intro150;
        s.Position2 = s.FollowJump(15);
        break;
      case 250:
        s.IntroClass = IntroUn2Pack;
        s.Position2 = 34;
        break;
      case 251 when ep[13] == 0x72:
        s.IntroClass = IntroMegalite;
        s.Position2 = s.FollowJump(14);
        break;
    }
    if (!s.DataBeforeDecoder && (s.IntroClass == 0 || s.Position2 == 0))
      throw new InvalidDataException("PKLITE: unknown stub intro.");
  }

  private static void AnalyzeDescrambler(State s) {
    var ep = s.Ep;
    var pos = s.Position2;
    if (s.IntroClass is Intro112 or Intro114 or Intro150 && pos + 200 <= EpBytesLength) {
      // (pattern, offset of the word count, of the end position, of the jump, of the operator)
      (string Pattern, int Count, int End, int Jump, int Op)[] descramblers = [
        ("\x2d\x20\x00\x8e\xd0\x2d??\x50\x52\xb9??\xbe??\x8b\xfe\xfd\x90\x49\x74?\xad\x92\x33\xc2\xab\xeb\xf6", 11, 14, 22, 25),
        ("\x8b\xfc\x81\xef??\x57\x57\x52\xb9??\xbe??\x8b\xfe\xfd\x49\x74?\xad\x92\x03\xc2\xab\xeb\xf6", 10, 13, 20, 23),
        ("\x8b\xfc\x81\xef??\x57\x57\x52\xb9??\xbe??\x8b\xfe\xfd\x90\x49\x74?\xad\x92\x03\xc2\xab\xeb\xf6", 10, 13, 21, 24),
        ("\x59\x2d\x20\x00\x8e\xd0\x51??\x00\x50\x80\x3e\x41\x01\xc3\x75\xe6\x52\xb8??\xbe??\x56\x56\x52\x50\x90???????\x74", 20, 23, 38, 45),
        ("\x2d\x20\x00????????????\xb9??\xbe????????\x74???\x03", 16, 19, 28, 31),
        ("\x2d\x20\x00????????????\xb9??\xbe?????????\x74???\x03", 16, 19, 29, 32),
        ("\x2d\x20\x00?????????????????\xb9??\xbe??????????\x74???\x03", 21, 24, 35, 38),
        ("\x8b\xfc\x81?????????????\xbb??\xbe??????\x74???\x03", 17, 20, 27, 30),
        ("\x59\x2d\x20\x00\x8e\xd0\x51\x2d??\x50\x52\xb9??\xbe??\x8b\xfe\xfd\x90\x49\x74?\xad\x92\x33", 13, 16, 24, 27),
      ];
      foreach (var (pattern, count, end, jump, op) in descramblers) {
        if (!Match(ep, pos, pattern))
          continue;
        s.Scrambled = true;
        s.ScrambleMethod = ep[pos + op] switch {
          0x33 => 1,
          0x03 => 2,
          _ => throw new InvalidDataException("PKLITE: unknown descrambler operation."),
        };
        s.ScrambledWordCount = Math.Max(0, ReadU16(ep, pos + count) - 1);
        s.PosOfLastScrambledWord = s.IpToEpRel(ReadU16(ep, pos + end));
        s.CopierPos = s.FollowJump(pos + jump);
        break;
      }
    }
    if (!s.Scrambled && !s.DataBeforeDecoder)
      s.CopierPos = s.Position2;
  }

  /// <summary>Each scrambled word is XORed with, or added to, the next one; the last with the initial DX.</summary>
  private static void Descramble(State s) {
    if (s.ScrambledWordCount < 1)
      return;
    if (s.PosOfLastScrambledWord + 2 > EpBytesLength)
      throw new InvalidDataException("PKLITE: scrambled region lies outside the stub.");
    var start = s.PosOfLastScrambledWord + 2 - s.ScrambledWordCount * 2;
    if (start < 0)
      throw new InvalidDataException("PKLITE: scrambled region lies outside the stub.");
    var ep = s.Ep;
    var current = ReadU16(ep, start);
    for (var pos = start; pos <= s.PosOfLastScrambledWord; pos += 2) {
      var next = pos == s.PosOfLastScrambledWord ? s.InitialKey : ReadU16(ep, pos + 2);
      var plain = s.ScrambleMethod == 2 ? (current + next) & 0xFFFF : current ^ next;
      BinaryPrimitives.WriteUInt16LittleEndian(ep.AsSpan((int)pos), (ushort)plain);
      current = next;
    }
  }

  private static void AnalyzeCopier(State s) {
    if (s.DataBeforeDecoder)
      return;
    var ep = s.Ep;
    var pos = s.CopierPos;
    if (pos == 0 || pos + 200 > EpBytesLength)
      throw new InvalidDataException("PKLITE: copier not found.");
    long field;
    long found;
    if ((found = Search(ep, pos, pos + 75, "\xb9??\x33\xff\x57\xbe??\xfc\xf3\xa5")) >= 0)
      field = found + 7;
    else if ((found = Search(ep, pos, pos + 75, "\xb9??\x33\xff\x57\xfc\xbe??\xf3\xa5\xcb")) >= 0)
      field = found + 8;
    else if ((found = Search(ep, pos, pos + 75, "\x57\xb9??\xbe??\xfc\xf3\xa5\xc3")) >= 0)
      field = found + 5;
    else if ((found = Search(ep, pos, pos + 75, "\xb9??\x33\xff\x56\xbe??\xfc\xf2\xa5\xca")) >= 0)
      field = found + 7;
    else if ((found = Search(ep, pos, pos + 75, "\xb9??\x2b\xff\x57\xbe??\xfc\xf3\xa5\xcb")) >= 0)
      field = found + 7;
    else
      throw new InvalidDataException("PKLITE: copier not recognised.");
    s.DecompressorPos = s.IpToEpRel(ReadU16(ep, field));
  }

  private static void AnalyzeDecompressor(State s) {
    var ep = s.Ep;
    if (s.DecompressorPos == 0 && s.DataBeforeDecoder) {
      if (Match(ep, 0x59, "\xf3\xa5\x2e\xa1????????\xcb\xfc"))
        s.DecompressorPos = 0x66;
      else if (Match(ep, 0x5b, "\xf3\xa5\x85\xed????????????\xcb\xfc"))
        s.DecompressorPos = 0x6c;
      else if (Match(ep, 0, "\x2e\x8c\x1e??\xfc\x8c\xc8\x2e\x2b\x06"))
        s.DecompressorPos = 0x5;
    }
    var pos = s.DecompressorPos;
    if (pos == 0 || pos + 200 > EpBytesLength)
      throw new InvalidDataException("PKLITE: decompressor not found.");

    if (Match(ep, pos, "\xfd\x8c\xdb\x53\x83\xc3"))
      s.P.DataPos = s.EntryPoint + s.IpToEpRel(ep[pos + 6] * 16L);
    else if (Match(ep, pos, "\xfd\x8c\xdb\x53\x81\xc3"))
      s.P.DataPos = s.EntryPoint + s.IpToEpRel(ReadU16(ep, pos + 6) * 16L);
    else if (Match(ep, pos, "\xfd\x5f\xc7\x85????\x4f\x4f\xbe??\x03\xf2\x8b\xca\xd1\xe9\xf3")) {
      s.P.DataPos = s.EntryPoint + 2 + s.IpToEpRel(ReadU16(ep, pos + 11));
      s.P.V120 = true;
    } else if (Match(ep, pos, "\xfd\x5f\x4f\x4f\xbe??\x03\xf2\x8b\xca\xd1\xe9\xf3")) {
      s.P.DataPos = s.EntryPoint + 2 + s.IpToEpRel(ReadU16(ep, pos + 5));
      s.P.V120 = true;
    } else if (Match(ep, pos, "\xfc\x8c\xc8\x2e\x2b\x06??\x8e\xd8\xbf"))
      s.P.DataPos = s.StartOfCode;
    else
      throw new InvalidDataException("PKLITE: decompressor not recognised.");
  }

  private static void DetectExtra(State s) {
    var ep = s.Ep;
    if (s.DecompressorPos == 0 || s.ApproxEndOfDecompressor == 0)
      throw new InvalidDataException("PKLITE: decompressor bounds unknown.");
    if (Search(ep, s.DecompressorPos, s.ApproxEndOfDecompressor, "\xad\x95\xb2\x10\x72\x08\xa4\xd1\xed\x4a\x74") >= 0) {
      s.P.Extra = 0;
      return;
    }
    var found = Search(ep, s.DecompressorPos, s.ApproxEndOfDecompressor, "\xad\x95\xb2\x10\x72\x0b\xac??\xaa\xd1\xed\x4a\x74");
    if (found >= 0 && ep[found + 7] == 0x32 && ep[found + 8] == 0xC2) {
      s.P.Extra = 1;
      return;
    }
    if (found >= 0 && ep[found + 7] == 0xF6 && ep[found + 8] == 0xD0) {
      s.P.Extra = 2; // literals inverted ("v1.23" variant)
      return;
    }
    throw new InvalidDataException("PKLITE: literal decoder not recognised.");
  }

  private static void DetectLargeAndV120(State s) {
    if (s.P.V120) {
      s.P.Large = false;
      return;
    }
    var ep = s.Ep;
    var found = Search(ep, s.ApproxEndOfDecompressor - 60, s.ApproxEndOfDecompressor,
      "\x01\x02\x00\x00\x03\x04\x05\x06\x00\x00\x00\x00\x00\x00\x00\x00\x07\x08\x09\x0a\x0b");
    if (found > 0) {
      s.P.Large = ep[found - 1] switch {
        0x09 => false,
        0x18 => true,
        _ => throw new InvalidDataException("PKLITE: compression size class not recognised."),
      };
      return;
    }
    // What is left should be v1.20 with large compression, which always uses extra compression.
    if (s.P.Extra != 0 && Search(ep, s.ApproxEndOfDecompressor - 50, s.ApproxEndOfDecompressor,
          "\x33\xc0\x8b\xd8\x8b\xc8\x8b\xd0\x8b\xe8\x8b\xf0\x8b") >= 0) {
      s.P.V120 = true;
      s.P.Large = true;
      return;
    }
    throw new InvalidDataException("PKLITE: compression size class not recognised.");
  }

  private static void DetectObfuscatedOffsets(State s) {
    if (!s.P.V120)
      return;
    var found = Search(s.Ep, s.DecompressorPos + 200, s.ApproxEndOfDecompressor, "\xac\x34?\x8a");
    if (found >= 0)
      s.P.OffsetXorKey = s.Ep[found + 2];
  }

  // ── decompression ──────────────────────────────────────────────────

  // Prefix codes: high 4 bits the code length, low 12 bits the code (read MSB-first).
  private static readonly ushort[] LengthsLarge = [
    0x2003, 0x3000, 0x4002, 0x4003, 0x4004, 0x500a, 0x500b, 0x500c,
    0x601a, 0x601b, 0x703a, 0x703b, 0x703c, 0x807a, 0x807b, 0x807c,
    0x90fa, 0x90fb, 0x90fc, 0x90fd, 0x90fe, 0x90ff, 0x601c, 0x2002,
  ];
  private static readonly ushort[] LengthsSmall = [
    0x2000, 0x3004, 0x3005, 0x400c, 0x400d, 0x400e, 0x400f, 0x3003,
    0x3002,
  ];
  private static readonly ushort[] Lengths120Large = [
    0x2003, 0x3000, 0x4005, 0x4006, 0x5006, 0x5007, 0x6008, 0x6009,
    0x7020, 0x7021, 0x7022, 0x7023, 0x8048, 0x8049, 0x804a, 0x9096,
    0x9097, 0x6013, 0x2002, 0x4007, 0x5005,
  ];
  private static readonly ushort[] Lengths120Small = [
    0x2003, 0x3000, 0x4004, 0x4005, 0x500e, 0x601e, 0x601f, 0x4006,
    0x2002, 0x4003, 0x4002,
  ];
  private static readonly ushort[] Offsets = [
    0x1001, 0x4000, 0x4001, 0x5004, 0x5005, 0x5006, 0x5007, 0x6010,
    0x6011, 0x6012, 0x6013, 0x6014, 0x6015, 0x6016, 0x702e, 0x702f,
    0x7030, 0x7031, 0x7032, 0x7033, 0x7034, 0x7035, 0x7036, 0x7037,
    0x7038, 0x7039, 0x703a, 0x703b, 0x703c, 0x703d, 0x703e, 0x703f,
  ];
  private static readonly ushort[] Offsets120 = [
    0x1001, 0x3000, 0x5004, 0x5005, 0x5006, 0x5007, 0x6010, 0x6011,
    0x6012, 0x6013, 0x6014, 0x6015, 0x702c, 0x702d, 0x702e, 0x702f,
    0x7030, 0x7031, 0x7032, 0x7033, 0x7034, 0x7035, 0x7036, 0x7037,
    0x7038, 0x7039, 0x703a, 0x703b, 0x703c, 0x703d, 0x703e, 0x703f,
  ];

  /// <summary>Lookup from (length, code) to symbol.</summary>
  private static Dictionary<int, int> BuildCodebook(ushort[] table) {
    var map = new Dictionary<int, int>(table.Length);
    for (var i = 0; i < table.Length; ++i)
      map.Add(table[i], i);
    return map;
  }

  private static readonly Dictionary<int, int> BookLengthsLarge = BuildCodebook(LengthsLarge);
  private static readonly Dictionary<int, int> BookLengthsSmall = BuildCodebook(LengthsSmall);
  private static readonly Dictionary<int, int> BookLengths120Large = BuildCodebook(Lengths120Large);
  private static readonly Dictionary<int, int> BookLengths120Small = BuildCodebook(Lengths120Small);
  private static readonly Dictionary<int, int> BookOffsets = BuildCodebook(Offsets);
  private static readonly Dictionary<int, int> BookOffsets120 = BuildCodebook(Offsets120);

  /// <summary>
  /// Bits come LSB-first from 16-bit little-endian words; the next word is fetched as soon as the
  /// previous one is used up, so the count left is never 0 between reads.
  /// </summary>
  private ref struct BitReader(ReadOnlySpan<byte> file, long pos) {
    private readonly ReadOnlySpan<byte> _file = file;
    public long Pos = pos;
    private uint _bits;
    public int Count;

    public void Refill() {
      if (this.Pos + 2 > this._file.Length)
        throw new InvalidDataException("PKLITE: unexpected end of file during decompression.");
      this._bits = BinaryPrimitives.ReadUInt16LittleEndian(this._file[(int)this.Pos..]);
      this.Pos += 2;
      this.Count = 16;
    }

    public int Bit() {
      var bit = (int)(this._bits & 1);
      this._bits >>= 1;
      if (--this.Count == 0)
        this.Refill();
      return bit;
    }

    public byte Byte() {
      if (this.Pos >= this._file.Length)
        throw new InvalidDataException("PKLITE: unexpected end of file during decompression.");
      return this._file[(int)this.Pos++];
    }

    public int Decode(Dictionary<int, int> book) {
      var code = 0;
      for (var length = 1; length <= 12; ++length) {
        code = (code << 1) | this.Bit();
        if (book.TryGetValue((length << 12) | code, out var symbol))
          return symbol;
      }
      throw new InvalidDataException("PKLITE: invalid prefix code.");
    }
  }

  private static (byte[] Code, long End) Decompress(ReadOnlySpan<byte> file, Params p, long startOfCode, bool allowPrehistory) {
    int longCode, ml2Code0, ml2Code1 = -1, lit0Code = -1, longBias;
    if (p.Large && p.V120) {
      longCode = 17; ml2Code0 = 18; ml2Code1 = 19; lit0Code = 20; longBias = 20;
    } else if (p.Large) {
      longCode = 22; ml2Code0 = 23; longBias = 25;
    } else if (p.V120) {
      longCode = 7; ml2Code0 = 8; ml2Code1 = 9; lit0Code = 10; longBias = 10;
    } else {
      longCode = 7; ml2Code0 = 8; longBias = 10;
    }
    var lengths = p.Large
      ? p.V120 ? BookLengths120Large : BookLengthsLarge
      : p.V120 ? BookLengths120Small : BookLengthsSmall;
    var offsets = p.V120 ? BookOffsets120 : BookOffsets;

    const int Mask = 8191;
    var window = new byte[Mask + 1];
    var windowPos = 0;
    var output = new MemoryStream();
    var written = 0L;
    var hasUncompressedArea = false;
    var uncompressedAreaInHistory = false;
    var uncompressedAreaEnd = 0L;

    void Emit(byte b) {
      if (written >= MaxOutput)
        throw new InvalidDataException("PKLITE: decompressed image exceeds 1 MiB.");
      output.WriteByte(b);
      ++written;
      window[windowPos] = b;
      windowPos = (windowPos + 1) & Mask;
    }

    void History(byte b) {
      window[windowPos] = b;
      windowPos = (windowPos + 1) & Mask;
    }

    void Write(byte b) {
      if (written >= MaxOutput)
        throw new InvalidDataException("PKLITE: decompressed image exceeds 1 MiB.");
      output.WriteByte(b);
      ++written;
    }

    var bits = new BitReader(file, p.DataPos);
    bits.Refill();
    while (true) {
      if (bits.Bit() == 0) {
        var literal = bits.Byte();
        if (p.Extra == 1)
          literal ^= (byte)bits.Count;
        else if (p.Extra == 2)
          literal ^= 0xFF;
        Emit(literal);
        continue;
      }

      var raw = bits.Decode(lengths);
      int length;
      var offsetHigh = 0;
      var haveOffsetHigh = false;
      if (raw < longCode)
        length = raw + 3;
      else if (raw == ml2Code0) {
        length = 2;
        haveOffsetHigh = true;
      } else if (raw == longCode) {
        var b = bits.Byte();
        if (b >= 0xFD) {
          if (b == 0xFD && p.Large) {
            // Uncompressed area: a length byte and that many stored bytes, restored behind the
            // 8-byte "PKLITE&\xA3" mark and the length the original carried there.
            hasUncompressedArea = true;
            int count = bits.Byte();
            var hack = KnownHistoryHack(file, startOfCode);
            uncompressedAreaInHistory = hack != null;
            for (var i = 0; i < 9; ++i)
              History(hack?[i] ?? 0);
            foreach (var m in "PKLITE&\xA3")
              Write((byte)m);
            Write((byte)count);
            for (var i = 0; i < count; ++i) {
              var stored = bits.Byte();
              History(uncompressedAreaInHistory ? stored : (byte)0);
              Write(stored);
            }
            uncompressedAreaEnd = written;
            continue;
          }
          if (b == 0xFE && p.Large)
            continue; // segment separator
          if (b == 0xFF)
            break;
          throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"PKLITE: unexpected code 0x{b:X2}."));
        }
        length = b + longBias;
      } else if (raw == lit0Code) {
        Emit(0);
        continue;
      } else if (raw == ml2Code1) {
        length = 2;
        offsetHigh = 1;
        haveOffsetHigh = true;
      } else
        throw new InvalidDataException("PKLITE: invalid match length code.");

      if (!haveOffsetHigh)
        offsetHigh = bits.Decode(offsets);
      var distance = (offsetHigh << 8) | (byte)(bits.Byte() ^ p.OffsetXorKey);

      if (hasUncompressedArea && !uncompressedAreaInHistory && distance > written - uncompressedAreaEnd)
        throw new InvalidDataException("PKLITE: match reaches back into an uncompressed area.");
      if (!(allowPrehistory && distance == written + 1) && (distance == 0 || distance > written))
        throw new InvalidDataException("PKLITE: match distance out of range.");
      var from = (windowPos - distance) & Mask;
      for (var i = 0; i < length; ++i) {
        Emit(window[from]);
        from = (from + 1) & Mask;
      }
    }
    return (output.ToArray(), bits.Pos);
  }

  /// <summary>
  /// PKZIP.EXE 2.04c/2.04e (registered) were packed by an internal PKLITE whose uncompressed
  /// area leaves 9 bytes in the history that later matches use; those two files are known by
  /// the bytes at a fixed spot and get their 9 bytes back.
  /// </summary>
  private static byte[]? KnownHistoryHack(ReadOnlySpan<byte> file, long startOfCode) {
    (int Pos, byte[] Bytes)[] known = [
      (19652, [0xb8, 0x1d, 0xf9, 0x7f, 0x17, 0x14, 0xd3, 0xb5, 0xbe]),
      (20084, [0xaa, 0x8a, 0x07, 0xa4, 0x89, 0xc9, 0x97, 0x21, 0x58]),
    ];
    foreach (var (pos, bytes) in known) {
      var at = startOfCode + pos;
      if (at + 9 <= file.Length && file.Slice((int)at, 9).SequenceEqual(bytes))
        return bytes;
    }
    return null;
  }

  // ── relocations, header, output ────────────────────────────────────

  /// <summary>Groups of (count byte, segment word, count offset words), ended by a zero count.</summary>
  private static (byte[] Table, long End) ReadShortRelocs(ReadOnlySpan<byte> file, long pos, long length) {
    var end = Math.Min(pos + length, file.Length);
    var table = new MemoryStream();
    Span<byte> entry = stackalloc byte[4];
    var total = 0;
    while (true) {
      if (pos + 1 > end)
        throw new InvalidDataException("PKLITE: relocation table is truncated.");
      int count = file[(int)pos++];
      if (count == 0)
        break;
      if (total + count > MaxRelocs || pos + 2 + count * 2L > end)
        throw new InvalidDataException("PKLITE: relocation table is truncated.");
      var segment = BinaryPrimitives.ReadUInt16LittleEndian(file[(int)pos..]);
      pos += 2;
      for (var i = 0; i < count; ++i) {
        file.Slice((int)pos, 2).CopyTo(entry);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], segment);
        table.Write(entry);
        pos += 2;
        ++total;
      }
    }
    return (table.ToArray(), pos);
  }

  /// <summary>
  /// Extra-compression layout: per 0x0FFF-paragraph step, a count word then that many offset
  /// words (big-endian when the stub descrambles by addition), ended by a count of 0xFFFF.
  /// </summary>
  private static (byte[] Table, long End) ReadLongRelocs(ReadOnlySpan<byte> file, long pos, long length, bool bigEndian) {
    var end = Math.Min(pos + length, file.Length);
    var table = new MemoryStream();
    Span<byte> entry = stackalloc byte[4];
    var total = 0;
    var segment = 0L;
    while (true) {
      if (pos + 2 > end)
        throw new InvalidDataException("PKLITE: relocation table is truncated.");
      var count = BinaryPrimitives.ReadUInt16LittleEndian(file[(int)pos..]);
      pos += 2;
      if (count == 0xFFFF)
        break;
      if (segment > 0xFFFF || total + count > MaxRelocs || pos + count * 2L > end)
        throw new InvalidDataException("PKLITE: relocation table is truncated.");
      for (var i = 0; i < count; ++i) {
        var offset = bigEndian
          ? BinaryPrimitives.ReadUInt16BigEndian(file[(int)pos..])
          : BinaryPrimitives.ReadUInt16LittleEndian(file[(int)pos..]);
        BinaryPrimitives.WriteUInt16LittleEndian(entry, offset);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], (ushort)segment);
        table.Write(entry);
        pos += 2;
        ++total;
      }
      segment += 0x0FFF;
    }
    return (table.ToArray(), pos);
  }

  /// <summary>The header of the restored program: its bytes from "MZ" up to the relocation table.</summary>
  private sealed record GuestHeader(byte[] Bytes, int RelocTablePos, long StartOfCode, long EndOfCode);

  private static GuestHeader Guest(byte[] header) {
    var info = DosExe.TryRead(header.Length >= 28 ? header : [.. header, .. new byte[28 - header.Length]])!;
    return new(header, Math.Max(28, info.RelocTablePos), info.StartOfCode, info.EndOfCode);
  }

  /// <summary>
  /// Files without extra compression keep the original header (without "MZ") after the
  /// packed file's own header; it is used when its registers and relocation count agree.
  /// </summary>
  private static GuestHeader? ReadOriginalHeader(ReadOnlySpan<byte> file, DosExe exe, Params p,
      (short SS, ushort SP, short CS, ushort IP) footer, int relocCount) {
    if (p.Extra != 0)
      return null;
    long pos = exe.RelocTablePos + 4L * exe.NumRelocs;
    var length = exe.StartOfCode - pos;
    if (length < 26 || pos + 26 > file.Length)
      return null;
    var origRelocPos = ReadU16(file, pos + 22);
    if (origRelocPos >= 28 && origRelocPos < 2 + length)
      length = origRelocPos - 2;
    if (ReadU16(file, pos) > 511 || ReadU16(file, pos + 2) == 0)
      return null;
    var bytes = new byte[2 + length];
    bytes[0] = (byte)'M';
    bytes[1] = (byte)'Z';
    DosExe.ReadPadded(file, pos, (int)length).CopyTo(bytes, 2);
    var guest = DosExe.TryRead(bytes.Length >= 28 ? bytes : [.. bytes, .. new byte[28 - bytes.Length]])!;
    if (guest.RegSS != footer.SS || guest.RegSP != footer.SP || guest.RegCS != footer.CS || guest.RegIP != footer.IP)
      return null;
    if (guest.NumRelocs != relocCount)
      return null;
    return Guest(bytes);
  }

  /// <summary>Builds a header from the footer registers when no copy of the original exists.</summary>
  private static GuestHeader ReconstructHeader(ReadOnlySpan<byte> file, DosExe exe, bool dataBeforeDecoder,
      (short SS, ushort SP, short CS, ushort IP) footer, int relocCount, int codeLength) {
    long minMem = exe.MinAlloc;
    long maxMem = exe.MaxAlloc == 0 ? 0xFFFF : exe.MaxAlloc;
    if (!dataBeforeDecoder) {
      var pos = exe.EntryPoint;
      var b = pos < file.Length && pos >= 0 ? file[(int)pos++] : (byte)0;
      if (b == 0x50)
        b = pos < file.Length ? file[(int)pos++] : (byte)0;
      if (b == 0xB8) {
        var n = ((long)ReadU16(file, pos) << 4) + 0x100 - codeLength;
        if (n >= 0)
          minMem = (n + 0xF) >> 4;
      }
    }
    if (maxMem < minMem)
      maxMem = minMem;
    var startOfCode = (28 + relocCount * 4 + 15) & ~15;
    var endOfCode = (long)startOfCode + codeLength;
    var h = new byte[28];
    BinaryPrimitives.WriteUInt16LittleEndian(h, 0x5A4D);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(2), (ushort)(endOfCode % 512));
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), (ushort)((endOfCode + 511) / 512));
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), (ushort)relocCount);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), (ushort)(startOfCode / 16));
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), (ushort)minMem);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(12), (ushort)maxMem);
    BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(14), footer.SS);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(16), footer.SP);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), footer.IP);
    BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), footer.CS);
    BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(24), 28);
    return Guest(h);
  }

  /// <summary>
  /// Header (up to its relocation table position), relocation table, zero padding (or cut) to the
  /// start of the image, the image, then the overlay when it lands where the header says the
  /// image ends.
  /// </summary>
  private static byte[] WriteExe(ReadOnlySpan<byte> file, DosExe exe, GuestHeader header, byte[] relocs, byte[] code, bool mayBeta) {
    var output = new MemoryStream();
    output.Write(header.Bytes, 0, Math.Min(header.Bytes.Length, header.RelocTablePos));
    output.SetLength(header.RelocTablePos);
    output.Position = header.RelocTablePos;
    output.Write(relocs);
    output.SetLength(header.StartOfCode);
    output.Position = header.StartOfCode;
    output.Write(code);

    var overlay = exe.OverlayLength;
    if (overlay > 0) {
      // The May 1990 beta mislabels files whose size is a multiple of 512; their overlay goes
      // after the image regardless.
      var betaSizeBug = mayBeta && ReadU16(header.Bytes, 2) == 0;
      if (betaSizeBug || output.Length == header.EndOfCode)
        output.Write(file[(int)exe.EndOfCode..]);
    }
    return output.ToArray();
  }

  // ── COM ────────────────────────────────────────────────────────────

  private static bool DetectComQuick(ReadOnlySpan<byte> file, out int verPos) {
    verPos = 0;
    var b = DosExe.ReadPadded(file, 0, 10);
    if (b[0] == 0xB8 && b[3] == 0xBA && b[6] == 0x3B && b[7] == 0xC4) {
      if (b[9] == 0x67) { verPos = 44; return true; } // v1.00-1.14
      if (b[9] == 0x69) { verPos = 46; return true; } // v1.15
    } else if (b[0] == 0x50 && b[1] == 0xB8 && b[4] == 0xBA && b[7] == 0x3B) {
      verPos = 46; // v1.50-2.01
      return true;
    } else if (b[0] == 0xBA && b[3] == 0xA1 && b[6] == 0x2D && b[7] == 0x20) {
      verPos = 36; // v1.00 beta
      return true;
    }
    return false;
  }

  private static DosUnpacked UnpackCom(ReadOnlySpan<byte> file, int verPos) {
    var ep = DosExe.ReadPadded(file, 0, EpBytesLength);
    var reported = ReadU16(file, verPos);

    long position2;
    if (Match(ep, 0, "\xb8??\xba??\x3b\xc4\x73"))
      position2 = 10;
    else if (Match(ep, 0, "\x50\xb8??\xba??\x3b\xc4\x73"))
      position2 = 11;
    else if (Match(ep, 0, "\xba??\xa1\x02\x00\x2d??\x8c\xcb??????\x77"))
      position2 = 18 + 1 + ep[18];
    else
      throw new InvalidDataException("PKLITE: COM stub not recognised.");

    long decompressorPos = 0;
    if (position2 + 100 <= EpBytesLength) {
      if (Match(ep, position2, "\x8b\xc4\x2d??\x25\xf0\xff\x8b\xf8\xb9??\xbe"))
        decompressorPos = ReadU16(ep, position2 + 14) - 0x100;
      else if (Match(ep, position2, "\x8b\xc4\x2d??\x90\x25\xf0\xff\x8b\xf8\xb9??\x90\xbe"))
        decompressorPos = ReadU16(ep, position2 + 16) - 0x100;
      else if (Match(ep, position2, "\xfa\xbc\x00\x02\x8e\xd0\xfb"))
        decompressorPos = position2 + 24;
    }
    if (decompressorPos == 0)
      throw new InvalidDataException("PKLITE: COM copier not recognised.");

    long dataPos = 0;
    if (decompressorPos > 0 && decompressorPos + 100 <= EpBytesLength) {
      if (Match(ep, decompressorPos, "\xfd\x8b\xf8\x4f\x4f\xbe"))
        dataPos = ReadU16(ep, decompressorPos + 6) + 2 - 0x100;
      else if (Match(ep, decompressorPos, "\xfd\xbe??\x03\xf2\x8b\xfa\x4f\x4f"))
        dataPos = ReadU16(ep, decompressorPos + 2) + 2 - 0x100;
    }
    if (dataPos < 1 || dataPos > file.Length)
      throw new InvalidDataException("PKLITE: COM compressed data not located.");

    var (code, _) = Decompress(file, new Params { DataPos = dataPos }, 0, (reported & 0x0FFF) == 0x100);
    return new(VersionString(reported) + " (com)", code, "unpacked.com");
  }
}
