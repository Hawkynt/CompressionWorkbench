#pragma warning disable CS1591
namespace FileFormat.ExePackers;

/// <summary>
/// A real-mode 8086/80186 interpreter, written from the Intel instruction set reference, for
/// running DOS decompression stubs in a sandbox. Memory is 1 MiB with the 8086 wrap-around;
/// I/O ports read as 0xFF; interrupts go through the vector table in memory, and vectors that
/// point into the reserved service segment are handed to <see cref="ServiceInterrupt"/>.
/// The hooks (<see cref="OnWrite"/>, <see cref="OnTransfer"/>) let the tracer watch what the
/// stub does without the stub being able to tell.
/// </summary>
internal sealed class DosCpu {
  public const int MemorySize = 0x100000;
  public const ushort ServiceSegment = 0xF000;

  public readonly byte[] Memory = new byte[MemorySize + 16];

  // General registers in encoding order: AX CX DX BX SP BP SI DI.
  public readonly ushort[] R = new ushort[8];
  // Segment registers in encoding order: ES CS SS DS.
  public readonly ushort[] S = new ushort[4];
  public ushort IP;
  public ushort Flags = 0xF002;

  public const int ES = 0, CS = 1, SS = 2, DS = 3;
  public const int AX = 0, CX = 1, DX = 2, BX = 3, SP = 4, BP = 5, SI = 6, DI = 7;

  public const ushort CF = 0x0001, PF = 0x0004, AF = 0x0010, ZF = 0x0040, SF = 0x0080,
    TF = 0x0100, IF = 0x0200, DF = 0x0400, OF = 0x0800;

  public long Instructions;
  public bool Halted;

  /// <summary>Called for every byte the program writes, with the linear address.</summary>
  public Action<int>? OnWrite;

  /// <summary>
  /// Called after a jump-class instruction (near/far JMP, RET, RETF, IRET, indirect JMP)
  /// with the linear address of the instruction and of its target.
  /// </summary>
  public Action<int, int>? OnTransfer;

  /// <summary>Handles an interrupt whose vector points into the service segment (number = IP).</summary>
  public Action<int>? ServiceInterrupt;

  private int _segOverride = -1;
  private int _rep; // 0 none, 1 REP/REPE (F3), 2 REPNE (F2)
  private int _instrStart;

  /// <summary>Linear address of the instruction being executed.</summary>
  public int CurrentInstruction => this._instrStart;

  public static int Linear(ushort seg, ushort off) => ((seg << 4) + off) & 0xFFFFF;

  // ── memory ───────────────────────────────────────────────────────────

  /// <summary>Called for every data byte the program reads (not for instruction fetches).</summary>
  public Action<int>? OnRead;

  public byte Read8(int linear) {
    linear &= 0xFFFFF;
    this.OnRead?.Invoke(linear);
    return this.Memory[linear];
  }
  public ushort Read16(int linear) => (ushort)(this.Read8(linear) | (this.Read8(linear + 1) << 8));

  public void Write8(int linear, byte value) {
    linear &= 0xFFFFF;
    this.Memory[linear] = value;
    this.OnWrite?.Invoke(linear);
  }

  public void Write16(int linear, ushort value) {
    this.Write8(linear, (byte)value);
    this.Write8(linear + 1, (byte)(value >> 8));
  }

  private byte Read8(ushort seg, ushort off) => this.Read8(Linear(seg, off));
  // Word accesses wrap within the segment.
  private ushort Read16(ushort seg, ushort off) => (ushort)(this.Read8(seg, off) | (this.Read8(seg, (ushort)(off + 1)) << 8));
  private void Write8(ushort seg, ushort off, byte v) => this.Write8(Linear(seg, off), v);
  private void Write16(ushort seg, ushort off, ushort v) {
    this.Write8(seg, off, (byte)v);
    this.Write8(seg, (ushort)(off + 1), (byte)(v >> 8));
  }

  private byte Fetch8() => this.Memory[Linear(this.S[CS], this.IP++)];
  private ushort Fetch16() {
    var lo = this.Fetch8();
    return (ushort)(lo | (this.Fetch8() << 8));
  }

  /// <summary>True while a PUSH (or an interrupt or call) writes the stack.</summary>
  public bool StackWrite;

  public void Push(ushort v) {
    this.R[SP] -= 2;
    this.StackWrite = true;
    this.Write16(this.S[SS], this.R[SP], v);
    this.StackWrite = false;
  }

  public ushort Pop() {
    var v = this.Read16(this.S[SS], this.R[SP]);
    this.R[SP] += 2;
    return v;
  }

  // ── registers ────────────────────────────────────────────────────────

  private byte GetR8(int r) => r < 4 ? (byte)this.R[r] : (byte)(this.R[r - 4] >> 8);

