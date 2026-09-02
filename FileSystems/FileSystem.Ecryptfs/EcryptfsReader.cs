#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Ecryptfs;

/// <summary>
/// Reads eCryptfs per-file encryption headers. eCryptfs is a stacking
/// file-level encryption filesystem (Linux) — every encrypted file is
/// stored on the lower filesystem as a regular file whose first page is
/// a metadata header followed by AES-CBC ciphertext extents. The header
/// starts with a 4-byte big-endian marker (<c>0x3C81B7F5</c>) so the
/// individual on-disk container is well-defined and detectable.
/// Decryption requires the user's mount passphrase + EFEK (Encrypted
/// File Encryption Key) tag-3 / tag-11 packets and is OUT OF SCOPE; this
/// reader surfaces the parsed header + the encrypted payload as a single
/// opaque entry.
///
/// File header layout (big-endian, file offset 0):
///   0x00 u32  marker            == 0x3C81B7F5
///   0x04 u64  decrypted-size    (plaintext length, host-endian on Linux)
///   0x0C u32  flags
///   0x10 u32  extent-size       (typically 4096)
///   0x14 ...  EFEK packets, tag-3 / tag-11 OpenPGP-style
///   ~0x800   start of AES-CBC ciphertext extents
/// </summary>
public sealed class EcryptfsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<EcryptfsEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<EcryptfsEntry> Entries => _entries;

  /// <summary>
  /// Gets or sets the marker.
  /// </summary>
  public uint Marker { get; private set; }
  /// <summary>
  /// Gets or sets the decrypted size.
  /// </summary>
  public ulong DecryptedSize { get; private set; }
  /// <summary>
  /// Gets or sets the flags.
  /// </summary>
  public uint Flags { get; private set; }
  /// <summary>
  /// Gets or sets the extent size.
  /// </summary>
  public uint ExtentSize { get; private set; }
  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// Defines the ecryptfs marker constant value.
  /// </summary>
  public const uint EcryptfsMarker = 0x3C81B7F5u;
  private const int HeaderMinSize = 24;

  /// <summary>
  /// Initializes a new instance of <see cref="EcryptfsReader"/>.
  /// </summary>
  public EcryptfsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderMinSize)
      throw new InvalidDataException("Ecryptfs: file too small for header.");

    var marker = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(0, 4));
    if (marker != EcryptfsMarker)
      throw new InvalidDataException($"Ecryptfs: invalid marker 0x{marker:X8} at offset 0 (expected 0x{EcryptfsMarker:X8}).");

    this.Marker = marker;
    // Decrypted-size is 8 bytes BE per the eCryptfs on-disk format documentation
    // (kernel writes it big-endian regardless of host byte order so files round-trip across hosts).
    this.DecryptedSize = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(4, 8));
    this.Flags = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(12, 4));
    this.ExtentSize = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(16, 4));
    this.ValidHeader = true;

    var meta = BuildMetadata();
    _entries.Add(new EcryptfsEntry { Name = "FULL.ecryptfs", Size = _data.Length, IsDirectory = false, Data = _data });
    _entries.Add(new EcryptfsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Data = meta });

    // Surface the encrypted ciphertext as opaque blob (everything after the metadata page).
    var payloadStart = (int)Math.Min((long)Math.Max(this.ExtentSize, 4096), _data.Length);
    var payloadLen = _data.Length - payloadStart;
    if (payloadLen > 0) {
      var blob = _data.AsSpan(payloadStart, payloadLen).ToArray();
      _entries.Add(new EcryptfsEntry { Name = "ciphertext.bin", Size = blob.Length, IsDirectory = false, Data = blob });
    }
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=eCryptfs (file-level encryption)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"marker=0x{this.Marker:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"decrypted_size={this.DecryptedSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"flags=0x{this.Flags:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"extent_size={this.ExtentSize}\n");
    bldr.Append("note=Ciphertext exposed opaque; decryption requires mount passphrase + EFEK.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(EcryptfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
