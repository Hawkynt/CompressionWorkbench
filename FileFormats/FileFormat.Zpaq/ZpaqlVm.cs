using System.Runtime.CompilerServices;

namespace FileFormat.Zpaq;

/// <summary>
/// Component type identifiers for the ZPAQL context-mixing prediction model.
/// </summary>
public enum ComponentType : byte {
  /// <summary>Constant prediction (always predicts 50/50).</summary>
  Const = 0,

  /// <summary>Context model — 2^n counters indexed by hashed context.</summary>
  Cm = 1,

  /// <summary>Indirect context model — byte history maps to a counter.</summary>
  Icm = 2,

  /// <summary>Match model — predicts by finding a previous matching context.</summary>
  Match = 3,

  /// <summary>Average of two component predictions.</summary>
  Avg = 4,

  /// <summary>Two-input adaptive mixer.</summary>
  Mix2 = 5,

  /// <summary>N-input adaptive mixer.</summary>
  Mix = 6,

  /// <summary>Indirect secondary symbol estimation.</summary>
  Isse = 7,

  /// <summary>Direct secondary symbol estimation.</summary>
  Sse = 8,
}

/// <summary>
/// Represents a single prediction component in the ZPAQL context-mixing model.
/// </summary>
public sealed class Component {
  /// <summary>The component type.</summary>
  public ComponentType Type { get; set; }

  /// <summary>First parameter (meaning depends on type, e.g. log2 of table size).</summary>
  public int Param1 { get; set; }

  /// <summary>Second parameter (meaning depends on type).</summary>
  public int Param2 { get; set; }

  /// <summary>Third parameter (meaning depends on type).</summary>
  public int Param3 { get; set; }

  /// <summary>Component memory (CM counters, ICM byte histories, etc.).</summary>
  public int[] Memory { get; set; } = [];

  /// <summary>Current hashed context value.</summary>
  public int Context { get; set; }

  /// <summary>Current prediction (0..65535, probability of next bit being 1).</summary>
  public int Prediction { get; set; } = 32768; // 50%

  /// <summary>Auxiliary state (match length, mixer weights, etc.).</summary>
  public int Aux { get; set; }
}

/// <summary>
/// ZPAQL virtual machine for ZPAQ compression and decompression.
/// </summary>
/// <remarks>
/// <para>
/// The ZPAQL VM is a bytecode interpreter with four 32-bit registers (A, B, C, D),
/// a 1-bit flag register (F), a 256-element register file (R[0]..R[255]),
/// a 32-bit H[] array, and an 8-bit M[] array. It executes two programs:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>HCOMP</b> — runs after each decoded byte to update hash contexts in H[].
///   </description></item>
///   <item><description>
///     <b>PCOMP</b> — optional post-processor that transforms decoded output.
///   </description></item>
/// </list>
/// <para>
/// The VM also manages an array of context-mixing components (CM, ICM, MATCH, etc.)
/// whose predictions are combined by an arithmetic coder to encode/decode data.
/// </para>
/// </remarks>
public sealed class ZpaqlVm {
  // ── Registers ───────────────────────────────────────────────────────────────

  private uint _a;
  private uint _b;
  private uint _c;
  private uint _d;
  private bool _f;
  private readonly uint[] _r = new uint[256];
  private int _pc;

  // ── Memory arrays ──────────────────────────────────────────────────────────

  private uint[] _h;  // H array (32-bit), indexed by B & (length-1)
  private byte[] _m;  // M array (8-bit), indexed by B/C/D & (length-1)
  private int _hMask; // H.Length - 1 for wrapping
  private int _mMask; // M.Length - 1 for wrapping

  // ── Programs ───────────────────────────────────────────────────────────────

  private readonly byte[] _hcomp; // HCOMP bytecode
  private readonly byte[] _pcomp; // PCOMP bytecode (optional, may be empty)

  // ── Context-mixing components ──────────────────────────────────────────────

  private readonly Component[] _components;

  // ── Arithmetic coder state ─────────────────────────────────────────────────

  private uint _x1; // lower bound of range
  private uint _x2; // upper bound of range

  // ── Squash / stretch tables for probability mapping ────────────────────────

  private static readonly int[] SquashTable = BuildSquashTable();
  private static readonly int[] StretchTable = BuildStretchTable();

