using System.Security.Cryptography;
using System.Text;

namespace FileFormat.VeraCrypt;

/// <summary>What a new TrueCrypt/VeraCrypt container is made of.</summary>
public sealed record TcCreateOptions {
  /// <summary>Write a VeraCrypt (<c>VERA</c>) volume; false writes the TrueCrypt 7.1 format (<c>TRUE</c>).</summary>
  public bool VeraCrypt { get; init; } = true;
  /// <summary>Cipher chain, by cryptsetup name (<c>aes</c>, <c>serpent-twofish-aes</c>) or VeraCrypt name (<c>AES-Twofish-Serpent</c>); always XTS.</summary>
  public string Cipher { get; init; } = "aes";
  /// <summary>Header-key PRF: <c>sha512</c>, <c>sha256</c>, <c>whirlpool</c>, <c>blake2s-256</c>, <c>stribog512</c>, <c>ripemd160</c> or <c>argon2id</c>.</summary>
  public string Prf { get; init; } = "sha512";
  /// <summary>VeraCrypt PIM; 0 for the default iterations.</summary>
  public int Pim { get; init; }
  /// <summary>Data-area size in bytes; 0 for the payload's size. Must be whole 512-byte sectors.</summary>
  public long VolumeSize { get; init; }
  /// <summary>
  /// Leave the unused part of the data area unwritten (a sparse file where the stream supports
  /// holes) instead of filling it with random bytes. Like a VeraCrypt dynamic volume, it then
  /// shows which parts are in use.
  /// </summary>
  public bool Sparse { get; init; }
  /// <summary>Keyfile contents mixed into the password.</summary>
  public IReadOnlyList<byte[]> Keyfiles { get; init; } = [];

  /// <summary>The XTS chain <see cref="Cipher"/> names.</summary>
  public TcCipherChain ResolveChain() {
    var name = this.Cipher.Trim();
    foreach (var chain in TcCipherChain.All.Where(static c => c.Mode == TcMode.Xts))
      if (chain.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || chain.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
        return chain;
    throw new NotSupportedException($"TrueCrypt/VeraCrypt: no XTS cipher or cascade named '{this.Cipher}'.");
  }

  /// <summary>The derivation <see cref="Prf"/> names for this format.</summary>
  public TcKdf ResolveKdf() {
    var prf = this.Prf.Trim().ToLowerInvariant() switch {
      "sha-512" => "sha512", "sha-256" => "sha256", "blake2s" => "blake2s-256", "streebog" => "stribog512", var p => p,
    };
    return TcKdf.All.FirstOrDefault(k => k.VeraCrypt == this.VeraCrypt && k.Prf == prf && !k.Legacy
             && (this.VeraCrypt || k.Iterations == (prf == "ripemd160" ? 2000 : 1000)))
      ?? throw new NotSupportedException($"{(this.VeraCrypt ? "VeraCrypt" : "TrueCrypt")} volumes cannot be created with the PRF '{this.Prf}'.");
  }
}

/// <summary>
/// Writes TrueCrypt/VeraCrypt containers in the layout both tools create: a 128 KiB header area
/// (standard header at 0, hidden-volume header slot at 64 KiB), the data area, and a 128 KiB
/// backup area (backup header, backup hidden-volume header slot at 64 KiB before the end).
/// Unused header slots hold random bytes, so a container never shows whether it holds a hidden
/// volume.
/// </summary>
public static class TcVolumeWriter {
  /// <summary>Writes a container whose data area starts with <paramref name="payload"/>.</summary>
  public static void Write(Stream output, ReadOnlySpan<byte> payload, string password, TcCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(password);
    ArgumentNullException.ThrowIfNull(options);
    if (!options.VeraCrypt && options.Pim != 0)
      throw new ArgumentException("TrueCrypt volumes have no PIM.", nameof(options));
    var volumeSize = options.VolumeSize == 0 ? payload.Length : options.VolumeSize;
    if (payload.Length % 512 != 0 || volumeSize % 512 != 0)
      throw new ArgumentException("TrueCrypt/VeraCrypt volumes hold whole 512-byte sectors.", nameof(payload));
    if (payload.Length > volumeSize)
      throw new ArgumentException($"The payload ({payload.Length} bytes) is larger than the volume ({volumeSize} bytes).", nameof(options));
    if (options.Sparse && !output.CanSeek)
      throw new ArgumentException("A sparse volume needs a seekable output.", nameof(output));
    var chain = options.ResolveChain();
    var kdf = options.ResolveKdf();
    var pw = Encoding.UTF8.GetBytes(password);
    var keys = RandomNumberGenerator.GetBytes(256);
    try {
      var header = TcVolume.BuildHeader(options.VeraCrypt, kdf, options.Pim, chain, pw, options.Keyfiles, keys,
        (ulong)volumeSize, 0, TcVolume.DataAreaOffset, (ulong)volumeSize);
      output.Write(header);
      WriteRandom(output, TcVolume.DataAreaOffset - header.Length);   // incl. the hidden-header slot

      using (var cipher = new TcSectorCipher(chain, keys, TcVolume.DataAreaOffset / 512)) {
        var buffer = new byte[1 << 20];
        for (var done = 0; done < payload.Length;) {
          var n = Math.Min(buffer.Length, payload.Length - done);
          payload.Slice(done, n).CopyTo(buffer);
          cipher.Encrypt(buffer.AsSpan(0, n), done / 512);
          output.Write(buffer, 0, n);
          done += n;
        }
        CryptographicOperations.ZeroMemory(buffer);
      }
      var unused = volumeSize - payload.Length;
      if (options.Sparse) output.Seek(unused, SeekOrigin.Current);
      else WriteRandom(output, unused);

      var backup = TcVolume.BuildHeader(options.VeraCrypt, kdf, options.Pim, chain, pw, options.Keyfiles, keys,
        (ulong)volumeSize, 0, TcVolume.DataAreaOffset, (ulong)volumeSize);
      output.Write(backup);
      WriteRandom(output, TcVolume.BackupHeaderFromEnd - backup.Length); // incl. the backup hidden-header slot
      if (options.Sparse && output.CanSeek && output.Length < output.Position) output.SetLength(output.Position);
    } finally {
      CryptographicOperations.ZeroMemory(keys);
      CryptographicOperations.ZeroMemory(pw);
    }
  }

  private static void WriteRandom(Stream output, long count) {
    var buffer = new byte[64 * 1024];
    while (count > 0) {
      var n = (int)Math.Min(buffer.Length, count);
      RandomNumberGenerator.Fill(buffer.AsSpan(0, n));
      output.Write(buffer, 0, n);
      count -= n;
    }
  }
}
