using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET7_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics.Arm;
using ArmCrc = System.Runtime.Intrinsics.Arm.Crc32;
#endif

namespace Compression.Core.Checksums;

/// <summary>
/// Table-driven CRC-32 implementation with configurable polynomial, slicing-by-4 acceleration,
/// and hardware intrinsics for SSE4.2 (CRC-32C), PCLMULQDQ (IEEE), and ARM CRC32 when available.
/// </summary>
public sealed class Crc32 : IChecksum {
  /// <summary>
  /// Standard IEEE 802.3 polynomial (used by ZIP, GZIP, PNG, etc.).
  /// </summary>
  public const uint Ieee = 0xEDB88320u;

  /// <summary>
  /// Castagnoli (CRC-32C) polynomial.
  /// </summary>
  public const uint Castagnoli = 0x82F63B78u;

  private readonly uint[][] _tables;
  private readonly uint _polynomial;
  private uint _crc;

  /// <summary>
  /// Initializes a new <see cref="Crc32"/> with the specified polynomial.
  /// </summary>
  /// <param name="polynomial">The reflected polynomial. Defaults to <see cref="Ieee"/>.</param>
  public Crc32(uint polynomial = Crc32.Ieee) {
    this._tables = CrcTableGenerator.GenerateSlicingTables(polynomial);
    this._polynomial = polynomial;
    this._crc = 0xFFFFFFFFu;
  }

  /// <inheritdoc />
  public uint Value => this._crc ^ 0xFFFFFFFFu;

  /// <inheritdoc />
  public void Reset() => this._crc = 0xFFFFFFFFu;

  /// <inheritdoc />
  public void Update(byte b) => this._crc = this._tables[0][(this._crc ^ b) & 0xFF] ^ (this._crc >> 8);