  // ── Construction ───────────────────────────────────────────────────────────

  /// <summary>
  /// Creates a ZPAQL VM with the specified program and component configuration.
  /// </summary>
  /// <param name="hcomp">HCOMP bytecode (context hashing program).</param>
  /// <param name="pcomp">PCOMP bytecode (post-processing program, may be empty).</param>
  /// <param name="hh">Log2 of H[] array size (0..31). H has 2^hh entries.</param>
  /// <param name="hm">Log2 of M[] array size (0..31). M has 2^hm entries.</param>
  /// <param name="components">Context-mixing component definitions. May be empty for store-only archives.</param>
  /// <exception cref="ArgumentNullException">Thrown if <paramref name="hcomp"/> is null.</exception>
  public ZpaqlVm(byte[] hcomp, byte[] pcomp, int hh, int hm, Component[]? components = null) {
    ArgumentNullException.ThrowIfNull(hcomp);
    _hcomp = hcomp;
    _pcomp = pcomp ?? [];
    _components = components ?? [];

    hh = Math.Clamp(hh, 0, 24);
    hm = Math.Clamp(hm, 0, 24);
    var hSize = 1 << hh;
    var mSize = 1 << hm;
    _h = new uint[hSize];
    _m = new byte[mSize];
    _hMask = hSize - 1;
    _mMask = mSize - 1;

    _x1 = 0;
    _x2 = 0xFFFFFFFF;

    InitComponents();
  }

  /// <summary>
  /// Parses a ZPAQL program header from a binary span and constructs the VM.
  /// </summary>
  /// <param name="program">
  /// Binary ZPAQL program header. Layout:
  /// [0]: hh (log2 H size), [1]: hm (log2 M size),
  /// [2]: number of components (n),
  /// followed by n component descriptors (type + params),
  /// then HCOMP bytecode (2-byte LE length + code),
  /// then optional PCOMP bytecode (2-byte LE length + code, 0 if absent).
  /// </param>
  /// <exception cref="InvalidDataException">Thrown if the program header is malformed.</exception>
  public ZpaqlVm(ReadOnlySpan<byte> program) {
    if (program.Length < 3)
      ThrowMalformed("Program header too short.");

    var hh = Math.Clamp((int)program[0], 0, 24);
    var hm = Math.Clamp((int)program[1], 0, 24);
    int nComp = program[2];
    var pos = 3;

    // Parse component descriptors.
    var comps = new List<Component>();
    for (var i = 0; i < nComp; i++) {
      if (pos >= program.Length)
        ThrowMalformed("Unexpected end of component descriptors.");

      var comp = new Component { Type = (ComponentType)program[pos++] };

      // Each component type has a fixed number of parameter bytes.
      var paramCount = GetComponentParamCount(comp.Type);
      if (pos + paramCount > program.Length)
        ThrowMalformed("Unexpected end of component parameters.");

      if (paramCount >= 1) comp.Param1 = program[pos++];
      if (paramCount >= 2) comp.Param2 = program[pos++];
      if (paramCount >= 3) comp.Param3 = program[pos++];

      comps.Add(comp);
    }

    _components = comps.ToArray();

    // Parse HCOMP bytecode.
    if (pos + 2 > program.Length)
      ThrowMalformed("Missing HCOMP length.");
    var hcompLen = program[pos] | (program[pos + 1] << 8);
    pos += 2;
    if (pos + hcompLen > program.Length)
      ThrowMalformed("HCOMP bytecode truncated.");
    _hcomp = program.Slice(pos, hcompLen).ToArray();
    pos += hcompLen;

    // Parse optional PCOMP bytecode.
    if (pos + 2 <= program.Length) {
      var pcompLen = program[pos] | (program[pos + 1] << 8);
      pos += 2;
      if (pcompLen > 0 && pos + pcompLen <= program.Length) {
        _pcomp = program.Slice(pos, pcompLen).ToArray();
      } else {
        _pcomp = [];
      }
    } else {
      _pcomp = [];
    }

    var hSize = 1 << hh;
    var mSize = 1 << hm;
    _h = new uint[hSize];
    _m = new byte[mSize];
    _hMask = hSize - 1;
    _mMask = mSize - 1;

    _x1 = 0;
    _x2 = 0xFFFFFFFF;

    InitComponents();
  }

