#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.ExePackers;

/// <summary>
/// The byte-wise operations a Yoda's Crypter decryption loop is built from.
/// </summary>
/// <remarks>
/// Yoda's Crypter (Ashkbiz Danehkar) does not compress: it leaves every section
/// at its original file offset and runs a per-byte cipher over the ones that
/// carry code or writable data. The cipher itself is emitted fresh for every
/// build — a random sequence of <c>al</c> operations, some of them mixing in
/// <c>cl</c>, which the stub's <c>loop</c> instruction counts down from the
/// region length. Because the sequence has to be executable it is stored in the
/// clear, so reading it back off the stub reverses the encryption exactly.
/// </remarks>
public enum YodaByteOpKind {
  /// <summary>
  /// Specifies the add immediate option.
  /// </summary>
  AddImmediate,
  /// <summary>
  /// Specifies the subtract immediate option.
  /// </summary>
  SubtractImmediate,
  /// <summary>
  /// Specifies the xor immediate option.
  /// </summary>
  XorImmediate,
  /// <summary>
  /// Specifies the add counter option.
  /// </summary>
  AddCounter,
  /// <summary>
  /// Specifies the subtract counter option.
  /// </summary>
  SubtractCounter,
  /// <summary>
  /// Specifies the xor counter option.
  /// </summary>
  XorCounter,
  /// <summary>
  /// Specifies the rotate left option.
  /// </summary>
  RotateLeft,
  /// <summary>
  /// Specifies the rotate right option.
  /// </summary>
  RotateRight,
  /// <summary>
  /// Specifies the increment option.
  /// </summary>
  Increment,
  /// <summary>
  /// Specifies the decrement option.
  /// </summary>
  Decrement,
  /// <summary>
  /// Specifies the not option.
  /// </summary>
  Not,
  /// <summary>
  /// Specifies the negate option.
  /// </summary>
  Negate,
}

/// <summary>
/// Represents a yoda byte op.
/// </summary>
public readonly record struct YodaByteOp(YodaByteOpKind Kind, byte Operand);

/// <summary>What <see cref="YodaCrypterStub"/> recovered from a packed image.</summary>
public sealed record YodaCrypterStubInfo(
  byte[] DecryptedImage,
  uint? OriginalEntryPoint,
  IReadOnlyList<YodaByteOp> StubCipher,
  IReadOnlyList<YodaByteOp> SectionCipher,
  IReadOnlyList<string> DecryptedSections,
  IReadOnlyList<string> SkippedSections,
  uint StubSectionRva);

/// <summary>
/// Static unpacker for Yoda's Crypter protected Win32 images.
/// </summary>
/// <remarks>
/// <para>The stub is layered. The entry point holds a plaintext prologue that
/// establishes a load-delta in <c>ebp</c> (<c>call $+5</c> / <c>pop ebp</c> /
/// <c>sub ebp, imm32</c>), sets <c>ecx</c> to a region length and <c>edx</c> to
/// a region address, and then runs an inline <c>lodsb</c> … <c>stosb</c> /
/// <c>loop</c> cipher over the rest of the stub. Once that body is decrypted it
/// exposes a section walker that skips <c>.rsrc</c>, <c>.reloc</c>, <c>.rdata</c>,
/// <c>.idata</c>, <c>.edata</c>, <c>.tls</c> and the stub's own <c>yC</c>
/// section and calls a second cipher loop over every remaining section's
/// <c>SizeOfRawData</c> bytes.</para>
/// <para>All of that is reversible from the file alone: this type walks the two
/// cipher loops, replays them backwards, and reads the original entry point out
/// of the slot the stub restores it from. Only the import directory is not
/// recoverable — Yoda's Crypter overwrites it with its own descriptor format and
/// nibble-swaps the hint/name strings — so the result is the original section
/// payload, not a runnable executable.</para>
/// <para>Reversed from the packed samples in the chesvectain/PackingData corpus;
/// no packer or unpacker source was consulted. Format background:
/// <c>https://sourceforge.net/projects/yodap/</c>.</para>
/// </remarks>
public static class YodaCrypterStub {

