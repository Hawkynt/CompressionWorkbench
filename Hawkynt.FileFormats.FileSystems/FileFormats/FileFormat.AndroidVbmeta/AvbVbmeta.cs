#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.AndroidVbmeta;

/// <summary>One AVB descriptor, kept as its exact encoded bytes (16-byte tag/length header, body, padding).</summary>
public sealed record AvbDescriptor(ulong Tag, byte[] Encoded) {
  public const ulong Property = 0, Hashtree = 1, Hash = 2, KernelCmdline = 3, ChainPartition = 4;

  public string Kind => this.Tag switch {
    Property => "property", Hashtree => "hashtree", Hash => "hash", KernelCmdline => "kernel_cmdline",
    ChainPartition => "chain_partition", _ => "tag" + this.Tag.ToString(CultureInfo.InvariantCulture),
  };

  public ReadOnlySpan<byte> Body => this.Encoded.AsSpan(16);
}

/// <summary>
/// An Android Verified Boot 2.0 <c>vbmeta</c> image (magic <c>AVB0</c>): the 256-byte header, the
/// authentication block (hash, signature) and the auxiliary block (descriptors, public key, public
/// key metadata), parsed and verified, and written back the way <c>avbtool make_vbmeta_image</c>
/// lays one out.
/// </summary>
/// <remarks>
/// Structures follow AOSP <c>platform/external/avb</c> — <c>libavb/avb_vbmeta_image.h</c>,
/// <c>avb_descriptor.h</c> and <c>avbtool.py</c> (MIT / Apache-2.0), converted. All integers are
/// big-endian. The hash covers the header and the auxiliary block; the signature is RSA PKCS#1 v1.5
/// over the same bytes, with the public key in AVB's own encoding (bit count, n0inv, modulus, R²).
/// </remarks>
public sealed class AvbVbmeta {

  public const int HeaderSize = 256;
  public const int ReleaseStringSize = 48;
  public const int ReservedSize = 80;

  public uint VersionMajor { get; set; } = 1;
  public uint VersionMinor { get; set; }
  public uint AlgorithmType { get; set; }
  public ulong RollbackIndex { get; set; }
  public uint Flags { get; set; }
  public uint RollbackIndexLocation { get; set; }
  /// <summary>The 48-byte release-string field, raw (avbtool writes 47 bytes plus a NUL).</summary>
  public byte[] ReleaseString { get; set; } = new byte[ReleaseStringSize];
  public byte[] Reserved { get; set; } = new byte[ReservedSize];

  public List<AvbDescriptor> Descriptors { get; set; } = [];
  public byte[] PublicKey { get; set; } = [];
  public byte[] PublicKeyMetadata { get; set; } = [];
  public byte[] Hash { get; set; } = [];
  public byte[] Signature { get; set; } = [];
  /// <summary>Bytes after the vbmeta blob (partition padding).</summary>
  public byte[] Trailer { get; set; } = [];

  /// <summary>True when the image is laid out as avbtool lays it out, so a rewrite reproduces it.</summary>
  public bool IsCanonical { get; private set; } = true;
  public string? NonCanonicalReason { get; private set; }
  /// <summary>Null when the algorithm carries no hash.</summary>
  public bool? HashValid { get; private set; }
  /// <summary>Null when the algorithm carries no signature this reader can check.</summary>
  public bool? SignatureValid { get; private set; }

  public static string AlgorithmName(uint type) => type switch {
    0 => "NONE", 1 => "SHA256_RSA2048", 2 => "SHA256_RSA4096", 3 => "SHA256_RSA8192",
    4 => "SHA512_RSA2048", 5 => "SHA512_RSA4096", 6 => "SHA512_RSA8192", 7 => "MLDSA65", 8 => "MLDSA87",
    _ => "UNKNOWN",
  };

  // ── parse ────────────────────────────────────────────────────────────

