#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.ExePackers;

/// <summary>
/// Generic DOS unpacker: runs a packed EXE or COM file's own decompression stub in
/// <see cref="DosCpu"/> under a minimal DOS, and stops where the original program starts.
/// <para>
/// The end of the stub is recognised the way IUP 0.6.7 (Frank Zago, public domain) recognises
/// it: a jump backwards by at least 2000 bytes (or to offset 0x100) taken while DS and ES both
/// hold the PSP segment, as DOS leaves them for a freshly started program; a long forward jump
/// starts a new phase (the stub moved itself). A jump back from outside the unpacked bytes onto
/// unpacked code that never ran is kept as a weaker candidate, taken when the program then
/// calls DOS. The image length comes from which instructions wrote what in the final phase
/// (<see cref="ImageExtent"/>), or from an MZ header the stub unpacked behind the image.
/// Unlike IUP, relocations are found by running the stub twice at two load segments and taking
/// every word that differs by exactly the distance between them.
/// </para>
/// </summary>
internal static class DosTracer {
  public sealed record Result(byte[] Executable, bool IsCom, long InstructionCount);

  private const ushort PspA = 0x1000, PspB = 0x1123, MemoryTop = 0xA000, EnvSegmentOffset = 0x40;
  private const int MinJump = 2000, MinImage = 256;
  private const long DefaultInstructionLimit = 200_000_000;

  private sealed class Run {
    public required DosCpu Cpu;
    public ushort Psp;
    public int Base; // linear address of the load image (or of offset 0x100 for COM)
    public int AdrSup;
    public bool Done;
    public bool Ended;
    public bool Tentative;
    public string? Failure;
    public ushort EndCS, EndIP, EndSS, EndSP;
    public byte[] Snapshot = [];
    public bool UsedFileIo;
    public long FilePos;
    public ushort BlockEnd;
    public ushort NextFree;
    // Image-extent bookkeeping for the final phase (since the last long forward jump).
    public long PhaseStart;
    public readonly long[] LastWrite = new long[DosCpu.MemorySize];
    public readonly long[] LastRun = new long[DosCpu.MemorySize];
    public readonly Dictionary<int, Writer> Writers = [];
    public readonly List<(int Address, long Time, int Reader)> PureReads = [];
    public readonly List<int> StepReads = [];
    public readonly List<(int Address, long Time)> Executed = [];
    public readonly HashSet<int> StepWrites = [];
  }

  private sealed class Writer {
    public long Count, First, Last;
    public int Min = int.MaxValue, Max = int.MinValue;
  }