  private void SetR8(int r, byte v) {
    if (r < 4) this.R[r] = (ushort)((this.R[r] & 0xFF00) | v);
    else this.R[r - 4] = (ushort)((this.R[r - 4] & 0x00FF) | (v << 8));
  }

  private bool Get(ushort f) => (this.Flags & f) != 0;

  private void Set(ushort f, bool on) {
    if (on) this.Flags |= f;
    else this.Flags = (ushort)(this.Flags & ~f);
  }

  // ── ModR/M ───────────────────────────────────────────────────────────

  private int _mod, _reg, _rm;
  private ushort _eaSeg, _eaOff;

  private void DecodeModRm() {
    var b = this.Fetch8();
    this._mod = b >> 6;
    this._reg = (b >> 3) & 7;
    this._rm = b & 7;
    if (this._mod == 3)
      return;
    int off;
    var seg = DS;
    switch (this._rm) {
      case 0: off = this.R[BX] + this.R[SI]; break;
      case 1: off = this.R[BX] + this.R[DI]; break;
      case 2: off = this.R[BP] + this.R[SI]; seg = SS; break;
      case 3: off = this.R[BP] + this.R[DI]; seg = SS; break;
      case 4: off = this.R[SI]; break;
      case 5: off = this.R[DI]; break;
      case 6:
        if (this._mod == 0) off = this.Fetch16();
        else { off = this.R[BP]; seg = SS; }
        break;
      default: off = this.R[BX]; break;
    }
    if (this._mod == 1) off += (sbyte)this.Fetch8();
    else if (this._mod == 2) off += this.Fetch16();
    this._eaOff = (ushort)off;
    this._eaSeg = this.S[this._segOverride >= 0 ? this._segOverride : seg];
  }

  private byte GetE8() => this._mod == 3 ? this.GetR8(this._rm) : this.Read8(this._eaSeg, this._eaOff);
  private ushort GetE16() => this._mod == 3 ? this.R[this._rm] : this.Read16(this._eaSeg, this._eaOff);

  private void SetE8(byte v) {
    if (this._mod == 3) this.SetR8(this._rm, v);
    else this.Write8(this._eaSeg, this._eaOff, v);
  }

  private void SetE16(ushort v) {
    if (this._mod == 3) this.R[this._rm] = v;
    else this.Write16(this._eaSeg, this._eaOff, v);
  }

  private ushort DataSeg => this.S[this._segOverride >= 0 ? this._segOverride : DS];

  // ── flags ────────────────────────────────────────────────────────────

  private static readonly bool[] Parity = BuildParity();

  private static bool[] BuildParity() {
    var p = new bool[256];
    for (var i = 0; i < 256; ++i)
      p[i] = (System.Numerics.BitOperations.PopCount((uint)i) & 1) == 0;
    return p;
  }

  private void SetSzp(uint result, bool word) {
    var masked = word ? result & 0xFFFF : result & 0xFF;
    this.Set(ZF, masked == 0);
    this.Set(SF, (masked & (word ? 0x8000u : 0x80u)) != 0);
    this.Set(PF, Parity[masked & 0xFF]);
  }

  /// <summary>ALU operation number (ADD OR ADC SBB AND SUB XOR CMP) on two operands.</summary>
  private uint Alu(int op, uint a, uint b, bool word) {
    var mask = word ? 0xFFFFu : 0xFFu;
    var sign = word ? 0x8000u : 0x80u;
    uint r;
    switch (op) {
      case 0: case 2: {
        var carry = op == 2 && this.Get(CF) ? 1u : 0u;
        r = a + b + carry;
        this.Set(CF, r > mask);
        this.Set(AF, ((a ^ b ^ r) & 0x10) != 0);
        this.Set(OF, ((~(a ^ b) & (a ^ r)) & sign) != 0);
        break;
      }
      case 3: case 5: case 7: {
        var borrow = op == 3 && this.Get(CF) ? 1u : 0u;
        r = a - b - borrow;
        this.Set(CF, (ulong)b + borrow > a);
        this.Set(AF, ((a ^ b ^ r) & 0x10) != 0);
        this.Set(OF, (((a ^ b) & (a ^ r)) & sign) != 0);
        break;
      }
      case 1: r = a | b; this.Set(CF, false); this.Set(OF, false); this.Set(AF, false); break;
      case 4: r = a & b; this.Set(CF, false); this.Set(OF, false); this.Set(AF, false); break;
      default: r = a ^ b; this.Set(CF, false); this.Set(OF, false); this.Set(AF, false); break;
    }
    r &= mask;
    this.SetSzp(r, word);
    return r;
  }

  private uint Inc(uint a, bool word) {
    var cf = this.Get(CF);
    var r = this.Alu(0, a, 1, word);
    this.Set(CF, cf);
    return r;
  }