  /// <summary>Instruction-walk budget; the prologue and cipher loops are far shorter than this.</summary>
  private const int _MAX_STEPS = 512;

  /// <summary>Longest junk gap a polymorphic short jump is allowed to skip over.</summary>
  private const int _MAX_JUNK_JUMP = 0x20;

  /// <summary>
  /// Performs the try unpack operation.
  /// </summary>
  public static bool TryUnpack(ReadOnlySpan<byte> image, out YodaCrypterStubInfo? info) {
    info = null;
    try {
      info = Unpack(image);
      return true;
    } catch (InvalidDataException) {
      return false;
    } catch (ArgumentOutOfRangeException) {
      return false;
    }
  }

  /// <summary>Layer budget for images the packer was run over more than once.</summary>
  private const int _MAX_PASSES = 4;

  /// <summary>
  /// Peels every Yoda's Crypter layer the image carries. Packing an already
  /// packed file just appends a second <c>yC</c> section, and the outer walker
  /// skips <c>yC</c> sections, so the inner stub survives the outer pass intact
  /// and the same walk applies again.
  /// </summary>
  public static YodaCrypterStubInfo Unpack(ReadOnlySpan<byte> image) {
    var info = UnpackOnce(image);
    var decrypted = new List<string>(info.DecryptedSections);
    var skipped = new List<string>(info.SkippedSections);
    var walked = new HashSet<uint> { info.StubSectionRva };

    for (var pass = 1; pass < _MAX_PASSES; ++pass) {
      // Only a pass that recovered an entry point may be followed by another.
      // A stub whose entry-point slot could not be read leaves the header still
      // pointing into that stub, and running the walk again would decrypt every
      // section a second time and destroy the plaintext just recovered.
      if (info.OriginalEntryPoint is null)
        break;

      YodaCrypterStubInfo next;
      try {
        next = UnpackOnce(info.DecryptedImage);
      } catch (InvalidDataException) {
        break;
      } catch (ArgumentOutOfRangeException) {
        break;
      }

      // Belt and braces: never walk the same stub section twice.
      if (!walked.Add(next.StubSectionRva))
        break;

      foreach (var name in next.DecryptedSections)
        if (!decrypted.Contains(name)) decrypted.Add(name);
      foreach (var name in next.SkippedSections)
        if (!skipped.Contains(name)) skipped.Add(name);
      info = next;
    }

    return info with { DecryptedSections = decrypted, SkippedSections = skipped };
  }

