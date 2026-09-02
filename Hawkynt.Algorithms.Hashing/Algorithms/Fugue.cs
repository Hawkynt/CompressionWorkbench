using System.Buffers.Binary;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// Fugue SHA-3 candidate family. All digest sizes share the same state-machine implementation;
/// the selected digest size chooses the Fugue-2, Fugue-3 or Fugue-4 round schedule.
/// </summary>
public static class Fugue {
  /// <summary>
  /// Gets the supported hash-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    new(224, 256, 32),
    new(384, 512, 128)
  ];

  private static readonly uint[] Iv224 = [0xf4c9120dU, 0x6286f757U, 0xee39e01cU, 0xe074e3cbU, 0xa1127c62U, 0x9a43d215U, 0xbd8d679aU];
  private static readonly uint[] Iv256 = [0xe952bddeU, 0x6671135fU, 0xe0d4f668U, 0xd2b0b594U, 0xf96c621dU, 0xfbf929deU, 0x9149e899U, 0x34f8c248U];
  private static readonly uint[] Iv384 = [0xaa61ec0dU, 0x31252e1fU, 0xa01db4c7U, 0x00600985U, 0x215ef44aU, 0x741b5e9cU, 0xfa693e9aU, 0x473eb040U, 0xe502ae8aU, 0xa99c25e0U, 0xbc95517cU, 0x5c1095a1U];
  private static readonly uint[] Iv512 = [0x8807a57eU, 0xe616af75U, 0xc5d3e4dbU, 0xac9ab027U, 0xd915f117U, 0xb6eecc54U, 0x06e8020bU, 0x4a92efd1U, 0xaac6e2c9U, 0xddb21398U, 0xcae65838U, 0x437f203fU, 0x25ea78e7U, 0x951fddd6U, 0xda6ed11dU, 0xe13e3567U];

  private static readonly byte[] AesSBox = [
    0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76,
    0xca,0x82,0xc9,0x7d,0xfa,0x59,0x47,0xf0,0xad,0xd4,0xa2,0xaf,0x9c,0xa4,0x72,0xc0,
    0xb7,0xfd,0x93,0x26,0x36,0x3f,0xf7,0xcc,0x34,0xa5,0xe5,0xf1,0x71,0xd8,0x31,0x15,
    0x04,0xc7,0x23,0xc3,0x18,0x96,0x05,0x9a,0x07,0x12,0x80,0xe2,0xeb,0x27,0xb2,0x75,
    0x09,0x83,0x2c,0x1a,0x1b,0x6e,0x5a,0xa0,0x52,0x3b,0xd6,0xb3,0x29,0xe3,0x2f,0x84,
    0x53,0xd1,0x00,0xed,0x20,0xfc,0xb1,0x5b,0x6a,0xcb,0xbe,0x39,0x4a,0x4c,0x58,0xcf,
    0xd0,0xef,0xaa,0xfb,0x43,0x4d,0x33,0x85,0x45,0xf9,0x02,0x7f,0x50,0x3c,0x9f,0xa8,
    0x51,0xa3,0x40,0x8f,0x92,0x9d,0x38,0xf5,0xbc,0xb6,0xda,0x21,0x10,0xff,0xf3,0xd2,
    0xcd,0x0c,0x13,0xec,0x5f,0x97,0x44,0x17,0xc4,0xa7,0x7e,0x3d,0x64,0x5d,0x19,0x73,
    0x60,0x81,0x4f,0xdc,0x22,0x2a,0x90,0x88,0x46,0xee,0xb8,0x14,0xde,0x5e,0x0b,0xdb,
    0xe0,0x32,0x3a,0x0a,0x49,0x06,0x24,0x5c,0xc2,0xd3,0xac,0x62,0x91,0x95,0xe4,0x79,
    0xe7,0xc8,0x37,0x6d,0x8d,0xd5,0x4e,0xa9,0x6c,0x56,0xf4,0xea,0x65,0x7a,0xae,0x08,
    0xba,0x78,0x25,0x2e,0x1c,0xa6,0xb4,0xc6,0xe8,0xdd,0x74,0x1f,0x4b,0xbd,0x8b,0x8a,
    0x70,0x3e,0xb5,0x66,0x48,0x03,0xf6,0x0e,0x61,0x35,0x57,0xb9,0x86,0xc1,0x1d,0x9e,
    0xe1,0xf8,0x98,0x11,0x69,0xd9,0x8e,0x94,0x9b,0x1e,0x87,0xe9,0xce,0x55,0x28,0xdf,
    0x8c,0xa1,0x89,0x0d,0xbf,0xe6,0x42,0x68,0x41,0x99,0x2d,0x0f,0xb0,0x54,0xbb,0x16
  ];

  // The JavaScript/sphlib port materializes four 256-word MixColumns tables. They are rotations
  // of the same AES-derived table, so generate the base table once and rotate at lookup time.
  private static readonly uint[] Mix0 = BuildMixTable();

  /// <summary>
  /// Computes the Fugue hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 256) {
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));

    var stateSize = hashSizeBits <= 256 ? 30 : 36;
    var family = hashSizeBits <= 256 ? 2 : hashSizeBits == 384 ? 3 : 4;
    var cases = family == 2 ? 5 : family == 3 ? 4 : 3;
    var rcm = family == 2 ? 6 : family == 3 ? 9 : 12;
    var state = new uint[stateSize];
    var iv = hashSizeBits switch { 224 => Iv224, 256 => Iv256, 384 => Iv384, _ => Iv512 };
    Array.Copy(iv, 0, state, state.Length - iv.Length, iv.Length);

    var shift = 0;
    var offset = 0;
    while (offset + 4 <= data.Length) {
      ProcessWord(state, family, BinaryPrimitives.ReadUInt32BigEndian(data[offset..]), shift);
      shift = (shift + 1) % cases;
      offset += 4;
    }

    if (offset < data.Length) {
      Span<byte> partial = stackalloc byte[4];
      partial.Clear();
      data[offset..].CopyTo(partial);
      ProcessWord(state, family, BinaryPrimitives.ReadUInt32BigEndian(partial), shift);
      shift = (shift + 1) % cases;
    }

    var bitLength = checked((ulong)data.Length * 8UL);
    ProcessWord(state, family, (uint)(bitLength >> 32), shift);
    shift = (shift + 1) % cases;
    ProcessWord(state, family, (uint)bitLength, shift);
    shift = (shift + 1) % cases;

    if (shift != 0)
      RotateStateRight(state, shift * rcm);

    Finalize(state, family);
    return Extract(state, hashSizeBits, family);
  }

  private static void ProcessWord(uint[] s, int family, uint word, int shift) {
    switch (family) {
      case 2: Fugue2Round(s, word, shift); break;
      case 3: Fugue3Round(s, word, shift); break;
      default: Fugue4Round(s, word, shift); break;
    }
  }

  private static void Fugue2Round(uint[] s, uint q, int shift) {
    switch (shift) {
      case 0:
        Tix2(s,q,0,1,8,10,24); Cmix(s,27,28,29,1,2,3,12,13,14); Smix(s,27,28,29,0); Cmix(s,24,25,26,28,29,0,9,10,11); Smix(s,24,25,26,27); break;
      case 1:
        Tix2(s,q,24,25,2,4,18); Cmix(s,21,22,23,25,26,27,6,7,8); Smix(s,21,22,23,24); Cmix(s,18,19,20,22,23,24,3,4,5); Smix(s,18,19,20,21); break;
      case 2:
        Tix2(s,q,18,19,26,28,12); Cmix(s,15,16,17,19,20,21,0,1,2); Smix(s,15,16,17,18); Cmix(s,12,13,14,16,17,18,27,28,29); Smix(s,12,13,14,15); break;
      case 3:
        Tix2(s,q,12,13,20,22,6); Cmix(s,9,10,11,13,14,15,24,25,26); Smix(s,9,10,11,12); Cmix(s,6,7,8,10,11,12,21,22,23); Smix(s,6,7,8,9); break;
      default:
        Tix2(s,q,6,7,14,16,0); Cmix(s,3,4,5,7,8,9,18,19,20); Smix(s,3,4,5,6); Cmix(s,0,1,2,4,5,6,15,16,17); Smix(s,0,1,2,3); break;
    }
  }

  private static void Fugue3Round(uint[] s, uint q, int shift) {
    switch (shift) {
      case 0:
        Tix3(s,q,0,1,4,8,16,27,30); Cmix(s,33,34,35,1,2,3,15,16,17); Smix(s,33,34,35,0); Cmix(s,30,31,32,34,35,0,12,13,14); Smix(s,30,31,32,33); Cmix(s,27,28,29,31,32,33,9,10,11); Smix(s,27,28,29,30); break;
      case 1:
        Tix3(s,q,27,28,31,35,7,18,21); Cmix(s,24,25,26,28,29,30,6,7,8); Smix(s,24,25,26,27); Cmix(s,21,22,23,25,26,27,3,4,5); Smix(s,21,22,23,24); Cmix(s,18,19,20,22,23,24,0,1,2); Smix(s,18,19,20,21); break;
      case 2:
        Tix3(s,q,18,19,22,26,34,9,12); Cmix(s,15,16,17,19,20,21,33,34,35); Smix(s,15,16,17,18); Cmix(s,12,13,14,16,17,18,30,31,32); Smix(s,12,13,14,15); Cmix(s,9,10,11,13,14,15,27,28,29); Smix(s,9,10,11,12); break;
      default:
        Tix3(s,q,9,10,13,17,25,0,3); Cmix(s,6,7,8,10,11,12,24,25,26); Smix(s,6,7,8,9); Cmix(s,3,4,5,7,8,9,21,22,23); Smix(s,3,4,5,6); Cmix(s,0,1,2,4,5,6,18,19,20); Smix(s,0,1,2,3); break;
    }
  }

  private static void Fugue4Round(uint[] s, uint q, int shift) {
    switch (shift) {
      case 0:
        Tix4(s,q,0,1,4,7,8,22,24,27,30); Cmix(s,33,34,35,1,2,3,15,16,17); Smix(s,33,34,35,0); Cmix(s,30,31,32,34,35,0,12,13,14); Smix(s,30,31,32,33); Cmix(s,27,28,29,31,32,33,9,10,11); Smix(s,27,28,29,30); Cmix(s,24,25,26,28,29,30,6,7,8); Smix(s,24,25,26,27); break;
      case 1:
        Tix4(s,q,24,25,28,31,32,10,12,15,18); Cmix(s,21,22,23,25,26,27,3,4,5); Smix(s,21,22,23,24); Cmix(s,18,19,20,22,23,24,0,1,2); Smix(s,18,19,20,21); Cmix(s,15,16,17,19,20,21,33,34,35); Smix(s,15,16,17,18); Cmix(s,12,13,14,16,17,18,30,31,32); Smix(s,12,13,14,15); break;
      default:
        Tix4(s,q,12,13,16,19,20,34,0,3,6); Cmix(s,9,10,11,13,14,15,27,28,29); Smix(s,9,10,11,12); Cmix(s,6,7,8,10,11,12,24,25,26); Smix(s,6,7,8,9); Cmix(s,3,4,5,7,8,9,21,22,23); Smix(s,3,4,5,6); Cmix(s,0,1,2,4,5,6,18,19,20); Smix(s,0,1,2,3); break;
    }
  }

  private static void Finalize(uint[] s, int family) {
    if (family == 2) {
      for (var i = 0; i < 10; ++i) { RotateStateRight(s,3); Cmix(s,0,1,2,4,5,6,15,16,17); Smix(s,0,1,2,3); }
      for (var i = 0; i < 13; ++i) {
        s[4]^=s[0]; s[15]^=s[0]; RotateStateRight(s,15); Smix(s,0,1,2,3);
        s[4]^=s[0]; s[16]^=s[0]; RotateStateRight(s,14); Smix(s,0,1,2,3);
      }
      s[4]^=s[0]; s[15]^=s[0];
      return;
    }

    if (family == 3) {
      for (var i = 0; i < 18; ++i) { RotateStateRight(s,3); Cmix(s,0,1,2,4,5,6,18,19,20); Smix(s,0,1,2,3); }
      for (var i = 0; i < 13; ++i) {
        s[4]^=s[0]; s[12]^=s[0]; s[24]^=s[0]; RotateStateRight(s,12); Smix(s,0,1,2,3);
        s[4]^=s[0]; s[13]^=s[0]; s[24]^=s[0]; RotateStateRight(s,12); Smix(s,0,1,2,3);
        s[4]^=s[0]; s[13]^=s[0]; s[25]^=s[0]; RotateStateRight(s,11); Smix(s,0,1,2,3);
      }
      s[4]^=s[0]; s[12]^=s[0]; s[24]^=s[0];
      return;
    }

    for (var i = 0; i < 32; ++i) { RotateStateRight(s,3); Cmix(s,0,1,2,4,5,6,18,19,20); Smix(s,0,1,2,3); }
    for (var i = 0; i < 13; ++i) {
      s[4]^=s[0]; s[9]^=s[0]; s[18]^=s[0]; s[27]^=s[0]; RotateStateRight(s,9); Smix(s,0,1,2,3);
      s[4]^=s[0]; s[10]^=s[0]; s[18]^=s[0]; s[27]^=s[0]; RotateStateRight(s,9); Smix(s,0,1,2,3);
      s[4]^=s[0]; s[10]^=s[0]; s[19]^=s[0]; s[27]^=s[0]; RotateStateRight(s,9); Smix(s,0,1,2,3);
      s[4]^=s[0]; s[10]^=s[0]; s[19]^=s[0]; s[28]^=s[0]; RotateStateRight(s,8); Smix(s,0,1,2,3);
    }
    s[4]^=s[0]; s[9]^=s[0]; s[18]^=s[0]; s[27]^=s[0];
  }

  private static byte[] Extract(uint[] s, int bits, int family) {
    ReadOnlySpan<int> indices = family switch {
      2 => [1,2,3,4,15,16,17,18],
      3 => [1,2,3,4,12,13,14,15,24,25,26,27],
      _ => [1,2,3,4,9,10,11,12,18,19,20,21,27,28,29,30]
    };
    var result = new byte[bits / 8];
    for (var i = 0; i < result.Length / 4; ++i)
      BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4), s[indices[i]]);
    return result;
  }

  private static void Tix2(uint[] s, uint q, int i00, int i01, int i08, int i10, int i24) {
    s[i10] ^= s[i00]; s[i00] = q; s[i08] ^= s[i00]; s[i01] ^= s[i24];
  }

  private static void Tix3(uint[] s, uint q, int i00, int i01, int i04, int i08, int i16, int i27, int i30) {
    s[i16] ^= s[i00]; s[i00] = q; s[i08] ^= s[i00]; s[i01] ^= s[i27]; s[i04] ^= s[i30];
  }

  private static void Tix4(uint[] s, uint q, int i00, int i01, int i04, int i07, int i08, int i22, int i24, int i27, int i30) {
    s[i22] ^= s[i00]; s[i00] = q; s[i08] ^= s[i00]; s[i01] ^= s[i24]; s[i04] ^= s[i27]; s[i07] ^= s[i30];
  }

  private static void Cmix(uint[] s, int i00, int i01, int i02, int i04, int i05, int i06, int ix0, int ix1, int ix2) {
    s[i00]^=s[i04]; s[i01]^=s[i05]; s[i02]^=s[i06]; s[ix0]^=s[i04]; s[ix1]^=s[i05]; s[ix2]^=s[i06];
  }

  private static void Smix(uint[] s, int i0, int i1, int i2, int i3) {
    var x0=s[i0]; var x1=s[i1]; var x2=s[i2]; var x3=s[i3];
    uint c0=0,c1=0,c2=0,c3=0,r0=0,r1=0,r2=0,r3=0,t;

    t=Mix(0,Byte(x0,3)); c0^=t;
    t=Mix(1,Byte(x0,2)); c0^=t; r1^=t;
    t=Mix(2,Byte(x0,1)); c0^=t; r2^=t;
    t=Mix(3,Byte(x0,0)); c0^=t; r3^=t;
    t=Mix(0,Byte(x1,3)); c1^=t; r0^=t;
    t=Mix(1,Byte(x1,2)); c1^=t;
    t=Mix(2,Byte(x1,1)); c1^=t; r2^=t;
    t=Mix(3,Byte(x1,0)); c1^=t; r3^=t;
    t=Mix(0,Byte(x2,3)); c2^=t; r0^=t;
    t=Mix(1,Byte(x2,2)); c2^=t; r1^=t;
    t=Mix(2,Byte(x2,1)); c2^=t;
    t=Mix(3,Byte(x2,0)); c2^=t; r3^=t;
    t=Mix(0,Byte(x3,3)); c3^=t; r0^=t;
    t=Mix(1,Byte(x3,2)); c3^=t; r1^=t;
    t=Mix(2,Byte(x3,1)); c3^=t; r2^=t;
    t=Mix(3,Byte(x3,0)); c3^=t;

    s[i0]=Pack(Byte(c0^r0,3),Byte(c1^r1,2),Byte(c2^r2,1),Byte(c3^r3,0));
    s[i1]=Pack((byte)(Byte(c1,3)^Byte(r0,2)),(byte)(Byte(c2,2)^Byte(r1,1)),(byte)(Byte(c3,1)^Byte(r2,0)),(byte)(Byte(c0,0)^Byte(r3,3)));
    s[i2]=Pack((byte)(Byte(c2,3)^Byte(r0,1)),(byte)(Byte(c3,2)^Byte(r1,0)),(byte)(Byte(c0,1)^Byte(r2,3)),(byte)(Byte(c1,0)^Byte(r3,2)));
    s[i3]=Pack((byte)(Byte(c3,3)^Byte(r0,0)),(byte)(Byte(c0,2)^Byte(r1,3)),(byte)(Byte(c1,1)^Byte(r2,2)),(byte)(Byte(c2,0)^Byte(r3,1)));
  }

  private static void RotateStateRight(uint[] s, int count) {
    count %= s.Length;
    if (count == 0) return;
    var copy = (uint[])s.Clone();
    for (var i = 0; i < s.Length; ++i)
      s[(i + count) % s.Length] = copy[i];
  }

  private static uint[] BuildMixTable() {
    var result = new uint[256];
    for (var i = 0; i < result.Length; ++i) {
      var value = AesSBox[i];
      result[i] = Pack(value, value, GfMultiply(value, 7), GfMultiply(value, 4));
    }
    return result;
  }

  private static uint Mix(int table, byte value) => RotateRight(Mix0[value], table * 8);
  private static uint RotateRight(uint value, int count) => count == 0 ? value : (value >> count) | (value << (32 - count));
  private static byte Byte(uint value, int index) => (byte)(value >> (index * 8));
  private static uint Pack(byte a, byte b, byte c, byte d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

  private static byte GfMultiply(byte a, int b) {
    var x = a;
    var multiplier = b;
    var result = 0;
    for (var i = 0; i < 8; ++i) {
      if ((multiplier & 1) != 0) result ^= x;
      x = (byte)(((x << 1) ^ ((x & 0x80) != 0 ? 0x11b : 0)) & 0xff);
      multiplier >>= 1;
    }
    return (byte)result;
  }
}