  private uint Dec(uint a, bool word) {
    var cf = this.Get(CF);
    var r = this.Alu(5, a, 1, word);
    this.Set(CF, cf);
    return r;
  }

  /// <summary>Shift/rotate group: ROL ROR RCL RCR SHL SHR SAL SAR.</summary>
  private uint Shift(int op, uint v, int count, bool word) {
    count &= 0x1F; // 80186 behaviour
    if (count == 0)
      return v;
    var original = v;
    var mask = word ? 0xFFFFu : 0xFFu;
    var top = word ? 0x8000u : 0x80u;
    for (var i = 0; i < count; ++i) {
      switch (op) {
        case 0: { var c = (v & top) != 0; v = ((v << 1) | (c ? 1u : 0u)) & mask; this.Set(CF, c); break; }
        case 1: { var c = (v & 1) != 0; v = (v >> 1) | (c ? top : 0); this.Set(CF, c); break; }
        case 2: { var c = (v & top) != 0; v = ((v << 1) | (this.Get(CF) ? 1u : 0u)) & mask; this.Set(CF, c); break; }
        case 3: { var c = (v & 1) != 0; v = (v >> 1) | (this.Get(CF) ? top : 0); this.Set(CF, c); break; }
        case 4: case 6: { this.Set(CF, (v & top) != 0); v = (v << 1) & mask; break; }
        case 5: { this.Set(CF, (v & 1) != 0); v >>= 1; break; }
        default: { this.Set(CF, (v & 1) != 0); v = (v >> 1) | (v & top); break; }
      }
    }
    switch (op) {
      case 0: case 2: this.Set(OF, ((v & top) != 0) ^ this.Get(CF)); break;
      case 1: case 3: this.Set(OF, ((v ^ (v << 1)) & top) != 0); break;
      case 4: case 6: this.Set(OF, ((v & top) != 0) ^ this.Get(CF)); this.SetSzp(v, word); break;
      // OF comes from the last single-bit step: the sign bit of the value it shifted.
      case 5: this.Set(OF, ((original >> (count - 1)) & top) != 0); this.SetSzp(v, word); break;
      default: this.Set(OF, false); this.SetSzp(v, word); break;
    }
    return v;
  }

  // ── interrupts ───────────────────────────────────────────────────────

  public void Interrupt(int n) {
    this.Push(this.Flags);
    this.Push(this.S[CS]);
    this.Push(this.IP);
    this.Set(IF, false);
    this.Set(TF, false);
    this.IP = this.Read16(n * 4);
    this.S[CS] = this.Read16(n * 4 + 2);
  }

  /// <summary>Returns from a service interrupt, keeping the flags the service set in CF/ZF.</summary>
  public void ServiceReturn(bool keepCarry = true) {
    var ip = this.Pop();
    var cs = this.Pop();
    var flags = this.Pop();
    if (keepCarry)
      flags = (ushort)((flags & ~(CF | ZF)) | (this.Flags & (CF | ZF)));
    this.IP = ip;
    this.S[CS] = cs;
    this.Flags = (ushort)(flags | 0xF002);
  }

  // ── execution ────────────────────────────────────────────────────────

  public void Step() {
    if (this.S[CS] == ServiceSegment && this.ServiceInterrupt != null) {
      this.ServiceInterrupt?.Invoke(this.IP);
      ++this.Instructions;
      return;
    }
    this._segOverride = -1;
    this._rep = 0;
    this._instrStart = Linear(this.S[CS], this.IP);
    ++this.Instructions;
    while (true) {
      var op = this.Fetch8();
      switch (op) {
        case 0x26: this._segOverride = ES; continue;
        case 0x2E: this._segOverride = CS; continue;
        case 0x36: this._segOverride = SS; continue;
        case 0x3E: this._segOverride = DS; continue;
        case 0xF0: continue;
        case 0xF2: this._rep = 2; continue;
        case 0xF3: this._rep = 1; continue;
        default: this.Execute(op); return;
      }
    }
  }

  private void Transfer(int target) => this.OnTransfer?.Invoke(this._instrStart, target);

