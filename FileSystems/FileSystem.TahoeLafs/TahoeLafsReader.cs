#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.TahoeLafs;

/// <summary>
/// Reads Tahoe-LAFS share-bucket files. Tahoe-LAFS is a distributed least-
/// authority file system: each upload is erasure-coded into N Reed-Solomon
/// shares, of which K are needed to reconstruct the plaintext. A single
/// share file (typically named after a base32 share identifier and stored
/// on disk by a "storage server") is a well-defined on-disk container —
/// THIS is what we recognise. The container itself is opaque (capability-
/// encrypted ciphertext) without the read-cap, so the contained share data
/// is surfaced as a single opaque entry alongside the parsed header.
///
/// Share-v1 / share-v2 header layout (big-endian, 32-bit fields at the
/// start of the share bucket file):
///   0x00 u32 version            (1 == immutable share v1, 2 == mutable v2)
///   0x04 u32 data-size          (length of contained ciphertext payload)
///   0x08 u32 lease-count        (number of leases following the data)
///   0x0C ... share-data-block   (capability-encrypted ciphertext)
/// Mutable (v2) buckets add a sequence number + root-hash block — we parse
/// only the leading fields to confirm format and report metadata.
/// </summary>
public sealed class TahoeLafsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<TahoeLafsEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<TahoeLafsEntry> Entries => _entries;

  /// <summary>
  /// Gets or sets the version.
  /// </summary>
  public uint Version { get; private set; }
  /// <summary>
  /// Gets or sets the data size.
  /// </summary>
  public uint DataSize { get; private set; }
  /// <summary>
  /// Gets or sets the lease count.
  /// </summary>
  public uint LeaseCount { get; private set; }
  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
  public bool ValidHeader { get; private set; }

  /// <summary>
  /// Initializes a new instance of <see cref="TahoeLafsReader"/>.
  /// </summary>
  public TahoeLafsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 12)
      throw new InvalidDataException("TahoeLafs: file too small for share header.");

    var version = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(0, 4));
    if (version != 1 && version != 2)
      throw new InvalidDataException($"TahoeLafs: unknown share version {version} (expected 1 or 2).");

    this.Version = version;
    this.DataSize = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(4, 4));
    this.LeaseCount = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(8, 4));
    this.ValidHeader = true;

    // Carve the share payload as opaque ciphertext blob.
    var payloadStart = 12;
    var payloadLen = (int)Math.Min((long)this.DataSize, _data.Length - payloadStart);
    if (payloadLen < 0) payloadLen = 0;

    var meta = BuildMetadata();
    _entries.Add(new TahoeLafsEntry { Name = "FULL.tahoe-share", Size = _data.Length, IsDirectory = false, Data = _data });
    _entries.Add(new TahoeLafsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Data = meta });

    if (payloadLen > 0) {
      var blob = _data.AsSpan(payloadStart, payloadLen).ToArray();
      var name = version == 1 ? "share.immutable.bin" : "share.mutable.bin";
      _entries.Add(new TahoeLafsEntry { Name = name, Size = blob.Length, IsDirectory = false, Data = blob });
    }
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=Tahoe-LAFS share\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version={this.Version}\n");
    bldr.Append("share_kind=").Append(this.Version == 1 ? "immutable" : "mutable").Append('\n');
    bldr.Append(CultureInfo.InvariantCulture, $"data_size={this.DataSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"lease_count={this.LeaseCount}\n");
    bldr.Append("note=Share payload is capability-encrypted ciphertext; decryption requires read-cap.\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(TahoeLafsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
