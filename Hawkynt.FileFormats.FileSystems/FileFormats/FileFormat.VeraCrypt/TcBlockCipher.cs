using System.Buffers.Binary;
using Compression.Core.Crypto;

namespace FileFormat.VeraCrypt;

/// <summary>One keyed block cipher as the TrueCrypt/VeraCrypt modes use it: raw single-block (ECB) transforms.</summary>
public abstract class TcBlockCipher : IDisposable {
  /// <summary>Block size in bytes (16, or 8 for the legacy 64-bit ciphers).</summary>
  public abstract int BlockSize { get; }

  /// <summary>Encrypts one block in place.</summary>
  public abstract void Encrypt(Span<byte> block);

  /// <summary>Decrypts one block in place.</summary>
  public abstract void Decrypt(Span<byte> block);

  /// <summary>Encrypts whole blocks in place, each on its own (ECB).</summary>
  public virtual void EncryptBlocks(Span<byte> blocks) {
    for (var i = 0; i < blocks.Length; i += this.BlockSize) this.Encrypt(blocks.Slice(i, this.BlockSize));
  }

  /// <summary>Decrypts whole blocks in place, each on its own (ECB).</summary>
  public virtual void DecryptBlocks(Span<byte> blocks) {
    for (var i = 0; i < blocks.Length; i += this.BlockSize) this.Decrypt(blocks.Slice(i, this.BlockSize));
  }

  public virtual void Dispose() => GC.SuppressFinalize(this);

  /// <summary>Key size in bytes the TrueCrypt/VeraCrypt tables give <paramref name="name"/> for one cipher key.</summary>
  public static TcBlockCipher Create(string name, ReadOnlySpan<byte> key) => name switch {
    "aes" => new AesCipher(key),
    "serpent" => new SerpentCipher(key),
    "twofish" => new TwofishCipher(key),
    "camellia" => new CamelliaCipher(key),
    "kuznyechik" => new KuznyechikCipher(key),
    "cast5" => new Cast5Cipher(key),
    "blowfish_le" => new BlowfishLe(key),
    "des3_ede" => new TripleDesCipher(key),
    _ => throw new NotSupportedException($"TrueCrypt/VeraCrypt cipher '{name}' is not supported."),
  };

  /// <summary>AES on the library's own cipher; whole runs of blocks go through it at once.</summary>
  private sealed class AesCipher(ReadOnlySpan<byte> key) : TcBlockCipher {
    private readonly AesBlockCipher _c = new(key);
    public override int BlockSize => AesBlockCipher.BlockSize;
    public override void Encrypt(Span<byte> block) => this._c.Encrypt(block[..AesBlockCipher.BlockSize]);
    public override void Decrypt(Span<byte> block) => this._c.Decrypt(block[..AesBlockCipher.BlockSize]);
    public override void EncryptBlocks(Span<byte> blocks) => this._c.Encrypt(blocks);
    public override void DecryptBlocks(Span<byte> blocks) => this._c.Decrypt(blocks);
  }

  private sealed class TripleDesCipher(ReadOnlySpan<byte> key) : TcBlockCipher {
    private readonly TripleDes _c = new(key);
    public override int BlockSize => TripleDes.BlockSize;
    public override void Encrypt(Span<byte> block) => this._c.Encrypt(block);
    public override void Decrypt(Span<byte> block) => this._c.Decrypt(block);
  }

  /// <summary>TrueCrypt's Blowfish reads its two 32-bit halves little-endian; the cipher itself is big-endian.</summary>
  private sealed class BlowfishLe(ReadOnlySpan<byte> key) : TcBlockCipher {
    private readonly Blowfish _bf = new(key);
    public override int BlockSize => 8;
    public override void Encrypt(Span<byte> block) {
      Swap(block);
      this._bf.Encrypt(block);
      Swap(block);
    }
    public override void Decrypt(Span<byte> block) {
      Swap(block);
      this._bf.Decrypt(block);
      Swap(block);
    }
    private static void Swap(Span<byte> b) {
      BinaryPrimitives.WriteUInt32BigEndian(b, BinaryPrimitives.ReadUInt32LittleEndian(b));
      BinaryPrimitives.WriteUInt32BigEndian(b[4..], BinaryPrimitives.ReadUInt32LittleEndian(b[4..]));
    }
  }

  private sealed class SerpentCipher(ReadOnlySpan<byte> key) : TcBlockCipher {
    private readonly Serpent _c = new(key);
    public override int BlockSize => Serpent.BlockSize;
    public override void Encrypt(Span<byte> block) => this._c.Encrypt(block);
    public override void Decrypt(Span<byte> block) => this._c.Decrypt(block);
  }

  private sealed class TwofishCipher(ReadOnlySpan<byte> key) : TcBlockCipher {
    private readonly Twofish _c = new(key);
    public override int BlockSize => Twofish.BlockSize;
    public override void Encrypt(Span<byte> block) => this._c.Encrypt(block);
    public override void Decrypt(Span<byte> block) => this._c.Decrypt(block);
  }

  private sealed class CamelliaCipher(ReadOnlySpan<byte> key) : TcBlockCipher {
    private readonly Camellia _c = new(key);
    public override int BlockSize => Camellia.BlockSize;
    public override void Encrypt(Span<byte> block) => this._c.Encrypt(block);
    public override void Decrypt(Span<byte> block) => this._c.Decrypt(block);
  }

  private sealed class KuznyechikCipher(ReadOnlySpan<byte> key) : TcBlockCipher {
    private readonly Kuznyechik _c = new(key);
    public override int BlockSize => Kuznyechik.BlockSize;
    public override void Encrypt(Span<byte> block) => this._c.Encrypt(block);
    public override void Decrypt(Span<byte> block) => this._c.Decrypt(block);
  }

  private sealed class Cast5Cipher(ReadOnlySpan<byte> key) : TcBlockCipher {
    private readonly Cast5 _c = new(key);
    public override int BlockSize => Cast5.BlockSize;
    public override void Encrypt(Span<byte> block) => this._c.Encrypt(block);
    public override void Decrypt(Span<byte> block) => this._c.Decrypt(block);
  }
}