  private void Execute(byte op) {
    if (op < 0x40 && (op & 7) < 6) {
      this.AluForm(op >> 3, op & 7);
      return;
    }
    switch (op) {
      case 0x06: this.Push(this.S[ES]); break;
      case 0x07: this.S[ES] = this.Pop(); break;
      case 0x0E: this.Push(this.S[CS]); break;
      case 0x16: this.Push(this.S[SS]); break;
      case 0x17: this.S[SS] = this.Pop(); break;
      case 0x1E: this.Push(this.S[DS]); break;
      case 0x1F: this.S[DS] = this.Pop(); break;
      case 0x27: this.Daa(); break;
      case 0x2F: this.Das(); break;
      case 0x37: this.Aaa(); break;
      case 0x3F: this.Aas(); break;
      case >= 0x40 and <= 0x47: this.R[op & 7] = (ushort)this.Inc(this.R[op & 7], true); break;
      case >= 0x48 and <= 0x4F: this.R[op & 7] = (ushort)this.Dec(this.R[op & 7], true); break;
      case >= 0x50 and <= 0x57:
        if ((op & 7) == SP) { var sp = this.R[SP]; this.Push((ushort)(sp - 2)); } // 186+ pushes the new SP? (8086 pushes decremented)
        else this.Push(this.R[op & 7]);
        break;
      case >= 0x58 and <= 0x5F: this.R[op & 7] = this.Pop(); break;
      case 0x60: {
        var sp = this.R[SP];
        for (var i = 0; i < 8; ++i) this.Push(i == SP ? sp : this.R[i]);
        break;
      }
      case 0x61: {
        for (var i = 7; i >= 0; --i) { var v = this.Pop(); if (i != SP) this.R[i] = v; }
        break;
      }
      case 0x62: this.DecodeModRm(); break; // BOUND: bounds never checked
      case 0x68: this.Push(this.Fetch16()); break;
      case 0x69: case 0x6B: {
        this.DecodeModRm();
        var a = (short)this.GetE16();
        int b = op == 0x69 ? (short)this.Fetch16() : (sbyte)this.Fetch8();
        var r = a * b;
        this.R[this._reg] = (ushort)r;
        var over = r != (short)r;
        this.Set(CF, over);
        this.Set(OF, over);
        break;
      }
      case 0x6A: this.Push((ushort)(sbyte)this.Fetch8()); break;
      case 0x6C: case 0x6D: case 0x6E: case 0x6F: this.StringIo(op); break;
      case >= 0x70 and <= 0x7F: {
        var d = (sbyte)this.Fetch8();
        if (this.Condition(op & 0xF)) this.IP = (ushort)(this.IP + d);
        break;
      }
      case 0x80: case 0x81: case 0x82: case 0x83: {
        this.DecodeModRm();
        var word = op is 0x81 or 0x83;
        if (word) {
          var a = this.GetE16();
          uint b = op == 0x81 ? this.Fetch16() : (ushort)(sbyte)this.Fetch8();
          var r = this.Alu(this._reg, a, b, true);
          if (this._reg != 7) this.SetE16((ushort)r);
        } else {
          var a = this.GetE8();
          var b = this.Fetch8();
          var r = this.Alu(this._reg, a, b, false);
          if (this._reg != 7) this.SetE8((byte)r);
        }
        break;
      }
      case 0x84: this.DecodeModRm(); this.Alu(4, this.GetE8(), this.GetR8(this._reg), false); break;
      case 0x85: this.DecodeModRm(); this.Alu(4, this.GetE16(), this.R[this._reg], true); break;
      case 0x86: { this.DecodeModRm(); var a = this.GetE8(); this.SetE8(this.GetR8(this._reg)); this.SetR8(this._reg, a); break; }
      case 0x87: { this.DecodeModRm(); var a = this.GetE16(); this.SetE16(this.R[this._reg]); this.R[this._reg] = a; break; }
      case 0x88: this.DecodeModRm(); this.SetE8(this.GetR8(this._reg)); break;
      case 0x89: this.DecodeModRm(); this.SetE16(this.R[this._reg]); break;
      case 0x8A: this.DecodeModRm(); this.SetR8(this._reg, this.GetE8()); break;
      case 0x8B: this.DecodeModRm(); this.R[this._reg] = this.GetE16(); break;
      case 0x8C: this.DecodeModRm(); this.SetE16(this.S[this._reg & 3]); break;
      case 0x8D: this.DecodeModRm(); this.R[this._reg] = this._eaOff; break;
      case 0x8E: this.DecodeModRm(); this.S[this._reg & 3] = this.GetE16(); break;
      case 0x8F: { this.DecodeModRm(); var v = this.Pop(); this.SetE16(v); break; }
      case 0x90: break;
      case >= 0x91 and <= 0x97: { var t = this.R[AX]; this.R[AX] = this.R[op & 7]; this.R[op & 7] = t; break; }
      case 0x98: this.R[AX] = (ushort)(sbyte)this.R[AX]; break;
      case 0x99: this.R[DX] = (this.R[AX] & 0x8000) != 0 ? (ushort)0xFFFF : (ushort)0; break;
      case 0x9A: {
        var ip = this.Fetch16();
        var cs = this.Fetch16();
        this.Push(this.S[CS]);
        this.Push(this.IP);
        this.S[CS] = cs;
        this.IP = ip;
        break;
      }
      case 0x9B: break;
      case 0x9C: this.Push(this.Flags); break;
      case 0x9D: this.Flags = (ushort)((this.Pop() & 0x0FD5) | 0xF002); break;
      case 0x9E: this.Flags = (ushort)((this.Flags & 0xFF00) | (this.R[AX] >> 8 & 0xD5) | 0x02); break;
      case 0x9F: this.R[AX] = (ushort)((this.R[AX] & 0xFF) | ((this.Flags & 0xFF) << 8)); break;
      case 0xA0: this.SetR8(0, this.Read8(this.DataSeg, this.Fetch16())); break;
      case 0xA1: this.R[AX] = this.Read16(this.DataSeg, this.Fetch16()); break;
      case 0xA2: this.Write8(this.DataSeg, this.Fetch16(), (byte)this.R[AX]); break;
      case 0xA3: this.Write16(this.DataSeg, this.Fetch16(), this.R[AX]); break;
      case >= 0xA4 and <= 0xA7: case >= 0xAA and <= 0xAF: this.StringOp(op); break;
      case 0xA8: this.Alu(4, (byte)this.R[AX], this.Fetch8(), false); break;
      case 0xA9: this.Alu(4, this.R[AX], this.Fetch16(), true); break;
      case >= 0xB0 and <= 0xB7: this.SetR8(op & 7, this.Fetch8()); break;
      case >= 0xB8 and <= 0xBF: this.R[op & 7] = this.Fetch16(); break;
      case 0xC0: case 0xC1: case 0xD0: case 0xD1: case 0xD2: case 0xD3: {
        this.DecodeModRm();
        var word = (op & 1) != 0;
        var count = op <= 0xC1 ? this.Fetch8() : op <= 0xD1 ? 1 : (byte)this.R[CX];
        if (word) this.SetE16((ushort)this.Shift(this._reg, this.GetE16(), count, true));
        else this.SetE8((byte)this.Shift(this._reg, this.GetE8(), count, false));
        break;
      }
      case 0xC2: { var n = this.Fetch16(); this.IP = this.Pop(); this.R[SP] += n; this.Transfer(Linear(this.S[CS], this.IP)); break; }
      case 0xC3: this.IP = this.Pop(); this.Transfer(Linear(this.S[CS], this.IP)); break;
      case 0xC4: case 0xC5: {
        this.DecodeModRm();
        this.R[this._reg] = this.Read16(this._eaSeg, this._eaOff);
        this.S[op == 0xC4 ? ES : DS] = this.Read16(this._eaSeg, (ushort)(this._eaOff + 2));
        break;
      }
      case 0xC6: this.DecodeModRm(); this.SetE8(this.Fetch8()); break;
      case 0xC7: this.DecodeModRm(); this.SetE16(this.Fetch16()); break;
      case 0xC8: {
        var size = this.Fetch16();
        var level = this.Fetch8() & 0x1F;
        this.Push(this.R[BP]);
        var frame = this.R[SP];
        for (var i = 1; i < level; ++i) {
          this.R[BP] -= 2;
          this.Push(this.Read16(this.S[SS], this.R[BP]));
        }
        if (level > 0) this.Push(frame);
        this.R[BP] = frame;
        this.R[SP] -= size;
        break;
      }
      case 0xC9: this.R[SP] = this.R[BP]; this.R[BP] = this.Pop(); break;
      case 0xCA: case 0xCB: {
        var n = op == 0xCA ? this.Fetch16() : (ushort)0;
        this.IP = this.Pop();
        this.S[CS] = this.Pop();
        this.R[SP] += n;
        this.Transfer(Linear(this.S[CS], this.IP));
        break;
      }
      case 0xCC: this.Interrupt(3); break;
      case 0xCD: this.Interrupt(this.Fetch8()); break;
      case 0xCE: if (this.Get(OF)) this.Interrupt(4); break;
      case 0xCF:
        this.IP = this.Pop();
        this.S[CS] = this.Pop();
        this.Flags = (ushort)((this.Pop() & 0x0FD5) | 0xF002);
        this.Transfer(Linear(this.S[CS], this.IP));
        break;
      case 0xD4: {
        var b = this.Fetch8();
        if (b == 0) {
          // The 8086 leaves the flags of a zero result behind before it traps.
          this.SetSzp(0, false); this.Set(AF, false); this.Set(CF, false); this.Set(OF, false);
          this.Interrupt(0);
          break;
        }
        var al = (byte)this.R[AX];
        this.R[AX] = (ushort)(((al / b) << 8) | (al % b));
        this.SetSzp((byte)this.R[AX], false);
        break;
      }
      case 0xD5: {
        var b = this.Fetch8();
        var al = (byte)((byte)this.R[AX] + (this.R[AX] >> 8) * b);
        this.R[AX] = al;
        this.SetSzp(al, false);
        break;
      }
      case 0xD6: this.SetR8(0, this.Get(CF) ? (byte)0xFF : (byte)0); break;
      case 0xD7: this.SetR8(0, this.Read8(this.DataSeg, (ushort)(this.R[BX] + (byte)this.R[AX]))); break;
      case >= 0xD8 and <= 0xDF: this.DecodeModRm(); break; // coprocessor escape: ignored
      case 0xE0: case 0xE1: case 0xE2: {
        var d = (sbyte)this.Fetch8();
        --this.R[CX];
        var go = this.R[CX] != 0 && (op == 0xE2 || (op == 0xE1 ? this.Get(ZF) : !this.Get(ZF)));
        if (go) this.IP = (ushort)(this.IP + d);
        break;
      }
      case 0xE3: { var d = (sbyte)this.Fetch8(); if (this.R[CX] == 0) this.IP = (ushort)(this.IP + d); break; }
      case 0xE4: this.Fetch8(); this.SetR8(0, 0xFF); break;
      case 0xE5: this.Fetch8(); this.R[AX] = 0xFFFF; break;
      case 0xE6: case 0xE7: this.Fetch8(); break;
      case 0xE8: { var d = this.Fetch16(); this.Push(this.IP); this.IP += d; break; }
      case 0xE9: { var d = this.Fetch16(); this.IP += d; this.Transfer(Linear(this.S[CS], this.IP)); break; }
      case 0xEA: {
        var ip = this.Fetch16();
        var cs = this.Fetch16();
        this.IP = ip;
        this.S[CS] = cs;
        this.Transfer(Linear(cs, ip));
        break;
      }
      case 0xEB: { var d = (sbyte)this.Fetch8(); this.IP = (ushort)(this.IP + d); break; }
      case 0xEC: this.SetR8(0, 0xFF); break;
      case 0xED: this.R[AX] = 0xFFFF; break;
      case 0xEE: case 0xEF: break;
      case 0xF4: this.Halted = true; break;
      case 0xF5: this.Set(CF, !this.Get(CF)); break;
      case 0xF6: case 0xF7: this.Group3(op == 0xF7); break;
      case 0xF8: this.Set(CF, false); break;
      case 0xF9: this.Set(CF, true); break;
      case 0xFA: this.Set(IF, false); break;
      case 0xFB: this.Set(IF, true); break;
      case 0xFC: this.Set(DF, false); break;
      case 0xFD: this.Set(DF, true); break;
      case 0xFE: {
        this.DecodeModRm();
        if (this._reg == 0) this.SetE8((byte)this.Inc(this.GetE8(), false));
        else if (this._reg == 1) this.SetE8((byte)this.Dec(this.GetE8(), false));
        else throw new InvalidOperationException($"invalid opcode FE /{this._reg}");
        break;
      }
      case 0xFF: this.Group5(); break;
      default: throw new InvalidOperationException($"invalid opcode {op:X2}");
    }
  }

