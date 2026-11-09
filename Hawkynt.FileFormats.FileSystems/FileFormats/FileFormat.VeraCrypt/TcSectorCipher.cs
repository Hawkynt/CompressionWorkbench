using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Registry;

namespace FileFormat.VeraCrypt;

/// <summary>
/// Sector encryption for one TrueCrypt/VeraCrypt cipher chain, keyed from the volume's 256-byte
/// key area. Sector numbers passed in are relative to the data area; <c>ivBase</c> is added to
/// form the number the IV or tweak is derived from. Also decrypts and encrypts the 448-byte
/// encrypted part of a volume header, which every mode treats as starting at IV block zero.
/// </summary>
public sealed class TcSectorCipher : ISectorCipher {
  public const int SectorSize = 512;

  private readonly TcCipherChain _chain;
  private readonly TcBlockCipher[] _ciphers;
  private readonly TcBlockCipher[] _tweaks = [];   // XTS second keys
  private readonly byte[][] _lrwSteps = [];        // LRW: K2·(2^(t+1)−1), t = 0..127
  private readonly byte[][] _lrwPowers = [];       // LRW: K2·x^k, k = 0..127
  private readonly int _lrwBlock = 16;             // LRW: 16 (GF(2^128)) or 8 (GF(2^64), TrueCrypt 4.x 64-bit ciphers)
  private readonly byte[] _ivArea = [];            // CBC: IV seed, then whitening seed (32 bytes)
  private readonly byte[] _headerWhitening = [];   // CBC header: 8 bytes
  private readonly long _ivBase;
  private readonly bool _headerKey;

  /// <param name="chain">The chain.</param>
  /// <param name="keys">The key area: the 256-byte master-key field, or the derived header key.</param>
  /// <param name="ivBase">IV sector number of data sector 0.</param>
  /// <param name="headerKey">Keyed for header decryption (legacy CBC derives its IV and whitening differently there).</param>
  public TcSectorCipher(TcCipherChain chain, ReadOnlySpan<byte> keys, long ivBase = 0, bool headerKey = false) {
    ArgumentNullException.ThrowIfNull(chain);
    this._chain = chain;
    this._ivBase = ivBase;
    this._headerKey = headerKey;
    var n = chain.Ciphers.Count;
    this._ciphers = new TcBlockCipher[n];
    switch (chain.Mode) {
      case TcMode.Xts:
        this._tweaks = new TcBlockCipher[n];
        for (var i = 0; i < n; ++i) {
          var c = chain.Ciphers[i];
          var half = c.KeySize / 2;
          this._ciphers[i] = TcBlockCipher.Create(c.Name, keys.Slice(c.KeyOffset, half));
          this._tweaks[i] = TcBlockCipher.Create(c.Name, keys.Slice(c.IvOffset, half));
        }
        break;
      case TcMode.Lrw:
        for (var i = 0; i < n; ++i) {
          var c = chain.Ciphers[i];
          this._ciphers[i] = TcBlockCipher.Create(c.Name, keys.Slice(c.KeyOffset, c.KeySize - 16));
        }
        this._lrwBlock = chain.Ciphers[0].BlockSize;
        (this._lrwPowers, this._lrwSteps) = LrwTables(keys[..this._lrwBlock]);
        break;
      default:
        for (var i = 0; i < n; ++i) {
          var c = chain.Ciphers[i];
          this._ciphers[i] = TcBlockCipher.Create(c.Name, keys.Slice(c.KeyOffset, c.KeySize));
        }
        this._ivArea = keys[..32].ToArray();
        this._headerWhitening = keys.Slice(8, 8).ToArray();
        break;
    }
  }

  /// <summary>The chain this cipher implements.</summary>
  public TcCipherChain Chain => this._chain;

  public void Decrypt(Span<byte> data, long firstSector) {
    for (var off = 0; off < data.Length; off += SectorSize)
      this.Process(data.Slice(off, SectorSize), this._ivBase + firstSector + off / SectorSize, encrypt: false);
  }

  public void Encrypt(Span<byte> data, long firstSector) {
    for (var off = 0; off < data.Length; off += SectorSize)
      this.Process(data.Slice(off, SectorSize), this._ivBase + firstSector + off / SectorSize, encrypt: true);
  }