  public static Result Unpack(ReadOnlySpan<byte> file, long instructionLimit = DefaultInstructionLimit) {
    var isCom = file.Length < 2 || !((file[0] == 'M' && file[1] == 'Z') || (file[0] == 'Z' && file[1] == 'M'));
    var bytes = file.ToArray();
    var a = Execute(bytes, isCom, PspA, instructionLimit);
    var b = Execute(bytes, isCom, PspB, instructionLimit);
    if (a.Snapshot.Length != b.Snapshot.Length)
      throw new InvalidDataException("DOS trace: the two runs restored images of different sizes.");

    var delta = (ushort)(PspB - PspA);
    if (!isCom && ((ushort)(b.EndCS - a.EndCS) != delta || (ushort)(b.EndSS - a.EndSS) != delta))
      throw new InvalidDataException("DOS trace: entry registers do not follow the load segment.");

    // Relocations: words that differ by exactly the load-segment distance.
    var relocs = new List<int>();
    var img = a.Snapshot;
    for (var i = 0; i < img.Length; ++i) {
      if (img[i] == b.Snapshot[i])
        continue;
      if (i + 1 >= img.Length)
        throw new InvalidDataException("DOS trace: load-dependent byte at the end of the image.");
      var wa = (ushort)(img[i] | (img[i + 1] << 8));
      var wb = (ushort)(b.Snapshot[i] | (b.Snapshot[i + 1] << 8));
      if ((ushort)(wb - wa) != delta)
        throw new InvalidDataException("DOS trace: load-dependent data that is not a relocation.");
      relocs.Add(i);
      ++i;
    }

    // An EXE that starts its program at PSP:0100 with no relocations is a COM file in an EXE
    // wrapper (DIET and others convert COM files this way); its image starts at PSP:0100 too.
    if (!isCom && relocs.Count == 0 && a.EndCS == a.Psp && a.EndIP == 0x100 && a.EndSS == a.Psp)
      return new(img, true, a.Cpu.Instructions);

    if (isCom) {
      if (relocs.Count != 0 || a.EndCS != a.Psp || a.EndIP != 0x100)
        throw new InvalidDataException("DOS trace: a COM file did not come back as a COM program.");
      return new(img, true, a.Cpu.Instructions);
    }

    var exe = DosExe.TryRead(bytes)!;
    var loadSeg = (ushort)(a.Psp + 0x10);

    // Packers that keep the original MZ header behind the image (DIET, ExeLITE) leave it in
    // memory; one whose registers are the ones the program starts with, and whose image size
    // is exactly the bytes before it, marks the image's end and gives its allocation fields.
    var stored = FindStoredHeader(img, (ushort)(a.EndCS - loadSeg), a.EndIP, (ushort)(a.EndSS - loadSeg), a.EndSP);
    if (stored != null) {
      img = img[..stored.Value.Offset];
      relocs.RemoveAll(r => r + 1 >= img.Length);
    }
    var relocTable = new byte[relocs.Count * 4];
    for (var k = 0; k < relocs.Count; ++k) {
      // Absolute relocation values are relative to the load segment.
      var wa = (ushort)(img[relocs[k]] | (img[relocs[k] + 1] << 8));
      var unrelocated = (ushort)(wa - loadSeg);
      img[relocs[k]] = (byte)unrelocated;
      img[relocs[k] + 1] = (byte)(unrelocated >> 8);
      BinaryPrimitives.WriteUInt16LittleEndian(relocTable.AsSpan(k * 4), (ushort)(relocs[k] & 0x0F));
      BinaryPrimitives.WriteUInt16LittleEndian(relocTable.AsSpan(k * 4 + 2), (ushort)(relocs[k] >> 4));
    }

    // Minimum allocation: keep the packed program's total memory (as IUP approximates it).
    var packedImage = exe.EndOfCode - exe.StartOfCode;
    var minAlloc = stored?.MinAlloc ?? (int)Math.Max(0, packedImage / 16 + exe.MinAlloc - img.Length / 16);
    var maxAlloc = stored?.MaxAlloc ?? (exe.MaxAlloc == 0xFFFF ? 0xFFFF : (int)Math.Clamp(packedImage / 16 + exe.MaxAlloc - img.Length / 16, minAlloc, 0xFFFF));
    var overlay = a.UsedFileIo ? [] : file[(int)Math.Min(exe.EndOfCode, file.Length)..];
    var output = DosExe.Build(relocTable, img, minAlloc, maxAlloc,
      (short)(a.EndSS - loadSeg), a.EndSP, a.EndIP, (short)(a.EndCS - loadSeg), overlay);
    return new(output, false, a.Cpu.Instructions);
  }

  private static (int Offset, int MinAlloc, int MaxAlloc)? FindStoredHeader(byte[] img, ushort cs, ushort ip, ushort ss, ushort sp) {
    for (var h = Math.Max(0, img.Length - 4096); h + 28 <= img.Length; ++h) {
      if (!((img[h] == 'M' && img[h + 1] == 'Z') || (img[h] == 'Z' && img[h + 1] == 'M')))
        continue;
      // DIET keeps flags in the high bits of the last-page count; a valid count is below 512.
      var copy = img.AsSpan(h, 28).ToArray();
      copy[3] &= 0x01;
      var hdr = DosExe.TryRead(copy);
      if (hdr == null || (ushort)hdr.RegCS != cs || hdr.RegIP != ip || (ushort)hdr.RegSS != ss || hdr.RegSP != sp)
        continue;
      if (hdr.EndOfCode - hdr.StartOfCode != h)
        continue;
      return (h, hdr.MinAlloc, hdr.MaxAlloc);
    }
    return null;
  }