  // ── Public API ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Gets the H[] context array. H values are set by the HCOMP program and
  /// used as context indices for the prediction components.
  /// </summary>
  public uint[] H => _h;

  /// <summary>
  /// Gets the M[] byte array used by the HCOMP/PCOMP programs.
  /// </summary>
  public byte[] M => _m;

  /// <summary>
  /// Gets the context-mixing components.
  /// </summary>
  public Component[] Components => _components;

  /// <summary>
  /// Gets or sets register A (32-bit accumulator).
  /// </summary>
  public uint A { get => _a; set => _a = value; }

  /// <summary>
  /// Gets or sets register B (32-bit, also indexes M[]).
  /// </summary>
  public uint B { get => _b; set => _b = value; }

  /// <summary>
  /// Gets or sets register C (32-bit, also indexes M[]).
  /// </summary>
  public uint C { get => _c; set => _c = value; }

  /// <summary>
  /// Gets or sets register D (32-bit, also indexes M[]).
  /// </summary>
  public uint D { get => _d; set => _d = value; }

  /// <summary>
  /// Gets or sets the flag register (1-bit).
  /// </summary>
  public bool F { get => _f; set => _f = value; }

  /// <summary>
  /// Executes the HCOMP program with the given input byte.
  /// This should be called after each decoded byte to update the
  /// context hashes in H[].
  /// </summary>
  /// <param name="input">The decoded byte value (0..255), or -1 for EOF.</param>
  public void RunHcomp(int input) {
    Execute(_hcomp, input);
  }

  /// <summary>
  /// Executes the PCOMP (post-processing) program with the given input byte.
  /// </summary>
  /// <param name="input">The decoded byte value (0..255), or -1 for EOF.</param>
  public void RunPcomp(int input) {
    if (_pcomp.Length > 0)
      Execute(_pcomp, input);
  }

  /// <summary>
  /// Computes a combined prediction from all components.
  /// Returns a probability in the range 0..65535, where 65535 means
  /// the next bit is certainly 1, and 0 means certainly 0.
  /// </summary>
  /// <returns>Combined prediction (0..65535).</returns>
  public int Predict() {
    if (_components.Length == 0)
      return 32768; // 50% if no components

    // Simple averaging of all component predictions.
    // A full implementation would use the mixer components (MIX, MIX2)
    // from the COMP section for weighted mixing.
    long sum = 0;
    var count = 0;

    for (var i = 0; i < _components.Length; i++) {
      var comp = _components[i];
      UpdateComponentPrediction(comp, i);
      sum += comp.Prediction;
      count++;
    }

    return count > 0 ? (int)Math.Clamp(sum / count, 0, 65535) : 32768;
  }

  /// <summary>
  /// Updates all components after a decoded bit.
  /// </summary>
  /// <param name="bit">The decoded bit (0 or 1).</param>
  public void Update(int bit) {
    for (var i = 0; i < _components.Length; i++)
      UpdateComponent(_components[i], bit);
  }

  /// <summary>
  /// Resets the VM state (registers, arrays, arithmetic coder) without
  /// changing the program or component configuration.
  /// </summary>
  public void Reset() {
    _a = _b = _c = _d = 0;
    _f = false;
    Array.Clear(_r);
    Array.Clear(_h);
    Array.Clear(_m);
    _x1 = 0;
    _x2 = 0xFFFFFFFF;

    InitComponents();
  }

  // ── Arithmetic coder ───────────────────────────────────────────────────────

  /// <summary>
  /// Gets the current arithmetic coder lower bound.
  /// </summary>
  public uint ArithLow => _x1;

  /// <summary>
  /// Gets the current arithmetic coder upper bound.
  /// </summary>
  public uint ArithHigh => _x2;

  /// <summary>
  /// Encodes a single bit using the arithmetic coder with the given prediction.
  /// </summary>
  /// <param name="bit">The bit to encode (0 or 1).</param>
  /// <param name="prediction">Probability of 1, in range 1..65534.</param>
  /// <param name="output">Action called with each output byte.</param>
  public void ArithEncode(int bit, int prediction, Action<byte> output) {
    prediction = Math.Clamp(prediction, 1, 65534);

    var range = _x2 - _x1;
    var split = _x1 + (uint)(((ulong)range * (uint)(prediction >> 1)) / 32768);

    if (bit != 0)
      _x1 = split + 1;
    else
      _x2 = split;

    // Normalize: emit matching high bytes.
    while ((_x1 ^ _x2) < 0x01000000U) {
      output((byte)(_x1 >> 24));
      _x1 <<= 8;
      _x2 = (_x2 << 8) | 0xFF;
    }
  }

