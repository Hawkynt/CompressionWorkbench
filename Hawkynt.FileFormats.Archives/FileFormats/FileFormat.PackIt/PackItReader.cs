using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.PackIt;

/// <summary>
/// Reads entries from a PackIt (.pit) classic Macintosh archive.
/// </summary>
/// <remarks>
/// <para>PackIt (Harry Chesley, 1984-86) has no archive header: entries follow one another, each
/// opened by a four-byte magic, and the archive ends with <c>"PEnd"</c>.</para>
/// <code>
///   magic    "PMag" stored · "PMa4" Huffman · "PMa5" Huffman + XOR · "PMa6" Huffman + DES
///   [0]      filename length
///   [1..64]  filename (63 bytes)
///   [64..68] file type        [68..72] creator      [72..74] Finder flags   [74..76] unused
///   [76..80] data fork size   [80..84] resource fork size
///   [84..88] modified         [88..92] created      (seconds since 1904, big-endian)
///   [92..94] CRC-16/XMODEM of bytes 0..92
///   data fork, resource fork, CRC-16/XMODEM of both forks
/// </code>
/// <para>In a compressed entry everything after the magic — the 94-byte header, both forks and the fork
/// CRC — is one Huffman stream (see <see cref="PackItHuffmanDecoder"/>), padded to a byte boundary.
/// Nothing records its compressed length, so listing decodes each compressed entry to find the next.
/// Encrypted entries cannot be walked without the password; listing stops at the first one.</para>
/// <para>Sourcing: layout from The Unarchiver's XADMaster (<c>XADPackItParser.m</c>,
/// LGPL-2.1-or-later); the header CRC (not checked by XADMaster) was identified by measurement on
/// real PackIt II archives, every header of which it matches.</para>
/// </remarks>
public sealed class PackItReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<PackItEntry> _entries = [];
  private bool _disposed;

  /// <summary>
  /// Opens a PackIt archive from the given stream and reads all entries.
  /// </summary>
  /// <param name="stream">A readable and seekable stream containing a .pit archive.</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open after this reader is disposed.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  /// <exception cref="InvalidDataException">The stream does not start with a PackIt entry or end marker.</exception>
  public PackItReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this.ParseArchive();
  }

  /// <summary>Gets the list of entries found in the archive.</summary>
  public IReadOnlyList<PackItEntry> Entries => this._entries;

  /// <summary>Gets whether listing stopped early (truncated archive or an entry it cannot walk past).</summary>
  public bool IsIncomplete { get; private set; }

  /// <summary>Extracts the data fork of the specified entry.</summary>
  /// <exception cref="NotSupportedException">The entry is encrypted.</exception>
  /// <exception cref="InvalidDataException">The header or fork CRC-16 does not match.</exception>
  public byte[] Extract(PackItEntry entry) => this.ExtractForks(entry).Data;

  /// <summary>Extracts the resource fork of the specified entry.</summary>
  /// <exception cref="NotSupportedException">The entry is encrypted.</exception>
  /// <exception cref="InvalidDataException">The header or fork CRC-16 does not match.</exception>
  public byte[] ExtractResourceFork(PackItEntry entry) => this.ExtractForks(entry).Resource;

  /// <summary>Decodes both forks of <paramref name="entry"/> and checks every CRC.</summary>
  public (byte[] Data, byte[] Resource) ExtractForks(PackItEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    if (entry.IsEncrypted)
      throw new NotSupportedException($"PackIt entry '{entry.Name}' is password-encrypted; decryption is not supported.");
    if (!entry.HeaderCrcValid)
      throw new InvalidDataException($"PackIt entry '{entry.Name}' has a header that fails its CRC-16.");

    var forkBytes = checked((int)(entry.DataForkSize + entry.ResourceForkSize));
    var body = new byte[forkBytes + 2];
    if (entry.IsCompressed) {
      this._stream.Position = entry.DataOffset;
      var decoder = new PackItHuffmanDecoder(this._stream);
      Span<byte> header = stackalloc byte[PackItConstants.HeaderSize];
      decoder.Decode(header);
      decoder.Decode(body);
    } else {
      this._stream.Position = entry.DataOffset;
      this._stream.ReadExactly(body);
    }

    var stored = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(forkBytes));
    var actual = Crc16Ccitt.Compute(body.AsSpan(0, forkBytes));
    if (stored != actual)
      throw new InvalidDataException(
        $"PackIt entry '{entry.Name}' fails its fork CRC-16: stored 0x{stored:X4}, computed 0x{actual:X4}.");

    var dataLength = (int)entry.DataForkSize;
    return (body.AsSpan(0, dataLength).ToArray(), body.AsSpan(dataLength, forkBytes - dataLength).ToArray());
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  // ── Parsing ───────────────────────────────────────────────────────────────────

  private void ParseArchive() {
    Span<byte> magic = stackalloc byte[4];
    Span<byte> header = stackalloc byte[PackItConstants.HeaderSize];
    var first = true;

    while (true) {
      var entryOffset = this._stream.Position;
      if (this._stream.Length - entryOffset < 4) {
        // A missing "PEnd" is tolerated (some transfers drop it); a missing first entry is not a PackIt file.
        if (first)
          throw new InvalidDataException("Stream is too short to be a PackIt archive.");
        this.IsIncomplete = this._stream.Length != entryOffset;
        return;
      }
      this._stream.ReadExactly(magic);

      if (magic.SequenceEqual(PackItConstants.MagicEnd))
        return;

      var stored = magic.SequenceEqual(PackItConstants.MagicStored);
      var compressed = magic.SequenceEqual(PackItConstants.MagicCompressed);
      var encrypted = magic.SequenceEqual(PackItConstants.MagicXorEncrypted) || magic.SequenceEqual(PackItConstants.MagicDesEncrypted);
      if (!stored && !compressed && !encrypted) {
        if (first)
          throw new InvalidDataException("Stream does not start with a PackIt entry magic.");
        this.IsIncomplete = true;
        return;
      }
      first = false;

      if (encrypted) {
        // The header itself is encrypted; without the password neither it nor the entry's length is known.
        this._entries.Add(new PackItEntry {
          Name = "(encrypted)", IsCompressed = true, IsEncrypted = true,
          EntryOffset = entryOffset, DataOffset = entryOffset + 4, EntryLength = this._stream.Length - entryOffset,
        });
        this.IsIncomplete = true;
        return;
      }

      var bodyStart = this._stream.Position;
      try {
        PackItHuffmanDecoder? decoder = null;
        if (stored)
          this._stream.ReadExactly(header);
        else {
          decoder = new PackItHuffmanDecoder(this._stream);
          decoder.Decode(header);
        }

        var dataSize = BinaryPrimitives.ReadUInt32BigEndian(header[76..]);
        var rsrcSize = BinaryPrimitives.ReadUInt32BigEndian(header[80..]);
        var crcValid = BinaryPrimitives.ReadUInt16BigEndian(header[92..])
          == Crc16Ccitt.Compute(header[..PackItConstants.HeaderCrcSpan]);
        var forkBytes = (long)dataSize + rsrcSize;

        long end;
        if (stored) {
          end = bodyStart + PackItConstants.HeaderSize + forkBytes + 2;
          if (end > this._stream.Length)
            throw new EndOfStreamException();
        } else {
          // Walk the forks and their CRC to find where the next entry starts.
          var scratch = new byte[64 * 1024];
          for (var left = forkBytes + 2; left > 0;) {
            var chunk = (int)Math.Min(left, scratch.Length);
            decoder!.Decode(scratch.AsSpan(0, chunk));
            left -= chunk;
          }
          end = bodyStart + decoder!.BytesConsumed;
        }

        this._entries.Add(new PackItEntry {
          Name = ReadName(header),
          FileType = Encoding.Latin1.GetString(header[64..68]),
          Creator = Encoding.Latin1.GetString(header[68..72]),
          FinderFlags = BinaryPrimitives.ReadUInt16BigEndian(header[72..]),
          DataForkSize = dataSize,
          ResourceForkSize = rsrcSize,
          Modified = FromMacTime(BinaryPrimitives.ReadUInt32BigEndian(header[84..])),
          Created = FromMacTime(BinaryPrimitives.ReadUInt32BigEndian(header[88..])),
          IsCompressed = compressed,
          HeaderCrcValid = crcValid,
          EntryOffset = entryOffset,
          DataOffset = stored ? bodyStart + PackItConstants.HeaderSize : bodyStart,
          EntryLength = end - entryOffset,
        });
        this._stream.Position = end;
      } catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException) {
        if (this._entries.Count == 0)
          throw new InvalidDataException("PackIt archive is truncated inside its first entry.", ex);
        this.IsIncomplete = true;
        return;
      }
    }
  }

  private static string ReadName(ReadOnlySpan<byte> header) {
    var length = Math.Min((int)header[0], PackItConstants.FileNameMaxLength);
    return Encoding.Latin1.GetString(header.Slice(1, length));
  }

  internal static DateTime? FromMacTime(uint seconds) =>
    seconds == 0 ? null : DateTime.UnixEpoch.AddSeconds(seconds - PackItConstants.MacEpochOffsetSeconds);

  internal static uint ToMacTime(DateTime? time) {
    if (time is null)
      return 0;
    var seconds = (long)(time.Value.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds + PackItConstants.MacEpochOffsetSeconds;
    return seconds is < 0 or > uint.MaxValue ? 0 : (uint)seconds;
  }
}