  private void AluForm(int alu, int form) {
    switch (form) {
      case 0: { this.DecodeModRm(); var r = this.Alu(alu, this.GetE8(), this.GetR8(this._reg), false); if (alu != 7) this.SetE8((byte)r); break; }
      case 1: { this.DecodeModRm(); var r = this.Alu(alu, this.GetE16(), this.R[this._reg], true); if (alu != 7) this.SetE16((ushort)r); break; }
      case 2: { this.DecodeModRm(); var r = this.Alu(alu, this.GetR8(this._reg), this.GetE8(), false); if (alu != 7) this.SetR8(this._reg, (byte)r); break; }
      case 3: { this.DecodeModRm(); var r = this.Alu(alu, this.R[this._reg], this.GetE16(), true); if (alu != 7) this.R[this._reg] = (ushort)r; break; }
      case 4: { var r = this.Alu(alu, (byte)this.R[AX], this.Fetch8(), false); if (alu != 7) this.SetR8(0, (byte)r); break; }
      default: { var r = this.Alu(alu, this.R[AX], this.Fetch16(), true); if (alu != 7) this.R[AX] = (ushort)r; break; }
    }
  }

  private bool Condition(int cc) {
    var r = (cc >> 1) switch {
      0 => this.Get(OF),
      1 => this.Get(CF),
      2 => this.Get(ZF),
      3 => this.Get(CF) || this.Get(ZF),
      4 => this.Get(SF),
      5 => this.Get(PF),
      6 => this.Get(SF) != this.Get(OF),
      _ => this.Get(ZF) || this.Get(SF) != this.Get(OF),
    };
    return (cc & 1) != 0 ? !r : r;
  }