  /// <summary>
  /// Decodes a single bit using the arithmetic coder with the given prediction.
  /// </summary>
  /// <param name="prediction">Probability of 1, in range 1..65534.</param>
  /// <param name="code">Current 32-bit code value from the compressed stream.</param>
  /// <param name="readByte">Function to read the next byte from the compressed stream (-1 on EOF).</param>
  /// <returns>The decoded bit (0 or 1) and the updated code value.</returns>
  public (int Bit, uint Code) ArithDecode(int prediction, uint code, Func<int> readByte) {
    prediction = Math.Clamp(prediction, 1, 65534);

    var range = _x2 - _x1;
    var split = _x1 + (uint)(((ulong)range * (uint)(prediction >> 1)) / 32768);

    int bit;
    if (code <= split) {
      bit = 0;
      _x2 = split;
    } else {
      bit = 1;
      _x1 = split + 1;
    }

    // Normalize: shift out matching high bytes.
    while ((_x1 ^ _x2) < 0x01000000U) {
      _x1 <<= 8;
      _x2 = (_x2 << 8) | 0xFF;
      var b = readByte();
      code = (code << 8) | (uint)(b < 0 ? 0 : b);
    }

    return (bit, code);
  }

  // ── Bytecode execution ─────────────────────────────────────────────────────

