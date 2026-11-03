namespace FileFormat.Rar;

/// <summary>
/// GF(2^16) arithmetic over the polynomial x^16 + x^12 + x^3 + x + 1 (0x1100B), the field RAR 5
/// recovery records and recovery volumes compute in (measured, docs/RAR-RECOVERY-ON-DISK.md).
/// </summary>
internal static class RarGf16 {
  private const int Poly = 0x1100B;
  private static readonly ushort[] Exp = new ushort[2 * 65535];
  private static readonly int[] Log = new int[65536];

  static RarGf16() {
    var x = 1;
    for (var i = 0; i < 65535; ++i) {
      Exp[i] = (ushort)x;
      Exp[i + 65535] = (ushort)x;
      Log[x] = i;
      x <<= 1;
      if ((x & 0x10000) != 0) x ^= Poly;
    }
  }

  public static ushort Mul(ushort a, ushort b) => a == 0 || b == 0 ? (ushort)0 : Exp[Log[a] + Log[b]];

  public static ushort Inv(ushort a) => a == 0 ? throw new DivideByZeroException("GF(2^16) has no inverse of 0.") : Exp[65535 - Log[a]];

  /// <summary>The logarithm of a non-zero element, for repeated multiplication by one factor.</summary>
  public static int LogOf(ushort a) => Log[a];

  /// <summary>
  /// <paramref name="acc"/>[i] ^= factor * <paramref name="data"/>[i] for little-endian 16-bit words,
  /// with the factor given as its logarithm.
  /// </summary>
  public static void MulAdd(Span<byte> acc, ReadOnlySpan<byte> data, int factorLog) {
    var exp = Exp;
    var log = Log;
    for (var i = 0; i + 1 < data.Length; i += 2) {
      var d = data[i] | data[i + 1] << 8;
      if (d == 0) continue;
      var p = exp[log[d] + factorLog];
      acc[i] ^= (byte)p;
      acc[i + 1] ^= (byte)(p >> 8);
    }
  }

  /// <summary>Cauchy coefficient RAR uses: 1 / (row xor column).</summary>
  public static ushort Cauchy(int row, int column) => Inv((ushort)(row ^ column));

  /// <summary>Inverts a square matrix in place (Gauss-Jordan); false when singular.</summary>
  public static bool Invert(ushort[,] m) {
    var n = m.GetLength(0);
    var inv = new ushort[n, n];
    for (var i = 0; i < n; ++i) inv[i, i] = 1;
    for (var col = 0; col < n; ++col) {
      var pivot = -1;
      for (var r = col; r < n; ++r)
        if (m[r, col] != 0) { pivot = r; break; }
      if (pivot < 0) return false;
      if (pivot != col)
        for (var k = 0; k < n; ++k) {
          (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
          (inv[col, k], inv[pivot, k]) = (inv[pivot, k], inv[col, k]);
        }
      var f = Inv(m[col, col]);
      for (var k = 0; k < n; ++k) {
        m[col, k] = Mul(m[col, k], f);
        inv[col, k] = Mul(inv[col, k], f);
      }
      for (var r = 0; r < n; ++r) {
        if (r == col || m[r, col] == 0) continue;
        var g = m[r, col];
        for (var k = 0; k < n; ++k) {
          m[r, k] ^= Mul(g, m[col, k]);
          inv[r, k] ^= Mul(g, inv[col, k]);
        }
      }
    }
    Array.Copy(inv, m, inv.Length);
    return true;
  }
}

/// <summary>
/// GF(2^8) over 0x11D with the classic systematic Reed-Solomon code (generator roots a^1..a^n):
/// the code RAR 3 recovery volumes use (measured, docs/RAR-RECOVERY-ON-DISK.md).
/// </summary>
internal static class RarGf8 {
  private static readonly byte[] Exp = new byte[512];
  private static readonly int[] Log = new int[256];