  /// <inheritdoc />
  public void Update(ReadOnlySpan<byte> data) {
#if NET7_0_OR_GREATER
    // Hardware-accelerated path for CRC-32C (Castagnoli polynomial).
    if (this._polynomial == Castagnoli) {
      if (Sse42.IsSupported) {
        this._crc = UpdateCrc32CSse42(this._crc, data);
        return;
      }
      if (ArmCrc.IsSupported) {
        this._crc = UpdateCrc32CArm(this._crc, data);
        return;
      }
    }

    // PCLMULQDQ-accelerated CRC-32 (IEEE polynomial) on x86/x64.
    if (this._polynomial == Ieee && Pclmulqdq.IsSupported && Sse41.IsSupported) {
      this._crc = UpdateCrc32IeePclmulqdq(this._crc, data, this._tables);
      return;
    }

    // Hardware-accelerated CRC-32 (IEEE polynomial) on ARM.
    if (this._polynomial == Ieee && ArmCrc.IsSupported) {
      this._crc = UpdateCrc32Arm(this._crc, data);
      return;
    }
#endif

    // Software fallback: slicing-by-4.
    this._crc = UpdateSlicingBy4(this._crc, data, this._tables);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint UpdateSlicingBy4(uint crc, ReadOnlySpan<byte> data, uint[][] tables) {
    var t0 = tables[0];
    var t1 = tables[1];
    var t2 = tables[2];
    var t3 = tables[3];
    var i = 0;

    // Slicing-by-4: process 4 bytes per iteration.
    var end4 = data.Length - 3;
    while (i < end4) {
      crc ^= (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
      crc = t3[crc & 0xFF] ^ t2[(crc >> 8) & 0xFF] ^ t1[(crc >> 16) & 0xFF] ^ t0[(crc >> 24) & 0xFF];
      i += 4;
    }

    // Scalar tail.
    for (; i < data.Length; ++i)
      crc = t0[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

    return crc;
  }

#if NET7_0_OR_GREATER

  /// <summary>
  /// SSE4.2 CRC-32C acceleration: uses the hardware CRC32 instruction for the Castagnoli polynomial.
  /// Processes 8 bytes per iteration on x64, 4 bytes on x86.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint UpdateCrc32CSse42(uint crc, ReadOnlySpan<byte> data) {
    var i = 0;

    // Process 8 bytes at a time on x64.
    if (Sse42.X64.IsSupported) {
      var crc64 = (ulong)crc;
      var end8 = data.Length - 7;
      while (i < end8) {
        crc64 = Sse42.X64.Crc32(crc64, MemoryMarshal.Read<ulong>(data.Slice(i, 8)));
        i += 8;
      }
      crc = (uint)crc64;
    }

    // Process 4 bytes at a time.
    var end4 = data.Length - 3;
    while (i < end4) {
      crc = Sse42.Crc32(crc, MemoryMarshal.Read<uint>(data.Slice(i, 4)));
      i += 4;
    }

    // Scalar tail.
    for (; i < data.Length; ++i)
      crc = Sse42.Crc32(crc, data[i]);

    return crc;
  }

  /// <summary>
  /// ARM CRC32C acceleration: uses the hardware CRC32C instruction for the Castagnoli polynomial.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint UpdateCrc32CArm(uint crc, ReadOnlySpan<byte> data) {
    var i = 0;

    if (ArmCrc.Arm64.IsSupported) {
      var end8 = data.Length - 7;
      while (i < end8) {
        crc = ArmCrc.Arm64.ComputeCrc32C(crc, MemoryMarshal.Read<ulong>(data.Slice(i, 8)));
        i += 8;
      }
    }

    var end4 = data.Length - 3;
    while (i < end4) {
      crc = ArmCrc.ComputeCrc32C(crc, MemoryMarshal.Read<uint>(data.Slice(i, 4)));
      i += 4;
    }

    for (; i < data.Length; ++i)
      crc = ArmCrc.ComputeCrc32C(crc, data[i]);

    return crc;
  }

  /// <summary>
  /// PCLMULQDQ-accelerated CRC-32 for the IEEE polynomial (0xEDB88320).
  /// Processes 64 bytes at a time using carryless multiplication to fold four 128-bit
  /// vectors in parallel, then reduces to the final 32-bit CRC.
  /// Algorithm based on Intel's CRC whitepaper and zlib/Chromium's crc32_simd.c.
  /// Falls back to slicing-by-4 for the tail.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint UpdateCrc32IeePclmulqdq(uint crc, ReadOnlySpan<byte> data, uint[][] tables) {
    var i = 0;

    // Need at least 64 bytes to use the PCLMULQDQ path
    if (data.Length >= 64) {
      // Reflected IEEE polynomial fold constants from zlib/Chromium crc32_simd.c.
      // k1k2: fold-by-4 constants (x^(512+64) mod P, x^(512) mod P)
      var k1k2 = Vector128.Create(0x0154442bd4UL, 0x01c6e41596UL);
      // k3k4: fold-by-1 constants (x^(128+64) mod P, x^(128) mod P)
      var k3k4 = Vector128.Create(0x01751997d0UL, 0x00ccaa009eUL);
      // k5k6: 96-to-64 reduction constant (x^64 mod P)
      var k5k6 = Vector128.Create(0x0163cd6124UL, 0UL);
      // Barrett: mu (floor(x^64/P)) and P' (bit-reflected polynomial with x^32 term)
      var poly = Vector128.Create(0x01db710641UL, 0x01f7011641UL);

      // Load first 64 bytes into four 128-bit vectors
      var x0 = MemoryMarshal.Read<Vector128<byte>>(data);
      var x1 = MemoryMarshal.Read<Vector128<byte>>(data[16..]);
      var x2 = MemoryMarshal.Read<Vector128<byte>>(data[32..]);
      var x3 = MemoryMarshal.Read<Vector128<byte>>(data[48..]);
      i = 64;

      // XOR the initial CRC into the first vector
      x0 = Sse2.Xor(x0, Vector128.Create(crc, 0, 0, 0).AsByte());

      // Main folding loop: process 64 bytes per iteration using fold-by-4
      while (i + 64 <= data.Length) {
        var y0 = MemoryMarshal.Read<Vector128<byte>>(data[i..]);
        var y1 = MemoryMarshal.Read<Vector128<byte>>(data[(i + 16)..]);
        var y2 = MemoryMarshal.Read<Vector128<byte>>(data[(i + 32)..]);
        var y3 = MemoryMarshal.Read<Vector128<byte>>(data[(i + 48)..]);
        i += 64;

        x0 = FoldVector(x0, y0, k1k2);
        x1 = FoldVector(x1, y1, k1k2);
        x2 = FoldVector(x2, y2, k1k2);
        x3 = FoldVector(x3, y3, k1k2);
      }

      // Fold 4 vectors into 1 using fold-by-1
      x0 = FoldVector(x0, x1, k3k4);
      x0 = FoldVector(x0, x2, k3k4);
      x0 = FoldVector(x0, x3, k3k4);

      // Process remaining 16-byte chunks
      while (i + 16 <= data.Length) {
        var y = MemoryMarshal.Read<Vector128<byte>>(data[i..]);
        i += 16;
        x0 = FoldVector(x0, y, k3k4);
      }

      // ── 128-bit to 32-bit reduction (zlib crc32_simd.c algorithm) ─────────

      // Step 1: 128-bit to 96-bit fold
      // Multiply high 64 bits of x0 by k3 (lo of k3k4), XOR with low 64 bits shifted
      var v = x0.AsUInt64();
      var tmp0 = Pclmulqdq.CarrylessMultiply(v, k3k4.AsUInt64(), 0x10); // hi(x0) * k3
      v = Sse2.Xor(Sse2.ShiftRightLogical128BitLane(v.AsByte(), 8).AsUInt64(), tmp0);

      // Step 2: 96-bit to 64-bit fold
      // Multiply low 32 bits by k5, XOR with bits shifted right by 32
      var mask32 = Vector128.Create(~0, 0, ~0, 0).AsUInt64(); // keeps low 32 of each 64-bit lane
      var tmp1 = Sse2.ShiftRightLogical128BitLane(v.AsByte(), 4).AsUInt64(); // shift right 4 bytes
      v = Sse2.And(v, mask32); // keep only low 32 bits
      v = Pclmulqdq.CarrylessMultiply(v, k5k6.AsUInt64(), 0x00); // lo(v) * k5
      v = Sse2.Xor(v, tmp1);

      // Step 3: Barrett reduction (64-bit to 32-bit)
      var tmp2 = Sse2.And(v, mask32); // low 32 bits
      tmp2 = Pclmulqdq.CarrylessMultiply(tmp2, poly.AsUInt64(), 0x10); // * hi(poly) = * P'
      tmp2 = Sse2.And(tmp2, mask32); // mask to low 32
      tmp2 = Pclmulqdq.CarrylessMultiply(tmp2, poly.AsUInt64(), 0x00); // * lo(poly) = * mu
      v = Sse2.Xor(v, tmp2);

      // CRC is in bits 32..63 (element index 1 of uint32 view)
      crc = Sse41.Extract(v.AsUInt32(), 1);
    }

    // Process remaining bytes with slicing-by-4
    if (i < data.Length)
      crc = UpdateSlicingBy4(crc, data[i..], tables);

    return crc;
  }