  private void Execute(byte[] code, int inputByte) {
    if (code.Length == 0)
      return;

    // Place input in A register as per ZPAQL spec.
    _a = (uint)(inputByte & 0xFF);
    _pc = 0;
    var limit = 1 << 24; // execution limit to prevent infinite loops

    while (_pc < code.Length && --limit > 0) {
      var op = code[_pc++];

      switch (op) {
        // 0: ERROR / HALT
        case 0:
          return;

        // 2..3: A++, A--
        case 2: _a++; break;
        case 3: _a--; break;

        // 4: A = !A (logical NOT)
        case 4: _a = _a == 0 ? 1U : 0U; break;

        // 5: A = 0
        case 5: _a = 0; break;

        // 6..8: B=A, C=A, D=A
        case 6: _b = _a; break;
        case 7: _c = _a; break;
        case 8: _d = _a; break;

        // 9..11: *B=A, *C=A, *D=A (write to M[])
        case 9: _m[(int)(_b & (uint)_mMask)] = (byte)_a; break;
        case 10: _m[(int)(_c & (uint)_mMask)] = (byte)_a; break;
        case 11: _m[(int)(_d & (uint)_mMask)] = (byte)_a; break;

        // 12..14: A=B, A=C, A=D
        case 12: _a = _b; break;
        case 13: _a = _c; break;
        case 14: _a = _d; break;

        // 15..17: A=*B, A=*C, A=*D (read from M[])
        case 15: _a = _m[(int)(_b & (uint)_mMask)]; break;
        case 16: _a = _m[(int)(_c & (uint)_mMask)]; break;
        case 17: _a = _m[(int)(_d & (uint)_mMask)]; break;

        // 18: A = B + A, F = (result == 0)
        case 18: _a = _b + _a; _f = _a == 0; break;

        // 19: A = B - A, F = borrow (B < A before subtraction)
        case 19: _f = _b < _a; _a = _b - _a; break;

        // 20: A = B * A, F = (result == 0)
        case 20: _a = _b * _a; _f = _a == 0; break;

        // 21: A = B / A, F = (remainder == 0). Div by 0 => A = 0.
        case 21:
          if (_a == 0) { _a = 0; _f = true; }
          else { var rem = _b % _a; _a = _b / _a; _f = rem == 0; }
          break;

        // 22: A = B % A. Div by 0 => A = 0.
        case 22:
          _a = _a == 0 ? 0 : _b % _a;
          break;

        // 23..25: A = B & A, B | A, B ^ A
        case 23: _a = _b & _a; break;
        case 24: _a = _b | _a; break;
        case 25: _a = _b ^ _a; break;

        // 26: A = B << (A mod 32)
        case 26: _a = _b << (int)(_a & 31); break;

        // 27: A = B >> (A mod 32) (logical)
        case 27: _a = _b >> (int)(_a & 31); break;

        // 28: A <<= 8
        case 28: _a <<= 8; break;

        // 29: A >>= 8 (logical)
        case 29: _a >>= 8; break;

        // 30: F = (A == B)
        case 30: _f = _a == _b; break;

        // 31: F = (A < B) unsigned
        case 31: _f = _a < _b; break;

        // 32: F = (A > B) unsigned
        case 32: _f = _a > _b; break;

        // 33..38: reserved / unused
        case 33: case 34: case 35: case 36: case 37: case 38:
          break;

        // 39: HALT
        case 39: return;

        // 40: F? (no-op — just tests F, used as prefix)
        case 40: break;

        // 41: JT — jump if F is true (1-byte signed offset follows)
        case 41: {
          var offset = ReadSignedByte(code, ref _pc);
          if (_f) _pc += offset;
          break;
        }

        // 42: JF — jump if F is false (1-byte signed offset follows)
        case 42: {
          var offset = ReadSignedByte(code, ref _pc);
          if (!_f) _pc += offset;
          break;
        }

        // 43: JMP — unconditional jump (1-byte signed offset)
        case 43: {
          var offset = ReadSignedByte(code, ref _pc);
          _pc += offset;
          break;
        }

        // 44..46: reserved jump variants
        case 44: case 45: case 46:
          if (_pc < code.Length) _pc++; // skip 1-byte operand
          break;

        // 47: LJ — long jump (2-byte unsigned absolute target)
        case 47: {
          var target = ReadUInt16(code, ref _pc);
          _pc = target;
          break;
        }

        // 48: R[imm] = A (immediate register index follows)
        case 48: {
          var idx = (int)ReadByte(code, ref _pc);
          _r[idx] = _a;
          break;
        }

        // 49: A = R[imm]
        case 49: {
          var idx = (int)ReadByte(code, ref _pc);
          _a = _r[idx];
          break;
        }

        // 50..55: reserved register ops (with 1-byte operand)
        case 50: case 51: case 52: case 53: case 54: case 55:
          if (_pc < code.Length) _pc++; // skip operand
          break;

        // 56: A += imm
        case 56: _a += ReadByte(code, ref _pc); break;

        // 57: A -= imm
        case 57: _a -= ReadByte(code, ref _pc); break;

        // 58: A *= imm
        case 58: _a *= ReadByte(code, ref _pc); break;

        // 59: A /= imm (0 => A=0)
        case 59: {
          var imm = ReadByte(code, ref _pc);
          _a = imm == 0 ? 0 : _a / imm;
          break;
        }

        // 60: A %= imm (0 => A=0)
        case 60: {
          var imm = ReadByte(code, ref _pc);
          _a = imm == 0 ? 0 : _a % imm;
          break;
        }

        // 61: A &= imm
        case 61: _a &= ReadByte(code, ref _pc); break;

        // 62: A |= imm
        case 62: _a |= ReadByte(code, ref _pc); break;

        // 63: A ^= imm
        case 63: _a ^= ReadByte(code, ref _pc); break;

        // 64..127: 2-byte instructions
        // Most are A op= imm16 or extended register/memory ops.
        case >= 64 and <= 127: {
          Execute2Byte(op, code);
          break;
        }

        // 128..223: 3-byte instructions (conditional jumps with 2-byte offset, etc.)
        case >= 128 and <= 223: {
          Execute3Byte(op, code);
          break;
        }

        // 224: OUT — emit A as output byte (used in PCOMP)
        case 224:
          OutputByte?.Invoke((byte)_a);
          break;

        // 225: HASH — A = (A + *B + 512) * 773
        case 225: {
          uint mb = _m[(int)(_b & (uint)_mMask)];
          _a = (_a + mb + 512) * 773;
          break;
        }

        // 226: HASHD — H[D] = (H[D] + A + 512) * 773
        case 226: {
          var idx = (int)(_d & (uint)_hMask);
          _h[idx] = (_h[idx] + _a + 512) * 773;
          break;
        }

        // 227..254: reserved / unused
        case >= 227 and <= 254:
          break;

        // 255: HALT
        case 255: return;
      }
    }
  }

