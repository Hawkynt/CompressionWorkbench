namespace FileFormat.VeraCrypt;

/// <summary>How a chain encrypts sectors.</summary>
public enum TcMode {
  /// <summary>XTS, 512-byte data units (TrueCrypt 5+, VeraCrypt).</summary>
  Xts,
  /// <summary>LRW with a shared 16-byte tweak key (TrueCrypt 4.1–4.3).</summary>
  Lrw,
  /// <summary>CBC with a seeded IV and sector whitening; cascades run as one composite cipher ("outer CBC").</summary>
  CbcOuter,
  /// <summary>CBC with a seeded IV and sector whitening; each cascade layer runs its own CBC ("inner CBC").</summary>
  CbcInner,
}

/// <summary>One cipher of a chain and where its key material sits in the 256-byte key area.</summary>
/// <param name="Name">Cipher name (<c>aes</c>, <c>serpent</c>, …).</param>
/// <param name="KeySize">Key bytes the mode takes for this cipher (XTS: both halves; LRW: cipher key + 16-byte tweak key).</param>
/// <param name="BlockSize">Cipher block size.</param>
/// <param name="KeyOffset">Offset of the cipher key.</param>
/// <param name="IvOffset">XTS: offset of the tweak key; CBC: offset of the IV seed.</param>
/// <param name="KeyExtraSize">CBC: IV seed plus whitening bytes that follow the key.</param>
public sealed record TcCipher(string Name, int KeySize, int BlockSize, int KeyOffset, int IvOffset, int KeyExtraSize);

/// <summary>
/// A TrueCrypt/VeraCrypt cipher chain. The table is cryptsetup's (<c>lib/tcrypt/tcrypt.c</c>,
/// LGPL-2.1-or-later, see THIRD-PARTY-NOTICE.cryptsetup.txt): <see cref="Ciphers"/>[0] encrypts
/// first and decrypts last, and every layer uses the same sector numbering.
/// </summary>
/// <param name="Name">cryptsetup's long name, ciphers in encryption order (<c>serpent-twofish-aes</c>).</param>
/// <param name="Mode">Sector mode.</param>
/// <param name="Legacy">Only TrueCrypt before 5.0 wrote it.</param>
/// <param name="KeySize">Total key bytes the chain consumes.</param>
/// <param name="Ciphers">The ciphers, in encryption order.</param>
public sealed record TcCipherChain(string Name, TcMode Mode, bool Legacy, int KeySize, IReadOnlyList<TcCipher> Ciphers) {
  /// <summary>VeraCrypt's display name: the cipher applied last, outermost (<c>AES(Twofish(Serpent))</c> is <c>serpent-twofish-aes</c>).</summary>
  public string DisplayName => string.Join("-", this.Ciphers.Reverse().Select(static c => c.Name switch {
    "aes" => "AES", "serpent" => "Serpent", "twofish" => "Twofish", "camellia" => "Camellia", "kuznyechik" => "Kuznyechik",
    "cast5" => "CAST5", "blowfish_le" => "Blowfish", "des3_ede" => "Triple DES", var n => n,
  }));

  private static TcCipher C(string name, int keySize, int blockSize, int keyOffset, int ivOffset, int keyExtraSize)
    => new(name, keySize, blockSize, keyOffset, ivOffset, keyExtraSize);

  private static TcCipherChain Xts(string name, params TcCipher[] c) => new(name, TcMode.Xts, false, 64 * c.Length, c);
  private static TcCipherChain Lrw(string name, params TcCipher[] c) => new(name, TcMode.Lrw, false, 48 * c.Length, c);