  private void Group3(bool word) {
    this.DecodeModRm();
    switch (this._reg) {
      case 0: case 1:
        if (word) this.Alu(4, this.GetE16(), this.Fetch16(), true);
        else this.Alu(4, this.GetE8(), this.Fetch8(), false);
        break;
      case 2:
        if (word) this.SetE16((ushort)~this.GetE16());
        else this.SetE8((byte)~this.GetE8());
        break;
      case 3:
        if (word) { var v = this.GetE16(); var r = this.Alu(5, 0, v, true); this.SetE16((ushort)r); this.Set(CF, v != 0); }
        else { var v = this.GetE8(); var r = this.Alu(5, 0, v, false); this.SetE8((byte)r); this.Set(CF, v != 0); }
        break;
      case 4:
        if (word) {
          var r = (uint)this.R[AX] * this.GetE16();
          this.R[AX] = (ushort)r;
          this.R[DX] = (ushort)(r >> 16);
          this.Set(CF, this.R[DX] != 0); this.Set(OF, this.R[DX] != 0);
        } else {
          var r = (ushort)((byte)this.R[AX] * this.GetE8());
          this.R[AX] = r;
          this.Set(CF, (r >> 8) != 0); this.Set(OF, (r >> 8) != 0);
        }
        break;
      case 5:
        if (word) {
          var r = (short)this.R[AX] * (short)this.GetE16();
          this.R[AX] = (ushort)r;
          this.R[DX] = (ushort)(r >> 16);
          var over = r != (short)r;
          this.Set(CF, over); this.Set(OF, over);
        } else {
          var r = (short)((sbyte)this.R[AX] * (sbyte)this.GetE8());
          this.R[AX] = (ushort)r;
          var over = r != (sbyte)r;
          this.Set(CF, over); this.Set(OF, over);
        }
        break;
      case 6:
        if (word) {
          var d = this.GetE16();
          var n = ((uint)this.R[DX] << 16) | this.R[AX];
          if (d == 0 || n / d > 0xFFFF) { this.Interrupt(0); return; }
          this.R[AX] = (ushort)(n / d);
          this.R[DX] = (ushort)(n % d);
        } else {
          var d = this.GetE8();
          var n = this.R[AX];
          if (d == 0 || n / d > 0xFF) { this.Interrupt(0); return; }
          this.R[AX] = (ushort)(((n % d) << 8) | (n / d));
        }
        break;
      default:
        if (word) {
          var d = (short)this.GetE16();
          var n = (int)(((uint)this.R[DX] << 16) | this.R[AX]);
          if (d == 0) { this.Interrupt(0); return; }
          var q = n / d;
          // The 8086 traps on the most negative quotient as well.
          if (q is > short.MaxValue or <= short.MinValue) { this.Interrupt(0); return; }
          this.R[AX] = (ushort)q;
          this.R[DX] = (ushort)(n % d);
        } else {
          var d = (sbyte)this.GetE8();
          var n = (short)this.R[AX];
          if (d == 0) { this.Interrupt(0); return; }
          var q = n / d;
          if (q is > sbyte.MaxValue or <= sbyte.MinValue) { this.Interrupt(0); return; }
          this.R[AX] = (ushort)(((byte)(n % d) << 8) | (byte)q);
        }
        break;
    }
  }