  /// <summary>
  /// Event invoked when the PCOMP program executes an OUT instruction.
  /// </summary>
  public Action<byte>? OutputByte { get; set; }

  // ── 2-byte instruction handler ─────────────────────────────────────────────

  private void Execute2Byte(byte op, byte[] code) {
    // Opcodes 64..127: the second byte is an immediate operand.
    // We handle a useful subset; unrecognized opcodes skip 1 byte.

    var imm = ReadByte(code, ref _pc);

    switch (op) {
      // 64: A += imm*256 (upper byte add)
      case 64: _a += imm << 8; break;

      // 65: A -= imm*256
      case 65: _a -= imm << 8; break;

      // 66: A *= imm*256
      case 66: _a *= imm << 8; break;

      // 67: A /= imm*256 (0 => A=0)
      case 67: { var v = imm << 8; _a = v == 0 ? 0 : _a / v; break; }

      // 68: A %= imm*256 (0 => A=0)
      case 68: { var v = imm << 8; _a = v == 0 ? 0 : _a % v; break; }

      // 69: A &= imm*256
      case 69: _a &= imm << 8; break;

      // 70: A |= imm*256
      case 70: _a |= imm << 8; break;

      // 71: A ^= imm*256
      case 71: _a ^= imm << 8; break;

      // 72: A = imm (set A to 1-byte immediate)
      case 72: _a = imm; break;

      // 73: B = imm
      case 73: _b = imm; break;

      // 74: C = imm
      case 74: _c = imm; break;

      // 75: D = imm
      case 75: _d = imm; break;

      // 76: A += imm*65536
      case 76: _a += imm << 16; break;

      // 77: A -= imm*65536
      case 77: _a -= imm << 16; break;

      // 78: A += imm*16777216
      case 78: _a += imm << 24; break;

      // 79: A -= imm*16777216
      case 79: _a -= imm << 24; break;

      // 80: H[B] = A (indirect H store, imm ignored)
      case 80: _h[(int)(_b & (uint)_hMask)] = _a; break;

      // 81: A = H[B] (indirect H load, imm ignored)
      case 81: _a = _h[(int)(_b & (uint)_hMask)]; break;

      // 82: H[imm] = A
      case 82: _h[(int)(imm & (uint)_hMask)] = _a; break;

      // 83: A = H[imm]
      case 83: _a = _h[(int)(imm & (uint)_hMask)]; break;

      // Everything else: operand already consumed, no-op.
      default: break;
    }
  }

  // ── 3-byte instruction handler ─────────────────────────────────────────────

  private void Execute3Byte(byte op, byte[] code) {
    // Opcodes 128..223: 2-byte operand follows (little-endian).
    var imm16 = (int)ReadUInt16(code, ref _pc);

    switch (op) {
      // 128: A = imm16
      case 128: _a = (uint)imm16; break;

      // 129: B = imm16
      case 129: _b = (uint)imm16; break;

      // 130: C = imm16
      case 130: _c = (uint)imm16; break;

      // 131: D = imm16
      case 131: _d = (uint)imm16; break;

      // 132: JT with 2-byte offset
      case 132: if (_f) _pc = imm16; break;

      // 133: JF with 2-byte offset
      case 133: if (!_f) _pc = imm16; break;

      // 134: JMP with 2-byte absolute target
      case 134: _pc = imm16; break;

      // 135: A += imm16
      case 135: _a += (uint)imm16; break;

      // 136: A -= imm16
      case 136: _a -= (uint)imm16; break;

      // 137: A *= imm16
      case 137: _a *= (uint)imm16; break;

      // 138: A /= imm16
      case 138: _a = imm16 == 0 ? 0 : _a / (uint)imm16; break;

      // 139: A %= imm16
      case 139: _a = imm16 == 0 ? 0 : _a % (uint)imm16; break;

      // 140: A &= imm16
      case 140: _a &= (uint)imm16; break;

      // 141: A |= imm16
      case 141: _a |= (uint)imm16; break;

      // 142: A ^= imm16
      case 142: _a ^= (uint)imm16; break;

      // Everything else: operand already consumed, no-op.
      default: break;
    }
  }