  /// <summary>Every chain, in the order cryptsetup tries them.</summary>
  public static IReadOnlyList<TcCipherChain> All { get; } = [
    Xts("aes", C("aes", 64, 16, 0, 32, 0)),
    Xts("serpent", C("serpent", 64, 16, 0, 32, 0)),
    Xts("twofish", C("twofish", 64, 16, 0, 32, 0)),
    Xts("twofish-aes", C("twofish", 64, 16, 0, 64, 0), C("aes", 64, 16, 32, 96, 0)),
    Xts("serpent-twofish-aes", C("serpent", 64, 16, 0, 96, 0), C("twofish", 64, 16, 32, 128, 0), C("aes", 64, 16, 64, 160, 0)),
    Xts("aes-serpent", C("aes", 64, 16, 0, 64, 0), C("serpent", 64, 16, 32, 96, 0)),
    Xts("aes-twofish-serpent", C("aes", 64, 16, 0, 96, 0), C("twofish", 64, 16, 32, 128, 0), C("serpent", 64, 16, 64, 160, 0)),
    Xts("serpent-twofish", C("serpent", 64, 16, 0, 64, 0), C("twofish", 64, 16, 32, 96, 0)),
    Xts("camellia", C("camellia", 64, 16, 0, 32, 0)),
    Xts("kuznyechik", C("kuznyechik", 64, 16, 0, 32, 0)),
    Xts("kuznyechik-camellia", C("kuznyechik", 64, 16, 0, 64, 0), C("camellia", 64, 16, 32, 96, 0)),
    Xts("twofish-kuznyechik", C("twofish", 64, 16, 0, 64, 0), C("kuznyechik", 64, 16, 32, 96, 0)),
    Xts("serpent-camellia", C("serpent", 64, 16, 0, 64, 0), C("camellia", 64, 16, 32, 96, 0)),
    Xts("aes-kuznyechik", C("aes", 64, 16, 0, 64, 0), C("kuznyechik", 64, 16, 32, 96, 0)),
    Xts("camellia-serpent-kuznyechik", C("camellia", 64, 16, 0, 96, 0), C("serpent", 64, 16, 32, 128, 0), C("kuznyechik", 64, 16, 64, 160, 0)),

    Lrw("aes", C("aes", 48, 16, 32, 0, 0)),
    Lrw("serpent", C("serpent", 48, 16, 32, 0, 0)),
    Lrw("twofish", C("twofish", 48, 16, 32, 0, 0)),
    Lrw("twofish-aes", C("twofish", 48, 16, 32, 0, 0), C("aes", 48, 16, 64, 0, 0)),
    Lrw("serpent-twofish-aes", C("serpent", 48, 16, 32, 0, 0), C("twofish", 48, 16, 64, 0, 0), C("aes", 48, 16, 96, 0, 0)),
    Lrw("aes-serpent", C("aes", 48, 16, 32, 0, 0), C("serpent", 48, 16, 64, 0, 0)),
    Lrw("aes-twofish-serpent", C("aes", 48, 16, 32, 0, 0), C("twofish", 48, 16, 64, 0, 0), C("serpent", 48, 16, 96, 0, 0)),
    Lrw("serpent-twofish", C("serpent", 48, 16, 32, 0, 0), C("twofish", 48, 16, 64, 0, 0)),

    // TrueCrypt 4.1–4.3 also offered LRW over GF(2^64) for its 64-bit ciphers (not in cryptsetup's table).
    new("blowfish_le", TcMode.Lrw, true, 72, [new("blowfish_le", 72, 8, 32, 0, 0)]),
    new("cast5", TcMode.Lrw, true, 32, [new("cast5", 32, 8, 32, 0, 0)]),
    new("des3_ede", TcMode.Lrw, true, 40, [new("des3_ede", 40, 8, 32, 0, 0)]),

    new("aes", TcMode.CbcOuter, true, 64, [new("aes", 32, 16, 32, 0, 32)]),
    new("serpent", TcMode.CbcOuter, true, 64, [new("serpent", 32, 16, 32, 0, 32)]),
    new("twofish", TcMode.CbcOuter, true, 64, [new("twofish", 32, 16, 32, 0, 32)]),
    new("twofish-aes", TcMode.CbcOuter, true, 96, [new("twofish", 32, 16, 32, 0, 0), new("aes", 32, 16, 64, 0, 32)]),
    new("serpent-twofish-aes", TcMode.CbcOuter, true, 128, [new("serpent", 32, 16, 32, 0, 0), new("twofish", 32, 16, 64, 0, 0), new("aes", 32, 16, 96, 0, 32)]),
    new("aes-serpent", TcMode.CbcOuter, true, 96, [new("aes", 32, 16, 32, 0, 0), new("serpent", 32, 16, 64, 0, 32)]),
    new("aes-twofish-serpent", TcMode.CbcOuter, true, 128, [new("aes", 32, 16, 32, 0, 0), new("twofish", 32, 16, 64, 0, 0), new("serpent", 32, 16, 96, 0, 32)]),
    new("serpent-twofish", TcMode.CbcOuter, true, 96, [new("serpent", 32, 16, 32, 0, 0), new("twofish", 32, 16, 64, 0, 32)]),
    new("cast5", TcMode.CbcOuter, true, 40, [new("cast5", 16, 8, 32, 0, 24)]),
    new("des3_ede", TcMode.CbcOuter, true, 48, [new("des3_ede", 24, 8, 32, 0, 24)]),
    new("blowfish_le", TcMode.CbcOuter, true, 80, [new("blowfish_le", 56, 8, 32, 0, 24)]),
    new("blowfish_le-aes", TcMode.CbcInner, true, 120, [new("blowfish_le", 56, 8, 32, 0, 0), new("aes", 32, 16, 88, 0, 32)]),
    new("serpent-blowfish_le-aes", TcMode.CbcInner, true, 152, [new("serpent", 32, 16, 32, 0, 0), new("blowfish_le", 56, 8, 64, 0, 0), new("aes", 32, 16, 120, 0, 32)]),
  ];

  /// <summary>Looks a chain up by mode and cryptsetup name.</summary>
  public static TcCipherChain Find(TcMode mode, string name)
    => All.FirstOrDefault(c => c.Mode == mode && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
      ?? throw new NotSupportedException($"TrueCrypt/VeraCrypt: no {mode} cipher chain '{name}'.");
}