  private static YodaCrypterStubInfo UnpackOnce(ReadOnlySpan<byte> image) {
    var pe = YodaPeView.Parse(image);
    var stubSection = pe.FindStubSection() ?? throw new InvalidDataException("Yoda's Crypter: no yC section.");
    var stubStartRva = stubSection.VirtualAddress;
    var stubEndRva = stubStartRva + Math.Max(stubSection.VirtualSize, stubSection.RawSize);
    if (pe.EntryPoint < stubStartRva || pe.EntryPoint >= stubEndRva)
      throw new InvalidDataException("Yoda's Crypter: entry point is outside the yC section.");

    var working = image.ToArray();
    var stub = working.AsSpan((int)stubSection.RawOffset, (int)stubSection.RawSize).ToArray();
    var stubBase = pe.ImageBase + stubStartRva;

    // --- layer 1: the plaintext prologue decrypts the rest of the stub -----
    var prologue = Walk(stub, stubBase, pe.ImageBase + pe.EntryPoint);
    var stubCipher = ReadCipher(prologue);
    var (delta, length, target) = ReadPrologueOperands(prologue);
    var bodyOffset = checked((int)(delta + target - stubBase));
    if (bodyOffset < 0 || length < 0 || bodyOffset + length > stub.Length)
      throw new InvalidDataException("Yoda's Crypter: stub body range is out of bounds.");
    Decrypt(stub, bodyOffset, length, stubCipher);

    // --- layer 2: the decrypted body carries the section cipher ------------
    var walkerOffset = IndexOf(stub, _SECTION_WALKER_ANCHOR);
    if (walkerOffset < 0)
      throw new InvalidDataException("Yoda's Crypter: section walker not found in the decrypted stub.");
    var cipherRva = stubBase
      + (ulong)(walkerOffset + _SECTION_WALKER_ANCHOR.Length + 4)
      + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(stub.AsSpan(walkerOffset + _SECTION_WALKER_ANCHOR.Length));
    var sectionCipher = ReadCipher(Walk(stub, stubBase, cipherRva));

    // --- the walker's own skip list, read out of its compare table ---------
    var skip = ReadSkipList(stub);

    // --- replay the section cipher over every section the stub touches -----
    var decrypted = new List<string>();
    var skipped = new List<string>();
    foreach (var section in pe.Sections) {
      if (section.RawOffset == 0 || section.RawSize == 0)
        continue;
      if (skip.Contains(BinaryPrimitives.ReadUInt32LittleEndian(section.RawName))) {
        skipped.Add(section.Name);
        continue;
      }
      if (section.RawOffset + section.RawSize > (uint)working.Length)
        continue;
      Decrypt(working, (int)section.RawOffset, (int)section.RawSize, sectionCipher);
      decrypted.Add(section.Name);
    }

    var entryPoint = ReadOriginalEntryPoint(stub, stubBase, delta);
    if (entryPoint is { } rva)
      BinaryPrimitives.WriteUInt32LittleEndian(working.AsSpan(pe.EntryPointFieldOffset), rva);

    return new(working, entryPoint, stubCipher, sectionCipher, decrypted, skipped, stubStartRva);
  }

  /// <summary><c>mov esi, ds:[esi+0xc]</c> / <c>add esi, eax</c> / <c>call rel32</c> — the walker's call into the section cipher.</summary>
  private static ReadOnlySpan<byte> _SECTION_WALKER_ANCHOR => [0x3E, 0x8B, 0x76, 0x0C, 0x03, 0xF0, 0xE8];

  /// <summary><c>cmp dword ptr ds:[esi], imm32</c> — one entry of the walker's section-name skip table.</summary>
  private static ReadOnlySpan<byte> _SKIP_ENTRY_ANCHOR => [0x3E, 0x81, 0x3E];