  // ── Operand reading helpers ────────────────────────────────────────────────

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint ReadByte(byte[] code, ref int pc) =>
    pc < code.Length ? code[pc++] : 0U;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int ReadSignedByte(byte[] code, ref int pc) =>
    pc < code.Length ? (sbyte)code[pc++] : 0;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int ReadUInt16(byte[] code, ref int pc) {
    if (pc + 1 >= code.Length) {
      pc = code.Length;
      return 0;
    }
    var val = code[pc] | (code[pc + 1] << 8);
    pc += 2;
    return val;
  }

  // ── Component initialization ───────────────────────────────────────────────

  private void InitComponents() {
    for (var i = 0; i < _components.Length; i++) {
      var comp = _components[i];
      comp.Prediction = 32768; // 50%
      comp.Context = 0;
      comp.Aux = 0;

      switch (comp.Type) {
        case ComponentType.Cm: {
          // 2^Param1 counters, each starts at 32768 (50%).
          var size = 1 << Math.Clamp(comp.Param1, 0, 24);
          comp.Memory = new int[size];
          for (var j = 0; j < size; j++)
            comp.Memory[j] = 32768;
          break;
        }

        case ComponentType.Icm: {
          // Indirect context model: 256 byte-history entries + 2^(Param1+10) counters.
          var histSize = 256;
          var ctrSize = 1 << Math.Clamp(comp.Param1 + 10, 0, 24);
          comp.Memory = new int[histSize + ctrSize];
          // Initialize counters to 50%.
          for (var j = histSize; j < comp.Memory.Length; j++)
            comp.Memory[j] = 32768;
          break;
        }

        case ComponentType.Match: {
          // Match model: buffer of 2^Param1 bytes + 2^Param2 hash entries.
          var bufSize = 1 << Math.Clamp(comp.Param1, 0, 24);
          var hashSize = 1 << Math.Clamp(comp.Param2, 0, 24);
          comp.Memory = new int[bufSize + hashSize];
          break;
        }

        case ComponentType.Sse: {
          // SSE: 2^Param1 * 64 entries.
          var size = (1 << Math.Clamp(comp.Param1, 0, 20)) * 64;
          comp.Memory = new int[size];
          // Initialize to identity mapping.
          for (var j = 0; j < size; j++)
            comp.Memory[j] = Squash((j % 64 - 32) * 64);
          break;
        }

        default:
          comp.Memory ??= [];
          break;
      }
    }
  }

  // ── Component prediction & update ──────────────────────────────────────────

  private void UpdateComponentPrediction(Component comp, int index) {
    // Set context from H[index].
    comp.Context = (int)(_h[index & _hMask] & 0x7FFFFFFF);

    switch (comp.Type) {
      case ComponentType.Const:
        comp.Prediction = 32768;
        break;

      case ComponentType.Cm:
        if (comp.Memory.Length > 0) {
          var ctx = comp.Context % comp.Memory.Length;
          comp.Prediction = Math.Clamp(comp.Memory[ctx], 0, 65535);
        }
        break;

      case ComponentType.Icm:
        if (comp.Memory.Length > 256) {
          var histIdx = comp.Context & 255;
          var ctrIdx = 256 + (comp.Memory[histIdx] & (comp.Memory.Length - 257));
          if (ctrIdx < comp.Memory.Length)
            comp.Prediction = Math.Clamp(comp.Memory[ctrIdx], 0, 65535);
        }
        break;

      case ComponentType.Match:
        // Match model: if currently matching, predict from match bit.
        if (comp.Aux > 0)
          comp.Prediction = comp.Aux > 128 ? 65535 - 256 : 256;
        else
          comp.Prediction = 32768;
        break;

      case ComponentType.Avg:
        if (_components.Length > Math.Max(comp.Param1, comp.Param2)) {
          var p1 = _components[comp.Param1].Prediction;
          var p2 = _components[comp.Param2].Prediction;
          comp.Prediction = (p1 + p2 + 1) >> 1;
        }
        break;

      case ComponentType.Sse:
        if (comp.Memory.Length > 0) {
          var ctx = (comp.Context * 64) % comp.Memory.Length;
          comp.Prediction = Math.Clamp(comp.Memory[ctx], 0, 65535);
        }
        break;

      default:
        comp.Prediction = 32768;
        break;
    }
  }