  private void Group5() {
    this.DecodeModRm();
    switch (this._reg) {
      case 0: this.SetE16((ushort)this.Inc(this.GetE16(), true)); break;
      case 1: this.SetE16((ushort)this.Dec(this.GetE16(), true)); break;
      case 2: { var t = this.GetE16(); this.Push(this.IP); this.IP = t; break; }
      case 3: {
        var ip = this.Read16(this._eaSeg, this._eaOff);
        var cs = this.Read16(this._eaSeg, (ushort)(this._eaOff + 2));
        this.Push(this.S[CS]);
        this.Push(this.IP);
        this.S[CS] = cs;
        this.IP = ip;
        break;
      }
      case 4: this.IP = this.GetE16(); this.Transfer(Linear(this.S[CS], this.IP)); break;
      case 5: {
        var ip = this.Read16(this._eaSeg, this._eaOff);
        var cs = this.Read16(this._eaSeg, (ushort)(this._eaOff + 2));
        this.S[CS] = cs;
        this.IP = ip;
        this.Transfer(Linear(cs, ip));
        break;
      }
      // PUSH SP stores the decremented SP on the 8086/80186.
      case 6: this.Push(this._mod == 3 && this._rm == SP ? (ushort)(this.R[SP] - 2) : this.GetE16()); break;
      default: throw new InvalidOperationException("invalid opcode FF /7");
    }
  }