  /// <summary>Decrypts the 448-byte encrypted part of a header in place.</summary>
  public void DecryptHeader(Span<byte> area) => this.ProcessHeader(area, encrypt: false);

  /// <summary>Encrypts the 448-byte encrypted part of a header in place.</summary>
  public void EncryptHeader(Span<byte> area) => this.ProcessHeader(area, encrypt: true);

  private void ProcessHeader(Span<byte> area, bool encrypt) {
    switch (this._chain.Mode) {
      case TcMode.Xts:
        this.XtsUnit(area, 0, encrypt);
        break;
      case TcMode.Lrw:
        this.LrwUnit(area, 1, encrypt);
        break;
      case TcMode.CbcOuter:
        var bs = this._ciphers[0].BlockSize;
        if (!encrypt) Whiten8(area, this._headerWhitening);
        this.CbcComposite(area, this._ivArea.AsSpan(0, bs), encrypt);
        if (encrypt) Whiten8(area, this._headerWhitening);
        break;
      case TcMode.CbcInner:
        // cryptsetup removes the whitening once per layer, each layer with its own CBC pass.
        if (encrypt) {
          for (var j = 0; j < this._ciphers.Length; ++j) {
            Cbc(this._ciphers[j], area, this._ivArea.AsSpan(0, this._ciphers[j].BlockSize), encrypt: true);
            Whiten8(area, this._headerWhitening);
          }
        } else {
          for (var j = this._ciphers.Length - 1; j >= 0; --j) {
            Whiten8(area, this._headerWhitening);
            Cbc(this._ciphers[j], area, this._ivArea.AsSpan(0, this._ciphers[j].BlockSize), encrypt: false);
          }
        }
        break;
    }
  }

  private void Process(Span<byte> sector, long number, bool encrypt) {
    switch (this._chain.Mode) {
      case TcMode.Xts:
        this.XtsUnit(sector, number, encrypt);
        break;
      case TcMode.Lrw:
        this.LrwUnit(sector, (ulong)number * (ulong)(SectorSize / this._lrwBlock) + 1, encrypt);
        break;
      default:
        this.CbcSector(sector, number, encrypt);
        break;
    }
  }

  // ── XTS (IEEE 1619), one pass per chain layer ─────────────────────────────

  private void XtsUnit(Span<byte> unit, long dataUnit, bool encrypt) {
    if (encrypt)
      for (var i = 0; i < this._ciphers.Length; ++i) Xts(this._ciphers[i], this._tweaks[i], unit, dataUnit, true);
    else
      for (var i = this._ciphers.Length - 1; i >= 0; --i) Xts(this._ciphers[i], this._tweaks[i], unit, dataUnit, false);
  }

  /// <summary>XTS over one data unit: the tweaks of all its blocks are XORed in and out around one bulk ECB pass.</summary>
  private static void Xts(TcBlockCipher cipher, TcBlockCipher tweakCipher, Span<byte> unit, long dataUnit, bool encrypt) {
    Span<byte> tweaks = stackalloc byte[unit.Length];
    var tweak = tweaks[..16];
    tweak.Clear();
    BinaryPrimitives.WriteInt64LittleEndian(tweak, dataUnit);
    tweakCipher.Encrypt(tweak);
    for (var i = 16; i < unit.Length; i += 16) {
      // tweak *= alpha in GF(2^128), little-endian byte order (IEEE 1619).
      var lo = BinaryPrimitives.ReadUInt64LittleEndian(tweaks[(i - 16)..]);
      var hi = BinaryPrimitives.ReadUInt64LittleEndian(tweaks[(i - 8)..]);
      var carry = (ulong)((long)hi >> 63) & 0x87;
      BinaryPrimitives.WriteUInt64LittleEndian(tweaks[i..], (lo << 1) ^ carry);
      BinaryPrimitives.WriteUInt64LittleEndian(tweaks[(i + 8)..], (hi << 1) | (lo >> 63));
    }
    Xor(unit, tweaks);
    if (encrypt) cipher.EncryptBlocks(unit);
    else cipher.DecryptBlocks(unit);
    Xor(unit, tweaks);
  }

  private static void Xor(Span<byte> data, ReadOnlySpan<byte> with) {
    var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(data);
    var w = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(with);
    for (var i = 0; i < d.Length; ++i) d[i] ^= w[i];
    for (var i = d.Length * 8; i < data.Length; ++i) data[i] ^= with[i];
  }

