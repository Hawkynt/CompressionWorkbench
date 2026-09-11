#pragma warning disable CS1591
using System.Security.Cryptography;

namespace FileSystem.Ecryptfs;

/// <summary>
/// Reader for an eCryptfs lower file. eCryptfs is a stacked filesystem, so one
/// lower file represents one encrypted upper file rather than a complete volume.
/// </summary>
public sealed class EcryptfsReader : IDisposable {
  private readonly Stream _stream;
  private readonly EcryptfsCodec.Header _header;
  private readonly List<EcryptfsEntry> _entries = [];

  public EcryptfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._header = EcryptfsCodec.ReadHeader(stream);

    if (this._header.DecryptedSize > 0) {
      if (this._header.DecryptedSize > long.MaxValue)
        throw new InvalidDataException("eCryptfs plaintext size exceeds the supported entry-size range.");
      this._entries.Add(new EcryptfsEntry {
        Name = "content.bin",
        Size = (long)this._header.DecryptedSize,
        IsDirectory = false,
      });
    }
  }

  /// <summary>The validated eCryptfs marker relation constant.</summary>
  public uint Marker => EcryptfsCodec.MarkerMagic;

  /// <summary>The random first word of the two-word marker pair stored on disk.</summary>
  public uint MarkerWord => this._header.MarkerWord;

  /// <summary>Plaintext file length stored in the lower-file header.</summary>
  public ulong DecryptedSize => this._header.DecryptedSize;

  /// <summary>Raw eCryptfs file flags, including the version in the top byte.</summary>
  public uint Flags => this._header.Flags;

  /// <summary>eCryptfs lower-file format version.</summary>
  public byte FileVersion => this._header.FileVersion;

  /// <summary>Encryption extent size in bytes.</summary>
  public uint ExtentSize => this._header.ExtentSize;

  /// <summary>Number of header extents preceding ciphertext data.</summary>
  public ushort HeaderExtentCount => this._header.HeaderExtentCount;

  /// <summary>Total metadata region at the beginning of the lower file.</summary>
  public int MetadataSize => this._header.MetadataSize;

  /// <summary>Canonical lower-file length implied by the plaintext size and extent geometry.</summary>
  public long CanonicalLength => this._header.CanonicalLength;

  /// <summary>Human-readable cipher of the first passphrase packet, when present.</summary>
  public string CipherDescription => this._header.CipherDescription;

  /// <summary>Raw eight-byte authentication-token signature as lowercase hexadecimal.</summary>
  public string? PassphraseSignature
    => this._header.PassphrasePackets.Count == 0
      ? null
      : Convert.ToHexStringLower(this._header.PassphrasePackets[0].Signature);

  public IReadOnlyList<EcryptfsEntry> Entries => this._entries;

  /// <summary>Decrypts the single logical upper-file payload with a passphrase.</summary>
  /// <exception cref="CryptographicException">The passphrase does not match this lower file.</exception>
  public byte[] ExtractContent(string password) {
    if (this._header.DecryptedSize > int.MaxValue)
      throw new IOException("This eCryptfs payload is too large for the in-memory extraction API.");
    using var output = new MemoryStream((int)this._header.DecryptedSize);
    EcryptfsCodec.DecryptTo(this._stream, output, this._header, password);
    return output.ToArray();
  }

  /// <summary>
  /// Validates a passphrase against the authentication-token packet without
  /// materializing plaintext in memory. The ciphertext is streamed through the
  /// normal decoder into <see cref="Stream.Null"/>, so the same key and extent
  /// path used by extraction is exercised before a destructive mutation starts.
  /// </summary>
  /// <exception cref="CryptographicException">The passphrase does not match this lower file.</exception>
  public void ValidatePassword(string password)
    => EcryptfsCodec.DecryptTo(this._stream, Stream.Null, this._header, password);

  public void Dispose() {
    // The reader never owns the caller's stream.
  }
}
