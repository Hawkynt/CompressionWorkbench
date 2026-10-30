using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.PackIt;

/// <summary>
/// Creates a PackIt (.pit) classic Macintosh archive of stored ("PMag") entries.
/// </summary>
/// <remarks>
/// Entries are written as they are added; <see cref="Dispose"/> writes the <c>"PEnd"</c> marker, so the
/// stream must stay writable until then. The layout is the one <see cref="PackItReader"/> documents,
/// header and fork CRC-16s included; The Unarchiver (<c>unar</c>) reads it back.
/// </remarks>
public sealed class PackItWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>
  /// Initialises a new <see cref="PackItWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the .pit archive to.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open when this writer is disposed.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
  public PackItWriter(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Appends a stored file entry to the archive.
  /// </summary>
  /// <param name="name">The filename (up to 63 characters, Latin-1 encoded; longer names are cut).</param>
  /// <param name="data">The data fork bytes.</param>
  /// <param name="fileType">The four-character Mac file type code (default "TEXT").</param>
  /// <param name="creator">The four-character Mac creator code (default "CWIE").</param>
  /// <param name="resourceFork">The resource fork bytes, or <see langword="null"/> for none.</param>
  /// <param name="modified">The modification time, or <see langword="null"/> to record none.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="data"/> is null.</exception>
  /// <exception cref="ObjectDisposedException">Thrown when this writer has been disposed.</exception>
  public void AddFile(
      string name,
      byte[] data,
      string fileType = "TEXT",
      string creator  = "CWIE",
      byte[]? resourceFork = null,
      DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    ObjectDisposedException.ThrowIf(this._disposed, this);

    WriteStoredEntry(this._stream, name, data, resourceFork ?? [], fileType, creator, modified);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      this._stream.Write(PackItConstants.MagicEnd);
      this._stream.Flush();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  // ── Serialisation ─────────────────────────────────────────────────────────────

  /// <summary>Writes one complete "PMag" entry (magic, header, forks, fork CRC).</summary>
  internal static void WriteStoredEntry(Stream output, string name, ReadOnlySpan<byte> data, ReadOnlySpan<byte> resourceFork,
      string fileType, string creator, DateTime? modified) {
    Span<byte> header = stackalloc byte[PackItConstants.HeaderSize];
    header.Clear();

    var nameBytes = Encoding.Latin1.GetBytes(name);
    var nameLength = Math.Min(nameBytes.Length, PackItConstants.FileNameMaxLength);
    header[0] = (byte)nameLength;
    nameBytes.AsSpan(0, nameLength).CopyTo(header[1..]);
    WriteFourCc(header.Slice(64, 4), fileType);
    WriteFourCc(header.Slice(68, 4), creator);
    // [72..74] Finder flags and [74..76] are left zero.
    BinaryPrimitives.WriteUInt32BigEndian(header[76..], (uint)data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header[80..], (uint)resourceFork.Length);
    var macTime = PackItReader.ToMacTime(modified);
    BinaryPrimitives.WriteUInt32BigEndian(header[84..], macTime);
    BinaryPrimitives.WriteUInt32BigEndian(header[88..], macTime);
    BinaryPrimitives.WriteUInt16BigEndian(header[92..], Crc16Ccitt.Compute(header[..PackItConstants.HeaderCrcSpan]));

    var crc = new Crc16Ccitt();
    crc.Update(data);
    crc.Update(resourceFork);
    Span<byte> trailer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(trailer, (ushort)crc.Value);

    output.Write(PackItConstants.MagicStored);
    output.Write(header);
    output.Write(data);
    output.Write(resourceFork);
    output.Write(trailer);
  }

  private static void WriteFourCc(Span<byte> dest, string value) {
    var bytes = Encoding.Latin1.GetBytes(value);
    var length = Math.Min(bytes.Length, 4);
    bytes.AsSpan(0, length).CopyTo(dest);
    dest[length..].Fill((byte)' ');
  }
}