  // ── LRW (IEEE P1619 draft), T = K2 ⊗ I in GF(2^128), big-endian bit order ─

  /// <summary>K2·x^k and K2·(2^(t+1)−1) in GF(2^128) (x^128+x^7+x^2+x+1) or GF(2^64) (x^64+x^4+x^3+x+1), big-endian bit order.</summary>
  private static (byte[][] Powers, byte[][] Steps) LrwTables(ReadOnlySpan<byte> k2) {
    var n = k2.Length;
    var bits = n * 8;
    var reduce = n == 16 ? (byte)0x87 : (byte)0x1B;
    var powers = new byte[bits][];
    var x = k2.ToArray();
    for (var k = 0; k < bits; ++k) {
      powers[k] = (byte[])x.Clone();
      var carry = x[0] >> 7;
      for (var i = 0; i < n - 1; ++i) x[i] = (byte)((x[i] << 1) | (x[i + 1] >> 7));
      x[n - 1] = (byte)(x[n - 1] << 1);
      if (carry != 0) x[n - 1] ^= reduce;
    }
    var steps = new byte[bits][];
    var acc = new byte[n];
    for (var t = 0; t < bits; ++t) {
      for (var i = 0; i < n; ++i) acc[i] ^= powers[t][i];
      steps[t] = (byte[])acc.Clone();
    }
    return (powers, steps);
  }

  private void LrwTweak(ulong index, Span<byte> t) {
    t.Clear();
    for (var k = 0; k < 64; ++k)
      if ((index >> k & 1) != 0)
        for (var i = 0; i < t.Length; ++i) t[i] ^= this._lrwPowers[k][i];
  }

  private void LrwUnit(Span<byte> unit, ulong firstIndex, bool encrypt) {
    if (encrypt)
      for (var i = 0; i < this._ciphers.Length; ++i) this.Lrw(this._ciphers[i], unit, firstIndex, true);
    else
      for (var i = this._ciphers.Length - 1; i >= 0; --i) this.Lrw(this._ciphers[i], unit, firstIndex, false);
  }

  private void Lrw(TcBlockCipher cipher, Span<byte> unit, ulong index, bool encrypt) {
    var n = this._lrwBlock;
    Span<byte> tweaks = stackalloc byte[unit.Length];
    this.LrwTweak(index, tweaks[..n]);
    for (var i = n; i < unit.Length; i += n) {
      // T(I+1) = T(I) ⊕ K2·(I ⊕ (I+1)); I ⊕ (I+1) = 2^(t+1) − 1 for t trailing one bits of I.
      var step = this._lrwSteps[System.Numerics.BitOperations.TrailingZeroCount(~index)];
      for (var k = 0; k < n; ++k) tweaks[i + k] = (byte)(tweaks[i - n + k] ^ step[k]);
      ++index;
    }
    Xor(unit, tweaks);
    if (encrypt) cipher.EncryptBlocks(unit);
    else cipher.DecryptBlocks(unit);
    Xor(unit, tweaks);
  }

  // ── Legacy CBC with seeded IVs and sector whitening ──────────────────────

  /// <summary>
  /// TrueCrypt 4.x CBC: per cipher (or once for an outer-CBC cascade, which runs as one composite
  /// cipher), the IV is the seed XOR the sector number, and each block is whitened after the CBC
  /// step with 8 bytes derived from the seed bytes that follow the IV.
  /// </summary>
  private void CbcSector(Span<byte> sector, long number, bool encrypt) {
    Span<byte> white = stackalloc byte[8];
    Span<byte> iv = stackalloc byte[16];
    if (this._chain.Mode == TcMode.CbcOuter) {
      var bs = this._ciphers[0].BlockSize;
      var ivs = iv[..bs];
      SectorIv(this._ivArea, number, ivs);
      SectorWhitening(this._ivArea.AsSpan(bs, 16), number, white);
      if (!encrypt) Whiten8(sector, white);
      this.CbcComposite(sector, ivs, encrypt);
      if (encrypt) Whiten8(sector, white);
      return;
    }
    if (encrypt) {
      for (var j = 0; j < this._ciphers.Length; ++j) this.InnerLayer(j, sector, number, iv, white, encrypt: true);
    } else {
      for (var j = this._ciphers.Length - 1; j >= 0; --j) this.InnerLayer(j, sector, number, iv, white, encrypt: false);
    }
  }