  /// <summary>
  /// Folds one 128-bit vector into the next using carryless multiplication.
  /// result = clmul(lo(a), lo(k)) XOR clmul(hi(a), hi(k)) XOR b
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static Vector128<byte> FoldVector(Vector128<byte> a, Vector128<byte> b, Vector128<ulong> k) =>
    Sse2.Xor(
      Sse2.Xor(
        Pclmulqdq.CarrylessMultiply(a.AsUInt64(), k, 0x00).AsByte(),
        Pclmulqdq.CarrylessMultiply(a.AsUInt64(), k, 0x11).AsByte()),
      b);

  /// <summary>
  /// ARM CRC32 acceleration: uses the hardware CRC32 instruction for the IEEE polynomial.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint UpdateCrc32Arm(uint crc, ReadOnlySpan<byte> data) {
    var i = 0;

    if (ArmCrc.Arm64.IsSupported) {
      var end8 = data.Length - 7;
      while (i < end8) {
        crc = ArmCrc.Arm64.ComputeCrc32(crc, MemoryMarshal.Read<ulong>(data.Slice(i, 8)));
        i += 8;
      }
    }

    var end4 = data.Length - 3;
    while (i < end4) {
      crc = ArmCrc.ComputeCrc32(crc, MemoryMarshal.Read<uint>(data.Slice(i, 4)));
      i += 4;
    }

    for (; i < data.Length; ++i)
      crc = ArmCrc.ComputeCrc32(crc, data[i]);

    return crc;
  }

#endif

  /// <summary>
  /// Computes the CRC-32 of the given data in a single call using the IEEE polynomial.
  /// </summary>
  /// <param name="data">The data to checksum.</param>
  /// <returns>The CRC-32 value.</returns>
  public static uint Compute(ReadOnlySpan<byte> data) {
    var crc = new Crc32();
    crc.Update(data);
    return crc.Value;
  }

  /// <summary>
  /// Computes the CRC-32 of the given data with the specified polynomial.
  /// </summary>
  /// <param name="data">The data to checksum.</param>
  /// <param name="polynomial">The reflected polynomial.</param>
  /// <returns>The CRC-32 value.</returns>
  public static uint Compute(ReadOnlySpan<byte> data, uint polynomial) {
    var crc = new Crc32(polynomial);
    crc.Update(data);
    return crc.Value;
  }
}