  private static Run Execute(byte[] file, bool isCom, ushort psp, long limit) {
    var cpu = new DosCpu();
    var run = new Run { Cpu = cpu, Psp = psp };
    var mem = cpu.Memory;

    // Interrupt vectors point into the service segment; the vector number is the offset.
    for (var n = 0; n < 256; ++n) {
      BinaryPrimitives.WriteUInt16LittleEndian(mem.AsSpan(n * 4), (ushort)n);
      BinaryPrimitives.WriteUInt16LittleEndian(mem.AsSpan(n * 4 + 2), DosCpu.ServiceSegment);
    }

    // Environment just below the PSP: one variable, then the program's path.
    var envSeg = (ushort)(psp - EnvSegmentOffset);
    var env = Encoding.ASCII.GetBytes("PATH=C:\\\0\0\x01\0C:\\PROG.EXE\0");
    env.CopyTo(mem, envSeg * 16);

    // PSP.
    var p = psp * 16;
    mem[p] = 0xCD; mem[p + 1] = 0x20;
    BinaryPrimitives.WriteUInt16LittleEndian(mem.AsSpan(p + 2), MemoryTop);
    BinaryPrimitives.WriteUInt16LittleEndian(mem.AsSpan(p + 0x2C), envSeg);
    mem[p + 0x80] = 0;
    mem[p + 0x81] = 0x0D;
    run.BlockEnd = MemoryTop;
    run.NextFree = MemoryTop;

    if (isCom) {
      if (file.Length > 0xFF00)
        throw new InvalidDataException("DOS trace: COM file larger than a segment.");
      file.CopyTo(mem, p + 0x100);
      for (var i = 0; i < 4; ++i) cpu.S[i] = psp;
      cpu.IP = 0x100;
      cpu.R[DosCpu.SP] = 0xFFFE;
      run.Base = p + 0x100;
      run.AdrSup = run.Base + file.Length - 1;
    } else {
      var exe = DosExe.TryRead(file) ?? throw new InvalidDataException("DOS trace: not an MZ executable.");
      var start = (int)exe.StartOfCode;
      var length = (int)Math.Max(0, Math.Min(exe.EndOfCode, file.Length) - start);
      var loadSeg = (ushort)(psp + 0x10);
      if (loadSeg * 16 + length + exe.MinAlloc * 16 > MemoryTop * 16)
        throw new InvalidDataException("DOS trace: the program does not fit in conventional memory.");
      Array.Copy(file, start, mem, loadSeg * 16, length);
      for (var i = 0; i < exe.NumRelocs; ++i) {
        var at = exe.RelocTablePos + 4 * i;
        if (at + 4 > file.Length)
          throw new InvalidDataException("DOS trace: relocation table runs past the end of the file.");
        var off = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at));
        var seg = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 2));
        var lin = DosCpu.Linear((ushort)(seg + loadSeg), off);
        var w = cpu.Read16(lin);
        mem[lin] = (byte)(w + loadSeg);
        mem[(lin + 1) & 0xFFFFF] = (byte)((w + loadSeg) >> 8);
      }
      cpu.S[DosCpu.ES] = cpu.S[DosCpu.DS] = psp;
      cpu.S[DosCpu.CS] = (ushort)(loadSeg + exe.RegCS);
      cpu.IP = exe.RegIP;
      cpu.S[DosCpu.SS] = (ushort)(loadSeg + exe.RegSS);
      cpu.R[DosCpu.SP] = exe.RegSP;
      run.Base = loadSeg * 16;
      run.AdrSup = run.Base + length - 1;
    }

    var floor = run.Base;
    Array.Fill(run.LastWrite, -1L);
    cpu.OnWrite = linear => {
      if (cpu.StackWrite || linear < floor)
        return;
      var now = cpu.Instructions;
      run.LastWrite[linear] = now;
      run.StepWrites.Add(linear);
      if (!run.Writers.TryGetValue(cpu.CurrentInstruction, out var w))
        run.Writers[cpu.CurrentInstruction] = w = new Writer { First = now };
      ++w.Count;
      w.Last = now;
      w.Min = Math.Min(w.Min, linear - floor);
      w.Max = Math.Max(w.Max, linear - floor);
      if (linear > cpu.CurrentInstruction || linear < run.AdrSup)
        return;
      run.AdrSup = linear;
    };
    cpu.OnRead = linear => {
      if (linear >= floor)
        run.StepReads.Add(linear);
    };
    cpu.OnTransfer = (from, to) => {
      if (run.Done)
        return;
      if (to < from) {
        var newIp = cpu.IP;
        // A short jump back still ends the stub when it lands on bytes the stub unpacked in
        // this phase and never ran (aPACK -m keeps its stub right behind a small image).
        var newCode = run.LastWrite[to] >= run.PhaseStart && run.LastRun[to] < run.LastWrite[to];
        var dosLike = cpu.S[DosCpu.DS] == psp && cpu.S[DosCpu.ES] == psp;
        var strong = dosLike && (newIp == 0x100 || from - to >= MinJump);
        if (!strong && !newCode)
          return;
        var size = ImageExtent(run);
        // Weaker evidence, kept as the start only if the program then shows itself by calling
        // DOS: code outside the unpacked image jumping back onto bytes it unpacked and never ran,
        // whether short (aPACK -m keeps its stub right behind a small image) or without DS/ES
        // restored (aPACK -s).
        var weak = !strong && (from < run.Base || from >= run.Base + size);
        if (!strong && !weak)
          return;
        // A layered stub (WWPACK's protection stage) jumps back into a few bytes it decoded
        // first; that is not a program yet, so tracing goes on.
        if (size < MinImage)
          return;
        run.EndCS = cpu.S[DosCpu.CS];
        run.EndIP = cpu.IP;
        run.EndSS = cpu.S[DosCpu.SS];
        run.EndSP = cpu.R[DosCpu.SP];
        run.Snapshot = mem.AsSpan(run.Base, size).ToArray();
        run.Tentative = weak;
        if (strong) {
          run.Done = true;
          run.Ended = true;
        }
      } else if (to - from >= MinJump) {
        run.AdrSup = 0;
        run.PhaseStart = cpu.Instructions;
        run.Writers.Clear();
        run.PureReads.Clear();
        run.Executed.Clear();
      }
    };
    cpu.ServiceInterrupt = n => Service(run, file, n);

    while (!run.Done) {
      if (cpu.Instructions > limit)
        throw new InvalidDataException("DOS trace: the stub did not finish within the instruction limit.");
      if (cpu.Halted)
        throw new InvalidDataException("DOS trace: the stub halted.");
      // Code the stub runs from bytes it unpacked in this phase (a relocation routine
      // decompressed behind the image) is stub, not program.
      var at = DosCpu.Linear(cpu.S[DosCpu.CS], cpu.IP);
      run.LastRun[at] = cpu.Instructions + 1;
      if (at >= floor && run.LastWrite[at] >= run.PhaseStart && run.Executed.Count < 1_000_000)
        run.Executed.Add((at, cpu.Instructions + 1));
      try {
        cpu.Step();
      } catch (InvalidOperationException ex) {
        throw new InvalidDataException("DOS trace: " + ex.Message, ex);
      }
      // A read of a byte written in this phase, by an instruction that does not write it back,
      // is the stub consuming data it unpacked (relocations, a stored header).
      if (run.StepReads.Count > 0) {
        foreach (var r in run.StepReads)
          if (!run.StepWrites.Contains(r) && run.LastWrite[r] >= run.PhaseStart)
            run.PureReads.Add((r, cpu.Instructions, cpu.CurrentInstruction));
        run.StepReads.Clear();
      }
      run.StepWrites.Clear();
    }
    // The program called DOS after a long jump back into unpacked code: that jump was its start.
    if (!run.Ended && run.Tentative && run.Failure != null && run.Failure.StartsWith("DOS function", StringComparison.Ordinal))
      run.Ended = true;
    if (!run.Ended || run.Snapshot.Length == 0)
      throw new InvalidDataException("DOS trace: " + (run.Failure ?? "the original program's start was not reached."));
    return run;
  }

  /// <summary>
  /// Length of the restored image. The decompressor is the instruction that wrote most in the
  /// final phase together with the instructions writing into the same range while it ran
  /// (literal and match copies); the image is the run of bytes from the load address whose last
  /// write came after the decompressor started (bytes below its lowest write, which it left in
  /// place, included), cut where the stub, after the decompressor's last write, read back data
  /// it had unpacked (relocations, a stored header).
  /// </summary>
  private static int ImageExtent(Run run) {
    // The decompressor fills the image from (near) its start; a stub moving the compressed data
    // out of the way may write more bytes, but higher up.
    var dominant = run.Writers.Values.Where(w => w.Min <= 0x100).MaxBy(w => w.Count)
      ?? run.Writers.Values.MaxBy(w => w.Count);
    if (dominant == null)
      return 0;
    var helpers = run.Writers.Values
      .Where(w => w.Last >= dominant.First && w.First <= dominant.Last && w.Min <= dominant.Max && w.Max >= dominant.Min)
      .ToList();
    var start = helpers.Min(w => w.First);
    var last = helpers.Max(w => w.Last);
    var lowest = run.Base + helpers.Min(w => w.Min);
    var end = run.Base;
    while (end < DosCpu.MemorySize && (end < lowest || run.LastWrite[end] >= start))
      ++end;
    var helperAddresses = run.Writers.Where(kv => helpers.Contains(kv.Value)).Select(kv => kv.Key).ToHashSet();
    foreach (var (address, time, reader) in run.PureReads)
      if (time > last && address >= run.Base && address < end && !helperAddresses.Contains(reader))
        end = address;
    foreach (var (address, time) in run.Executed)
      if (time > last && address >= run.Base && address < end)
        end = address;
    return end - run.Base;
  }

  /// <summary>The handful of BIOS and DOS services a decompression stub uses.</summary>
  private static void Service(Run run, byte[] file, int n) {
    var cpu = run.Cpu;
    var ah = cpu.R[DosCpu.AX] >> 8;
    var al = cpu.R[DosCpu.AX] & 0xFF;
    void Carry(bool on) => cpu.Flags = on ? (ushort)(cpu.Flags | DosCpu.CF) : (ushort)(cpu.Flags & ~DosCpu.CF);
    void Stop(string why) {
      run.Done = true;
      run.Failure = why;
    }

    switch (n) {
      case 0x21:
        switch (ah) {
          case 0x30: cpu.R[DosCpu.AX] = 0x0005; cpu.R[DosCpu.BX] = 0; cpu.R[DosCpu.CX] = 0; break;
          case 0x4A: {
            var want = cpu.R[DosCpu.BX];
            if (cpu.S[DosCpu.ES] + want <= MemoryTop) {
              if (cpu.S[DosCpu.ES] == run.Psp) { run.BlockEnd = (ushort)(run.Psp + want); run.NextFree = Math.Max(run.NextFree == MemoryTop ? run.BlockEnd : run.NextFree, run.BlockEnd); }
              Carry(false);
            } else {
              cpu.R[DosCpu.BX] = (ushort)(MemoryTop - cpu.S[DosCpu.ES]);
              cpu.R[DosCpu.AX] = 8;
              Carry(true);
            }
            break;
          }
          case 0x48: {
            var want = cpu.R[DosCpu.BX];
            var free = run.NextFree == MemoryTop ? run.BlockEnd : run.NextFree;
            if (free + want <= MemoryTop) {
              cpu.R[DosCpu.AX] = free;
              run.NextFree = (ushort)(free + want);
              Carry(false);
            } else {
              cpu.R[DosCpu.BX] = (ushort)Math.Max(0, MemoryTop - free);
              cpu.R[DosCpu.AX] = 8;
              Carry(true);
            }
            break;
          }
          case 0x49: Carry(false); break;
          case 0x25:
            cpu.Write16(al * 4, cpu.R[DosCpu.DX]);
            cpu.Write16(al * 4 + 2, cpu.S[DosCpu.DS]);
            break;
          case 0x35:
            cpu.R[DosCpu.BX] = cpu.Read16(al * 4);
            cpu.S[DosCpu.ES] = cpu.Read16(al * 4 + 2);
            break;
          case 0x51: case 0x62: cpu.R[DosCpu.BX] = run.Psp; break;
          case 0x2F: cpu.S[DosCpu.ES] = run.Psp; cpu.R[DosCpu.BX] = 0x80; break;
          case 0x1A: break;
          case 0x19: cpu.R[DosCpu.AX] = (ushort)(cpu.R[DosCpu.AX] & 0xFF00 | 2); break;
          case 0x0E: cpu.R[DosCpu.AX] = (ushort)(cpu.R[DosCpu.AX] & 0xFF00 | 26); break;
          case 0x3D:
            run.UsedFileIo = true;
            run.FilePos = 0;
            cpu.R[DosCpu.AX] = 5;
            Carry(false);
            break;
          case 0x3E: Carry(false); break;
          case 0x3F: {
            if (cpu.R[DosCpu.BX] != 5) { cpu.R[DosCpu.AX] = 6; Carry(true); break; }
            var count = (int)Math.Min(cpu.R[DosCpu.CX], Math.Max(0, file.Length - run.FilePos));
            var at = DosCpu.Linear(cpu.S[DosCpu.DS], cpu.R[DosCpu.DX]);
            for (var i = 0; i < count; ++i)
              cpu.Write8(at + i, file[run.FilePos + i]);
            run.FilePos += count;
            cpu.R[DosCpu.AX] = (ushort)count;
            Carry(false);
            break;
          }
          case 0x42: {
            if (cpu.R[DosCpu.BX] != 5) { cpu.R[DosCpu.AX] = 6; Carry(true); break; }
            long offset = (cpu.R[DosCpu.CX] << 16) | cpu.R[DosCpu.DX];
            if (al == 1) offset += run.FilePos;
            else if (al == 2) offset += file.Length;
            run.FilePos = Math.Max(0, offset);
            cpu.R[DosCpu.AX] = (ushort)run.FilePos;
            cpu.R[DosCpu.DX] = (ushort)(run.FilePos >> 16);
            Carry(false);
            break;
          }
          case 0x44: cpu.R[DosCpu.DX] = 0x80; cpu.R[DosCpu.AX] = 0; Carry(false); break;
          case 0x00: case 0x4C: Stop("the program exited before its start was reached."); return;
          default: Stop($"DOS function {ah:X2}h called before the original program started."); return;
        }
        break;
      case 0x20: Stop("the program exited before its start was reached."); return;
      case 0x12: cpu.R[DosCpu.AX] = 640; break;
      case 0x11: cpu.R[DosCpu.AX] = 0x0021; break;
      case 0x1A: cpu.R[DosCpu.CX] = 0; cpu.R[DosCpu.DX] = 0; break;
      default: break; // other BIOS services and unhooked CPU traps return untouched
    }
    cpu.ServiceReturn();
  }
}