  private void InnerLayer(int j, Span<byte> sector, long number, Span<byte> iv, Span<byte> white, bool encrypt) {
    var bs = this._ciphers[j].BlockSize;
    var ivs = iv[..bs];
    SectorIv(this._ivArea, number, ivs);
    SectorWhitening(this._ivArea.AsSpan(bs, 16), number, white);
    if (!encrypt) Whiten8(sector, white);
    Cbc(this._ciphers[j], sector, ivs, encrypt);
    if (encrypt) Whiten8(sector, white);
  }

  /// <summary>IV seed XOR the little-endian sector number, repeated over the IV.</summary>
  private static void SectorIv(ReadOnlySpan<byte> seed, long sector, Span<byte> iv) {
    Span<byte> s = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(s, sector);
    for (var i = 0; i < iv.Length; ++i) iv[i] = (byte)(seed[i] ^ s[i % 8]);
  }

  /// <summary>
  /// The 8-byte whitening of a sector: the 16-byte whitening key XOR the sector number (twice),
  /// each 32-bit quarter replaced by its CRC-32, then the halves folded together.
  /// </summary>
  internal static void SectorWhitening(ReadOnlySpan<byte> whitening, long sector, Span<byte> result) {
    Span<byte> buf = stackalloc byte[16];
    Span<byte> s = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(s, sector);
    for (var i = 0; i < 16; ++i) buf[i] = (byte)(whitening[i] ^ s[i % 8]);
    for (var i = 0; i < 4; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(buf[(i * 4)..], Crc32Raw(buf.Slice(i * 4, 4)));
    for (var i = 0; i < 4; ++i) {
      buf[i] ^= buf[12 + i];
      buf[4 + i] ^= buf[8 + i];
    }
    buf[..8].CopyTo(result);
  }

  /// <summary>Reflected CRC-32 (polynomial 0xEDB88320) with a zero seed and no final inversion.</summary>
  internal static uint Crc32Raw(ReadOnlySpan<byte> data) {
    var crc = 0u;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; ++k) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }
    return crc;
  }

  private static void Whiten8(Span<byte> data, ReadOnlySpan<byte> white) {
    for (var i = 0; i < data.Length; ++i) data[i] ^= white[i % 8];
  }

  /// <summary>CBC over the whole chain treated as one block cipher (all layers share the block size).</summary>
  private void CbcComposite(Span<byte> data, ReadOnlySpan<byte> iv, bool encrypt) {
    var bs = iv.Length;
    Span<byte> prev = stackalloc byte[bs];
    Span<byte> saved = stackalloc byte[bs];
    iv.CopyTo(prev);
    for (var i = 0; i < data.Length; i += bs) {
      var b = data.Slice(i, bs);
      if (encrypt) {
        for (var k = 0; k < bs; ++k) b[k] ^= prev[k];
        foreach (var c in this._ciphers) c.Encrypt(b);
        b.CopyTo(prev);
      } else {
        b.CopyTo(saved);
        for (var j = this._ciphers.Length - 1; j >= 0; --j) this._ciphers[j].Decrypt(b);
        for (var k = 0; k < bs; ++k) b[k] ^= prev[k];
        saved.CopyTo(prev);
      }
    }
  }

  private static void Cbc(TcBlockCipher cipher, Span<byte> data, ReadOnlySpan<byte> iv, bool encrypt) {
    var bs = cipher.BlockSize;
    Span<byte> prev = stackalloc byte[bs];
    Span<byte> saved = stackalloc byte[bs];
    iv.CopyTo(prev);
    for (var i = 0; i < data.Length; i += bs) {
      var b = data.Slice(i, bs);
      if (encrypt) {
        for (var k = 0; k < bs; ++k) b[k] ^= prev[k];
        cipher.Encrypt(b);
        b.CopyTo(prev);
      } else {
        b.CopyTo(saved);
        cipher.Decrypt(b);
        for (var k = 0; k < bs; ++k) b[k] ^= prev[k];
        saved.CopyTo(prev);
      }
    }
  }

  public void Dispose() {
    foreach (var c in this._ciphers) c?.Dispose();
    foreach (var c in this._tweaks) c?.Dispose();
    CryptographicOperations.ZeroMemory(this._ivArea);
    CryptographicOperations.ZeroMemory(this._headerWhitening);
  }
}
