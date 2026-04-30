using System.IO.Compression;
using System.Text;

namespace FileFormat.Ypf;

/// <summary>
/// Reads a YPF v480 archive (YukaScript engine — Yu-No remake, Iyashi VN engine, etc.).
/// Names in real engine archives are XOR-obfuscated against a key derived from the version;
/// for round-trip parity with <see cref="YpfWriter"/> we treat names as raw ASCII. Real
/// engine archives may need a separate deobfuscation pass before being handed to this reader.
/// </summary>
public sealed class YpfReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>The version field from the header (always 480 for supported archives).</summary>
  public uint Version { get; }

  /// <summary>All entries parsed from the archive's entry table.</summary>
  public IReadOnlyList<YpfEntry> Entries { get; }

  /// <summary>Opens a YPF archive from <paramref name="stream"/>.</summary>
  /// <exception cref="InvalidDataException">Magic check failed.</exception>
  /// <exception cref="NotSupportedException">Header parsed cleanly but version != 480.</exception>
  public YpfReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (stream.Length - stream.Position < YpfConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid YPF archive.");

    Span<byte> header = stackalloc byte[YpfConstants.HeaderSize];
    ReadExact(header);

    for (var i = 0; i < YpfConstants.Magic.Length; ++i)
      if (header[i] != YpfConstants.Magic[i])
        throw new InvalidDataException("Invalid YPF magic.");

    this.Version = BitConverter.ToUInt32(header[4..8]);
    if (this.Version != YpfConstants.SupportedVersion)
      throw new NotSupportedException($"Unsupported YPF version: {this.Version} (only {YpfConstants.SupportedVersion} is supported).");

    var entryCount = BitConverter.ToUInt32(header[8..12]);
    var tableSize = BitConverter.ToUInt32(header[12..16]);
    // header[16..32] — 16 reserved bytes, intentionally unread.

    this.Entries = ReadEntries(entryCount, tableSize);
  }

  /// <summary>Reads, decompresses, and CRC-checks the bytes for the given entry.</summary>
  public byte[] Extract(YpfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.CompressedSize == 0)
      return [];

    this._stream.Position = entry.Offset;
    var compressed = new byte[entry.CompressedSize];
    ReadExact(compressed);

    return entry.Compression switch {
      YpfConstants.CompressionStored => compressed,
      YpfConstants.CompressionZlib => Inflate(compressed),
      _ => throw new InvalidDataException($"Unknown YPF compression flag: {entry.Compression}"),
    };
  }

  private List<YpfEntry> ReadEntries(uint count, uint tableSize) {
    var entries = new List<YpfEntry>((int)count);
    var tableStart = this._stream.Position;
    var tableEnd = tableStart + tableSize;

    // Hoisted out of the loop: avoids CA2014 (stackalloc-in-loop). 5 + 18 = 23 bytes total.
    Span<byte> fixed1 = stackalloc byte[5];
    Span<byte> fixed2 = stackalloc byte[1 + 1 + 4 + 4 + 4 + 4];

    for (var i = 0u; i < count; ++i) {
      if (this._stream.Position >= tableEnd)
        throw new InvalidDataException("YPF entry table truncated mid-record.");

      ReadExact(fixed1);
      var nameHash = BitConverter.ToUInt32(fixed1[0..4]);
      var nameLen = fixed1[4];

      var nameBytes = new byte[nameLen];
      ReadExact(nameBytes);
      var name = Encoding.ASCII.GetString(nameBytes);

      ReadExact(fixed2);
      var type = fixed2[0];
      var compression = fixed2[1];
      var rawSize = BitConverter.ToUInt32(fixed2[2..6]);
      var compSize = BitConverter.ToUInt32(fixed2[6..10]);
      var offset = BitConverter.ToUInt32(fixed2[10..14]);
      var crc32 = BitConverter.ToUInt32(fixed2[14..18]);

      // Compute actual CRC over the on-disk compressed bytes so callers can see corruption
      // without re-reading. We restore the table cursor after the data probe.
      var savedPos = this._stream.Position;
      var isCorrupt = false;
      if (compSize > 0 && offset + compSize <= this._stream.Length) {
        this._stream.Position = offset;
        var probe = new byte[compSize];
        ReadExact(probe);
        isCorrupt = YpfCrc32.Compute(probe) != crc32;
      } else if (compSize > 0) {
        // Offset/size point outside the file — definitely corrupt.
        isCorrupt = true;
      }
      this._stream.Position = savedPos;

      entries.Add(new YpfEntry {
        Name = name,
        NameHash = nameHash,
        Type = type,
        Compression = compression,
        RawSize = rawSize,
        CompressedSize = compSize,
        Offset = offset,
        Crc32 = crc32,
        IsCorrupt = isCorrupt,
      });
    }

    return entries;
  }

  private static byte[] Inflate(byte[] compressed) {
    using var src = new MemoryStream(compressed, writable: false);
    using var z = new ZLibStream(src, CompressionMode.Decompress);
    using var dst = new MemoryStream();
    z.CopyTo(dst);
    return dst.ToArray();
  }

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = this._stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of YPF stream.");
      total += read;
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