  private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d[o..]);
  private static ulong U64(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt64BigEndian(d[o..]);

  public static AvbVbmeta Parse(ReadOnlySpan<byte> d) {
    if (d.Length < HeaderSize) throw new InvalidDataException($"vbmeta: {d.Length} bytes is shorter than the {HeaderSize}-byte header.");
    if (!d[..4].SequenceEqual("AVB0"u8)) throw new InvalidDataException("vbmeta: no AVB0 magic.");
    var v = new AvbVbmeta {
      VersionMajor = U32(d, 4),
      VersionMinor = U32(d, 8),
      AlgorithmType = U32(d, 28),
      RollbackIndex = U64(d, 112),
      Flags = U32(d, 120),
      RollbackIndexLocation = U32(d, 124),
      ReleaseString = d.Slice(128, ReleaseStringSize).ToArray(),
      Reserved = d.Slice(176, ReservedSize).ToArray(),
    };
    var authSize = U64(d, 12);
    var auxSize = U64(d, 20);
    if (authSize > int.MaxValue || auxSize > int.MaxValue || HeaderSize + (long)authSize + (long)auxSize > d.Length)
      throw new InvalidDataException($"vbmeta: blocks of {authSize} + {auxSize} bytes run past the end of the {d.Length}-byte image.");
    var auth = d.Slice(HeaderSize, (int)authSize);
    var aux = d.Slice(HeaderSize + (int)authSize, (int)auxSize);

    v.Hash = Slice(auth, U64(d, 32), U64(d, 40), "hash");
    v.Signature = Slice(auth, U64(d, 48), U64(d, 56), "signature");
    v.PublicKey = Slice(aux, U64(d, 64), U64(d, 72), "public key");
    v.PublicKeyMetadata = Slice(aux, U64(d, 80), U64(d, 88), "public key metadata");
    var descOffset = U64(d, 96);
    var descSize = U64(d, 104);
    var descriptors = Slice(aux, descOffset, descSize, "descriptors");
    for (var o = 0; o < descriptors.Length;) {
      if (descriptors.Length - o < 16) throw new InvalidDataException("vbmeta: a descriptor header is cut short.");
      var tag = U64(descriptors, o);
      var following = U64(descriptors, o + 8);
      if (following > (ulong)(descriptors.Length - o - 16) || following % 8 != 0)
        throw new InvalidDataException($"vbmeta: descriptor at offset {o} declares {following} bytes that do not fit.");
      v.Descriptors.Add(new AvbDescriptor(tag, descriptors.AsSpan(o, 16 + (int)following).ToArray()));
      o += 16 + (int)following;
    }
    v.Trailer = d[(HeaderSize + (int)authSize + (int)auxSize)..].ToArray();

    // avbtool's layout: hash then signature; descriptors, key, key metadata; both blocks padded to 64 with zeros.
    var keyLen = v.PublicKey.Length;
    if (U64(d, 32) != 0 || U64(d, 48) != (ulong)v.Hash.Length
        || descOffset != 0 || U64(d, 64) != descSize || U64(d, 80) != descSize + (ulong)keyLen
        || authSize != Round64((ulong)(v.Hash.Length + v.Signature.Length))
        || auxSize != Round64(descSize + (ulong)keyLen + (ulong)v.PublicKeyMetadata.Length)
        || auth[(v.Hash.Length + v.Signature.Length)..].ContainsAnyExcept((byte)0)
        || aux[(int)(descSize + (ulong)keyLen + (ulong)v.PublicKeyMetadata.Length)..].ContainsAnyExcept((byte)0))
      v.MarkNonCanonical("the blocks are not laid out as avbtool lays them out");
    v.Verify(d[..HeaderSize], aux);
    return v;
  }

  private static byte[] Slice(ReadOnlySpan<byte> block, ulong offset, ulong size, string what) {
    if (offset > (ulong)block.Length || size > (ulong)block.Length - offset)
      throw new InvalidDataException($"vbmeta: the {what} ({size} bytes at {offset}) runs past its {block.Length}-byte block.");
    return block.Slice((int)offset, (int)size).ToArray();
  }

  private static ulong Round64(ulong n) => (n + 63) / 64 * 64;

  private void MarkNonCanonical(string reason) {
    this.IsCanonical = false;
    this.NonCanonicalReason ??= reason;
  }

  // ── verify ───────────────────────────────────────────────────────────

  private static HashAlgorithmName? HashOf(uint type) => type switch {
    1 or 2 or 3 => HashAlgorithmName.SHA256,
    4 or 5 or 6 => HashAlgorithmName.SHA512,
    _ => null,
  };

  private void Verify(ReadOnlySpan<byte> header, ReadOnlySpan<byte> aux) {
    if (HashOf(this.AlgorithmType) is not { } hashName) return;
    var message = new byte[header.Length + aux.Length];
    header.CopyTo(message);
    aux.CopyTo(message.AsSpan(header.Length));
    var digest = hashName == HashAlgorithmName.SHA256 ? SHA256.HashData(message) : SHA512.HashData(message);
    this.HashValid = digest.AsSpan().SequenceEqual(this.Hash);
    if (TryRsaKey(this.PublicKey) is not { } parameters) {
      this.SignatureValid = false;
      return;
    }
    try {
      using var rsa = RSA.Create(parameters);
      this.SignatureValid = this.HashValid == true && rsa.VerifyData(message, this.Signature, hashName, RSASignaturePadding.Pkcs1);
    } catch (CryptographicException) {
      this.SignatureValid = false;
    }
  }

  /// <summary>AVB's public key encoding: big-endian bit count and n0inv, then the modulus and R², each bit-count/8 bytes. Exponent 65537.</summary>
  public static RSAParameters? TryRsaKey(ReadOnlySpan<byte> key) {
    if (key.Length < 8) return null;
    var bits = BinaryPrimitives.ReadUInt32BigEndian(key);
    if (bits is not (2048 or 4096 or 8192) || key.Length != 8 + 2 * (bits / 8)) return null;
    return new RSAParameters { Modulus = key.Slice(8, (int)bits / 8).ToArray(), Exponent = [1, 0, 1] };
  }

  // ── write ────────────────────────────────────────────────────────────

  /// <summary>
  /// Writes the vbmeta blob as avbtool's <c>_generate_vbmeta_blob</c> does, then the trailer. Only
  /// the unsigned <c>NONE</c> algorithm can be written: anything else needs the private key.
  /// </summary>
  public void Write(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (this.AlgorithmType != 0 || this.Hash.Length > 0 || this.Signature.Length > 0 || this.PublicKey.Length > 0)
      throw new NotSupportedException("vbmeta: only the unsigned NONE algorithm can be written; signing needs the private key.");
    var descriptors = new MemoryStream();
    foreach (var desc in this.Descriptors) {
      Validate(desc);
      descriptors.Write(desc.Encoded);
    }
    var descSize = (ulong)descriptors.Length;
    var auxSize = Round64(descSize + (ulong)this.PublicKeyMetadata.Length);

    var h = new byte[HeaderSize];
    "AVB0"u8.CopyTo(h);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), this.VersionMajor);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), this.VersionMinor);
    BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(12), 0); // authentication block: no hash, no signature
    BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(20), auxSize);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(28), 0);
    // hash 32/40 and signature 48/56 stay zero
    BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(64), descSize); // public key offset (size 0)
    BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(80), descSize); // public key metadata offset
    BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(88), (ulong)this.PublicKeyMetadata.Length);
    BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(96), 0);
    BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(104), descSize);
    BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(112), this.RollbackIndex);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(120), this.Flags);
    BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(124), this.RollbackIndexLocation);
    this.ReleaseString.AsSpan(0, Math.Min(ReleaseStringSize, this.ReleaseString.Length)).CopyTo(h.AsSpan(128));
    h[128 + ReleaseStringSize - 1] = 0;
    this.Reserved.AsSpan(0, Math.Min(ReservedSize, this.Reserved.Length)).CopyTo(h.AsSpan(176));

    output.Write(h);
    descriptors.Position = 0;
    descriptors.CopyTo(output);
    output.Write(this.PublicKeyMetadata);
    output.Write(new byte[auxSize - descSize - (ulong)this.PublicKeyMetadata.Length]);
    output.Write(this.Trailer);
  }

  /// <summary>Checks an encoded descriptor's own length field, so a malformed blob is refused rather than written.</summary>
  public static void Validate(AvbDescriptor desc) {
    var e = desc.Encoded;
    if (e.Length < 16 || (e.Length - 16) % 8 != 0
        || BinaryPrimitives.ReadUInt64BigEndian(e.AsSpan(8)) != (ulong)(e.Length - 16)
        || BinaryPrimitives.ReadUInt64BigEndian(e) != desc.Tag)
      throw new ArgumentException($"vbmeta: a {desc.Kind} descriptor of {e.Length} bytes is not a well-formed AVB descriptor (tag, length, 8-byte padded body).");
  }

  public static AvbDescriptor FromEncoded(byte[] encoded) {
    if (encoded.Length < 16) throw new ArgumentException("vbmeta: a descriptor is at least 16 bytes.");
    var desc = new AvbDescriptor(BinaryPrimitives.ReadUInt64BigEndian(encoded), encoded);
    Validate(desc);
    return desc;
  }

  public static string ReleaseText(byte[] field) {
    var end = Array.IndexOf(field, (byte)0);
    return Encoding.UTF8.GetString(field, 0, end < 0 ? field.Length : end);
  }
}