  static RarGf8() {
    var x = 1;
    for (var i = 0; i < 255; ++i) {
      Exp[i] = (byte)x;
      Exp[i + 255] = (byte)x;
      Log[x] = i;
      x <<= 1;
      if ((x & 0x100) != 0) x ^= 0x11D;
    }
    Exp[510] = Exp[0];
    Exp[511] = Exp[1];
  }

  public static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

  public static byte Inv(byte a) => a == 0 ? throw new DivideByZeroException("GF(2^8) has no inverse of 0.") : Exp[255 - Log[a]];

  /// <summary>a^power.</summary>
  public static byte Pow(int power) => Exp[((power % 255) + 255) % 255];

  /// <summary>Generator polynomial coefficients, highest degree first, for <paramref name="n"/> parity symbols.</summary>
  public static byte[] Generator(int n) {
    var g = new byte[] { 1 };
    for (var i = 0; i < n; ++i) {
      var root = Pow(1 + i);
      var next = new byte[g.Length + 1];
      for (var j = 0; j < g.Length; ++j) {
        next[j] ^= g[j];
        next[j + 1] ^= Mul(g[j], root);
      }
      g = next;
    }
    return g;
  }

  /// <summary>Inverts a square matrix in place (Gauss-Jordan); false when singular.</summary>
  public static bool Invert(byte[,] m) {
    var n = m.GetLength(0);
    var inv = new byte[n, n];
    for (var i = 0; i < n; ++i) inv[i, i] = 1;
    for (var col = 0; col < n; ++col) {
      var pivot = -1;
      for (var r = col; r < n; ++r)
        if (m[r, col] != 0) { pivot = r; break; }
      if (pivot < 0) return false;
      if (pivot != col)
        for (var k = 0; k < n; ++k) {
          (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
          (inv[col, k], inv[pivot, k]) = (inv[pivot, k], inv[col, k]);
        }
      var f = Inv(m[col, col]);
      for (var k = 0; k < n; ++k) {
        m[col, k] = Mul(m[col, k], f);
        inv[col, k] = Mul(inv[col, k], f);
      }
      for (var r = 0; r < n; ++r) {
        if (r == col || m[r, col] == 0) continue;
        var g = m[r, col];
        for (var k = 0; k < n; ++k) {
          m[r, k] ^= Mul(g, m[col, k]);
          inv[r, k] ^= Mul(g, inv[col, k]);
        }
      }
    }
    Array.Copy(inv, m, inv.Length);
    return true;
  }
}

/// <summary>
/// CRC-64 with the ECMA-182 polynomial, reflected, as a raw register: the RAR 5 recovery record
/// uses it from 0 without a final xor for data shards, and as CRC-64/XZ (from and xored with all
/// ones) for its blocks.
/// </summary>
internal static class RarCrc64 {
  private static readonly ulong[] Table = BuildTable();

  private static ulong[] BuildTable() {
    var t = new ulong[256];
    for (var i = 0; i < 256; ++i) {
      var c = (ulong)i;
      for (var k = 0; k < 8; ++k) c = (c & 1) != 0 ? (c >> 1) ^ 0xC96C5795D7870F42UL : c >> 1;
      t[i] = c;
    }
    return t;
  }

  public static ulong Update(ulong register, ReadOnlySpan<byte> data) {
    var t = Table;
    foreach (var b in data) register = t[(byte)(register ^ b)] ^ (register >> 8);
    return register;
  }

  public static ulong Xz(ReadOnlySpan<byte> data) => ~Update(ulong.MaxValue, data);
}

/// <summary>Raw CRC-32 register updates, for the start values RAR 3/4 recovery data uses.</summary>
internal static class RarCrc32Raw {
  private static readonly uint[] Table = BuildTable();

  private static uint[] BuildTable() {
    var t = new uint[256];
    for (var i = 0u; i < 256; ++i) {
      var c = i;
      for (var k = 0; k < 8; ++k) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320U : c >> 1;
      t[i] = c;
    }
    return t;
  }

  public static uint Update(uint register, ReadOnlySpan<byte> data) {
    var t = Table;
    foreach (var b in data) register = t[(byte)(register ^ b)] ^ (register >> 8);
    return register;
  }
}