  private static void UpdateComponent(Component comp, int bit) {
    switch (comp.Type) {
      case ComponentType.Cm:
        if (comp.Memory.Length > 0) {
          var ctx = comp.Context % comp.Memory.Length;
          var p = comp.Memory[ctx];
          // Adapt towards the observed bit.
          var target = bit != 0 ? 65535 : 0;
          var rate = Math.Max(comp.Param2, 2); // learning rate
          comp.Memory[ctx] = p + ((target - p) >> rate);
        }
        break;

      case ComponentType.Icm:
        if (comp.Memory.Length > 256) {
          var histIdx = comp.Context & 255;
          var ctrIdx = 256 + (comp.Memory[histIdx] & (comp.Memory.Length - 257));
          if (ctrIdx < comp.Memory.Length) {
            var p = comp.Memory[ctrIdx];
            var target = bit != 0 ? 65535 : 0;
            comp.Memory[ctrIdx] = p + ((target - p) >> 5);
          }
          // Update byte history: shift in bit.
          comp.Memory[histIdx] = (comp.Memory[histIdx] << 1) | bit;
        }
        break;

      case ComponentType.Match:
        // Update match length tracking.
        if (comp.Aux > 0) {
          // If prediction was wrong, break match.
          var predicted1 = comp.Aux > 128;
          if ((bit != 0) != predicted1)
            comp.Aux = 0;
        }
        break;

      // CONST, AVG, SSE, MIX, MIX2, ISSE: simplified — no update needed for basic operation.
      default:
        break;
    }
  }

  // ── Probability tables ─────────────────────────────────────────────────────

  /// <summary>
  /// Squash function: maps a logistic value (-2048..2047) to a probability (0..65535).
  /// squash(x) = 65536 / (1 + exp(-x / 64)).
  /// </summary>
  /// <param name="x">Logistic value.</param>
  /// <returns>Probability in range 1..65534.</returns>
  public static int Squash(int x) {
    x = Math.Clamp(x, -2047, 2047);
    return SquashTable[x + 2047];
  }

  /// <summary>
  /// Stretch function: inverse of squash, maps probability (1..65534) to logistic.
  /// </summary>
  /// <param name="p">Probability (1..65534).</param>
  /// <returns>Logistic value approximately in range -2047..2047.</returns>
  public static int Stretch(int p) {
    p = Math.Clamp(p, 1, 65534);
    return StretchTable[p >> 6]; // 1024-entry table
  }

  private static int[] BuildSquashTable() {
    // squash(x) = 65536 / (1 + exp(-x/64))
    var table = new int[4095]; // index 0..4094 maps x = -2047..2047
    for (var i = 0; i < table.Length; i++) {
      var x = i - 2047;
      var p = 65536.0 / (1.0 + Math.Exp(-x / 64.0));
      table[i] = Math.Clamp((int)(p + 0.5), 1, 65534);
    }
    return table;
  }

  private static int[] BuildStretchTable() {
    // Inverse of squash, sampled at 1024 points.
    var table = new int[1024];
    for (var i = 0; i < 1024; i++) {
      var p = (i + 0.5) * 64.0; // map to 0..65535 range
      if (p <= 0) p = 1;
      if (p >= 65535) p = 65534;
      var x = -64.0 * Math.Log(65536.0 / p - 1.0);
      table[i] = Math.Clamp((int)Math.Round(x), -2047, 2047);
    }
    return table;
  }

  // ── Component parameter counts ─────────────────────────────────────────────

  private static int GetComponentParamCount(ComponentType type) =>
    type switch {
      ComponentType.Const => 0,
      ComponentType.Cm    => 2, // size, rate
      ComponentType.Icm   => 1, // size
      ComponentType.Match => 2, // bufSize, hashSize
      ComponentType.Avg   => 2, // comp1, comp2
      ComponentType.Mix2  => 3, // context, comp1, comp2
      ComponentType.Mix   => 3, // context, nInputs, rate
      ComponentType.Isse  => 2, // context, size
      ComponentType.Sse   => 2, // context, size
      _                   => 0,
    };

  // ── Throw helpers ──────────────────────────────────────────────────────────

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowMalformed(string message) =>
    throw new InvalidDataException(message);
}