  private void StringOp(byte op) {
    var word = (op & 1) != 0;
    var step = (short)(this.Get(DF) ? (word ? -2 : -1) : (word ? 2 : 1));
    var compare = op is 0xA6 or 0xA7 or 0xAE or 0xAF;
    while (true) {
      if (this._rep != 0 && this.R[CX] == 0)
        break;
      switch (op & 0xFE) {
        case 0xA4:
          if (word) this.Write16(this.S[ES], this.R[DI], this.Read16(this.DataSeg, this.R[SI]));
          else this.Write8(this.S[ES], this.R[DI], this.Read8(this.DataSeg, this.R[SI]));
          this.R[SI] = (ushort)(this.R[SI] + step);
          this.R[DI] = (ushort)(this.R[DI] + step);
          break;
        case 0xA6:
          if (word) this.Alu(7, this.Read16(this.DataSeg, this.R[SI]), this.Read16(this.S[ES], this.R[DI]), true);
          else this.Alu(7, this.Read8(this.DataSeg, this.R[SI]), this.Read8(this.S[ES], this.R[DI]), false);
          this.R[SI] = (ushort)(this.R[SI] + step);
          this.R[DI] = (ushort)(this.R[DI] + step);
          break;
        case 0xAA:
          if (word) this.Write16(this.S[ES], this.R[DI], this.R[AX]);
          else this.Write8(this.S[ES], this.R[DI], (byte)this.R[AX]);
          this.R[DI] = (ushort)(this.R[DI] + step);
          break;
        case 0xAC:
          if (word) this.R[AX] = this.Read16(this.DataSeg, this.R[SI]);
          else this.SetR8(0, this.Read8(this.DataSeg, this.R[SI]));
          this.R[SI] = (ushort)(this.R[SI] + step);
          break;
        default:
          if (word) this.Alu(7, this.R[AX], this.Read16(this.S[ES], this.R[DI]), true);
          else this.Alu(7, (byte)this.R[AX], this.Read8(this.S[ES], this.R[DI]), false);
          this.R[DI] = (ushort)(this.R[DI] + step);
          break;
      }
      if (this._rep == 0)
        break;
      --this.R[CX];
      ++this.Instructions;
      if (compare && ((this._rep == 1 && !this.Get(ZF)) || (this._rep == 2 && this.Get(ZF))))
        break;
    }
  }

  private void StringIo(byte op) {
    var word = (op & 1) != 0;
    var step = (short)(this.Get(DF) ? (word ? -2 : -1) : (word ? 2 : 1));
    while (this._rep == 0 || this.R[CX] != 0) {
      if (op < 0x6E) {
        if (word) this.Write16(this.S[ES], this.R[DI], 0xFFFF);
        else this.Write8(this.S[ES], this.R[DI], 0xFF);
        this.R[DI] = (ushort)(this.R[DI] + step);
      } else
        this.R[SI] = (ushort)(this.R[SI] + step);
      if (this._rep == 0) break;
      --this.R[CX];
    }
  }

  // ── BCD ──────────────────────────────────────────────────────────────

  // The 8086 compares the original AL with 0x9F instead of 0x99 when AF was set.
  private void Daa() {
    var old = (byte)this.R[AX];
    var cf = this.Get(CF);
    var af = this.Get(AF);
    var al = old;
    if ((al & 0x0F) > 9 || af) { al += 6; this.Set(AF, true); } else this.Set(AF, false);
    if (old > (af ? 0x9F : 0x99) || cf) { al += 0x60; this.Set(CF, true); } else this.Set(CF, false);
    this.SetR8(0, al);
    this.SetSzp(al, false);
  }

  private void Das() {
    var old = (byte)this.R[AX];
    var cf = this.Get(CF);
    var af = this.Get(AF);
    var al = old;
    if ((al & 0x0F) > 9 || af) { al -= 6; this.Set(AF, true); } else this.Set(AF, false);
    if (old > (af ? 0x9F : 0x99) || cf) { al -= 0x60; this.Set(CF, true); } else this.Set(CF, false);
    this.SetR8(0, al);
    this.SetSzp(al, false);
  }

  // The 8086 adjusts AL and AH separately (no carry from AL into AH).
  private void Aaa() {
    var al = (byte)this.R[AX];
    var ah = (byte)(this.R[AX] >> 8);
    if ((al & 0x0F) > 9 || this.Get(AF)) {
      al += 6; ++ah;
      this.Set(AF, true); this.Set(CF, true);
    } else { this.Set(AF, false); this.Set(CF, false); }
    this.R[AX] = (ushort)((ah << 8) | (al & 0x0F));
  }

  private void Aas() {
    var al = (byte)this.R[AX];
    var ah = (byte)(this.R[AX] >> 8);
    if ((al & 0x0F) > 9 || this.Get(AF)) {
      al -= 6; --ah;
      this.Set(AF, true); this.Set(CF, true);
    } else { this.Set(AF, false); this.Set(CF, false); }
    this.R[AX] = (ushort)((ah << 8) | (al & 0x0F));
  }
}