  /// <summary>
  /// The stub restores the original entry point as
  /// <c>ror(imageBase + [oepSlot], 7)</c> into the saved <c>ebx</c>, then lets an
  /// exception handler rotate it back into <c>Eip</c>; the slot therefore holds
  /// the entry point RVA verbatim.
  /// </summary>
  private static uint? ReadOriginalEntryPoint(byte[] stub, ulong stubBase, ulong delta) {
    // 8b d5 81 c2 <imageBaseSlot> 8b 1a 8b d5 81 c2 <oepSlot> 03 1a c1 cb 07
    for (var i = 0; i + 23 <= stub.Length; ++i) {
      if (stub[i] != 0x8B || stub[i + 1] != 0xD5 || stub[i + 2] != 0x81 || stub[i + 3] != 0xC2) continue;
      if (stub[i + 8] != 0x8B || stub[i + 9] != 0x1A) continue;
      if (stub[i + 10] != 0x8B || stub[i + 11] != 0xD5 || stub[i + 12] != 0x81 || stub[i + 13] != 0xC2) continue;
      if (stub[i + 18] != 0x03 || stub[i + 19] != 0x1A) continue;
      if (stub[i + 20] != 0xC1 || stub[i + 21] != 0xCB || stub[i + 22] != 0x07) continue;
      var slot = BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(i + 14));
      var offset = (long)(delta + slot - stubBase);
      if (offset < 0 || offset + 4 > stub.Length) return null;
      return BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan((int)offset));
    }
    return null;
  }

  private static HashSet<uint> ReadSkipList(byte[] stub) {
    var skip = new HashSet<uint>();
    for (var i = 0; i + 7 <= stub.Length; ++i)
      if (stub[i] == _SKIP_ENTRY_ANCHOR[0] && stub[i + 1] == _SKIP_ENTRY_ANCHOR[1] && stub[i + 2] == _SKIP_ENTRY_ANCHOR[2])
        skip.Add(BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(i + 3)));
    return skip;
  }

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);

  /// <summary>Replays a cipher loop over <paramref name="length"/> bytes; <c>cl</c> counts down from the length.</summary>
  private static void Decrypt(byte[] buffer, int offset, int length, IReadOnlyList<YodaByteOp> cipher) {
    // The stub loads ecx with the region length and decrements it with `loop`,
    // so byte i sees cl = (length - i) & 0xFF. Pre-computing one 256-byte table
    // per counter value turns the replay into a table lookup per byte.
    var tables = new byte[256][];
    for (var counter = 0; counter < 256; ++counter) {
      var table = new byte[256];
      for (var value = 0; value < 256; ++value)
        table[value] = Apply((byte)value, (byte)counter, cipher);
      tables[counter] = table;
    }

    for (var i = 0; i < length; ++i)
      buffer[offset + i] = tables[(length - i) & 0xFF][buffer[offset + i]];
  }

  private static byte Apply(byte value, byte counter, IReadOnlyList<YodaByteOp> cipher) {
    foreach (var op in cipher)
      value = op.Kind switch {
        YodaByteOpKind.AddImmediate => (byte)(value + op.Operand),
        YodaByteOpKind.SubtractImmediate => (byte)(value - op.Operand),
        YodaByteOpKind.XorImmediate => (byte)(value ^ op.Operand),
        YodaByteOpKind.AddCounter => (byte)(value + counter),
        YodaByteOpKind.SubtractCounter => (byte)(value - counter),
        YodaByteOpKind.XorCounter => (byte)(value ^ counter),
        YodaByteOpKind.RotateLeft => Rol(value, op.Operand),
        YodaByteOpKind.RotateRight => Ror(value, op.Operand),
        YodaByteOpKind.Increment => (byte)(value + 1),
        YodaByteOpKind.Decrement => (byte)(value - 1),
        YodaByteOpKind.Not => (byte)~value,
        YodaByteOpKind.Negate => (byte)-value,
        _ => value,
      };
    return value;
  }

  private static byte Rol(byte value, int count) {
    count &= 7;
    return (byte)((value << count) | (value >> (8 - count)));
  }

  private static byte Ror(byte value, int count) {
    count &= 7;
    return (byte)((value >> count) | (value << (8 - count)));
  }

  /// <summary>One decoded step of the stub walk.</summary>
  private readonly record struct Step(ulong Address, byte Opcode, byte Modrm, uint Immediate, YodaByteOp? Op, StepKind Kind);

  private enum StepKind { Other, SelfCall, PopEbp, SubEbp, MovEcx, SubEcx, AddEdx, LoadString, StoreString, Cipher }

  /// <summary>
  /// Decodes the stub from <paramref name="start"/>, stepping over the junk
  /// bytes the polymorphic engine hides behind short forward jumps.
  /// </summary>
  private static List<Step> Walk(byte[] stub, ulong stubBase, ulong start) {
    var steps = new List<Step>();
    var pc = start;
    for (var n = 0; n < _MAX_STEPS; ++n) {
      var offset = (long)(pc - stubBase);
      if (offset < 0 || offset >= stub.Length)
        break;

      var at = (int)offset;
      var opcode = stub[at];

      // A short forward jump over a handful of bytes is the engine's junk
      // filler; follow it rather than decoding what it hops over.
      if (opcode == 0xEB && at + 2 <= stub.Length) {
        var target = pc + 2 + (ulong)(long)(sbyte)stub[at + 1];
        if (target > pc && target - pc <= _MAX_JUNK_JUMP) { pc = target; continue; }
        break;
      }
      if (opcode == 0xE9 && at + 5 <= stub.Length) {
        var target = pc + 5 + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(stub.AsSpan(at + 1));
        if (target > pc && target - pc <= _MAX_JUNK_JUMP) { pc = target; continue; }
        break;
      }

      if (!TryDecode(stub, at, pc, out var step, out var size))
        break;

      steps.Add(step);
      if (step.Kind == StepKind.StoreString)
        break;
      pc += (ulong)size;
    }
    return steps;
  }

  private static bool TryDecode(byte[] stub, int at, ulong pc, out Step step, out int size) {
    step = default;
    size = 0;
    var remaining = stub.Length - at;
    var opcode = stub[at];

    static Step Op(ulong pc, YodaByteOpKind kind, byte operand) => new(pc, 0, 0, operand, new(kind, operand), StepKind.Cipher);

    switch (opcode) {
      // flag/padding filler the engine sprinkles between the real operations
      case 0x90 or 0xF5 or 0xF8 or 0xF9 or 0xFC or 0xFD:
        step = new(pc, opcode, 0, 0, null, StepKind.Other); size = 1; return true;
      case 0xAC:
        step = new(pc, opcode, 0, 0, null, StepKind.LoadString); size = 1; return true;
      case 0xAA:
        step = new(pc, opcode, 0, 0, null, StepKind.StoreString); size = 1; return true;
      case 0xE2 when remaining >= 2:                        // loop rel8
        step = new(pc, opcode, 0, 0, null, StepKind.Other); size = 2; return true;
      case 0x04 when remaining >= 2:                        // add al, imm8
        step = Op(pc, YodaByteOpKind.AddImmediate, stub[at + 1]); size = 2; return true;
      case 0x2C when remaining >= 2:                        // sub al, imm8
        step = Op(pc, YodaByteOpKind.SubtractImmediate, stub[at + 1]); size = 2; return true;
      case 0x34 when remaining >= 2:                        // xor al, imm8
        step = Op(pc, YodaByteOpKind.XorImmediate, stub[at + 1]); size = 2; return true;
      case 0x02 when remaining >= 2 && stub[at + 1] == 0xC1: // add al, cl
        step = Op(pc, YodaByteOpKind.AddCounter, 0); size = 2; return true;
      case 0x2A when remaining >= 2 && stub[at + 1] == 0xC1: // sub al, cl
        step = Op(pc, YodaByteOpKind.SubtractCounter, 0); size = 2; return true;
      case 0x32 when remaining >= 2 && stub[at + 1] == 0xC1: // xor al, cl
        step = Op(pc, YodaByteOpKind.XorCounter, 0); size = 2; return true;
      case 0xC0 when remaining >= 3 && stub[at + 1] == 0xC0: // rol al, imm8
        step = Op(pc, YodaByteOpKind.RotateLeft, stub[at + 2]); size = 3; return true;
      case 0xC0 when remaining >= 3 && stub[at + 1] == 0xC8: // ror al, imm8
        step = Op(pc, YodaByteOpKind.RotateRight, stub[at + 2]); size = 3; return true;
      case 0xFE when remaining >= 2 && stub[at + 1] == 0xC0: // inc al
        step = Op(pc, YodaByteOpKind.Increment, 0); size = 2; return true;
      case 0xFE when remaining >= 2 && stub[at + 1] == 0xC8: // dec al
        step = Op(pc, YodaByteOpKind.Decrement, 0); size = 2; return true;
      case 0xF6 when remaining >= 2 && stub[at + 1] == 0xD0: // not al
        step = Op(pc, YodaByteOpKind.Not, 0); size = 2; return true;
      case 0xF6 when remaining >= 2 && stub[at + 1] == 0xD8: // neg al
        step = Op(pc, YodaByteOpKind.Negate, 0); size = 2; return true;

      // prologue scaffolding
      case 0x55 or 0x53 or 0x56 or 0x57 or 0x60 or 0x5E or 0x5F or 0x5B or 0x61:
        step = new(pc, opcode, 0, 0, null, StepKind.Other); size = 1; return true;
      case 0x5D:
        step = new(pc, opcode, 0, 0, null, StepKind.PopEbp); size = 1; return true;
      case 0x8B when remaining >= 2 && stub[at + 1] is 0xEC or 0xD5 or 0xF7 or 0xFE or 0xF8 or 0xC1:
        step = new(pc, opcode, stub[at + 1], 0, null, StepKind.Other); size = 2; return true;
      case 0x33 when remaining >= 2 && stub[at + 1] is 0xC0 or 0xDB or 0xD2 or 0xC9:
        step = new(pc, opcode, stub[at + 1], 0, null, StepKind.Other); size = 2; return true;
      case 0x8D when remaining >= 2 && stub[at + 1] is 0x3A or 0x02 or 0x32:
        step = new(pc, opcode, stub[at + 1], 0, null, StepKind.Other); size = 2; return true;
      case 0xB9 when remaining >= 5:                        // mov ecx, imm32
        step = new(pc, opcode, 0, BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(at + 1)), null, StepKind.MovEcx);
        size = 5; return true;
      case 0x81 when remaining >= 6 && stub[at + 1] == 0xED: // sub ebp, imm32
        step = new(pc, opcode, stub[at + 1], BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(at + 2)), null, StepKind.SubEbp);
        size = 6; return true;
      case 0x81 when remaining >= 6 && stub[at + 1] == 0xE9: // sub ecx, imm32
        step = new(pc, opcode, stub[at + 1], BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(at + 2)), null, StepKind.SubEcx);
        size = 6; return true;
      case 0x81 when remaining >= 6 && stub[at + 1] == 0xC2: // add edx, imm32
        step = new(pc, opcode, stub[at + 1], BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(at + 2)), null, StepKind.AddEdx);
        size = 6; return true;
      case 0xE8 when remaining >= 5 && BinaryPrimitives.ReadUInt32LittleEndian(stub.AsSpan(at + 1)) == 0:
        step = new(pc, opcode, 0, 0, null, StepKind.SelfCall); size = 5; return true;
      default:
        return false;
    }
  }

  /// <summary>Collects the operations the loop applies between <c>lodsb</c> and <c>stosb</c>.</summary>
  private static List<YodaByteOp> ReadCipher(List<Step> steps) {
    var started = false;
    var cipher = new List<YodaByteOp>();
    foreach (var step in steps) {
      if (step.Kind == StepKind.LoadString) { started = true; cipher.Clear(); continue; }
      if (!started) continue;
      if (step.Kind == StepKind.StoreString) return cipher;
      if (step.Op is { } op) cipher.Add(op);
    }
    throw new InvalidDataException("Yoda's Crypter: no complete lodsb/stosb cipher loop found.");
  }

  /// <summary>
  /// Reads the load delta, region length and region address the prologue sets up.
  /// </summary>
  private static (ulong Delta, int Length, ulong Target) ReadPrologueOperands(List<Step> steps) {
    ulong? afterSelfCall = null, delta = null, target = null;
    uint? ecx = null;
    int? length = null;

    foreach (var step in steps)
      switch (step.Kind) {
        case StepKind.SelfCall: afterSelfCall = step.Address + 5; break;
        case StepKind.SubEbp when afterSelfCall is { } value: delta = value - step.Immediate; break;
        case StepKind.MovEcx: ecx = step.Immediate; break;
        case StepKind.SubEcx when ecx is { } value: length = (int)(value - step.Immediate); break;
        case StepKind.AddEdx: target ??= step.Immediate; break;
      }

    if (delta is null || length is null || target is null)
      throw new InvalidDataException("Yoda's Crypter: stub prologue did not yield a decryption range.");
    return (delta.Value, length.Value, target.Value);
  }
}
