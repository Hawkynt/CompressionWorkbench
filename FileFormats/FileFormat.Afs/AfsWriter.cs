using System.Text;

namespace FileFormat.Afs;

/// <summary>
/// Creates a Sega AFS (Athena File System) archive.
/// </summary>
public sealed class AfsWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data, DateTime? LastModified)> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="AfsWriter"/>.
  /// </summary>
  /// <param name="stream">The stream that will receive the AFS archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public AfsWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file entry to the archive.
  /// </summary>
  /// <param name="name">Entry name (max 31 ASCII bytes — the 32nd byte is the null terminator).</param>
  /// <param name="data">Raw file bytes.</param>
  /// <param name="lastModified">Optional last-modified timestamp.</param>
  /// <exception cref="ArgumentException">Thrown if <paramref name="name"/> exceeds 31 ASCII bytes.</exception>
  public void AddEntry(string name, byte[] data, DateTime? lastModified = null) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var byteLen = Encoding.ASCII.GetByteCount(name);
    if (byteLen > AfsConstants.MaxNameLength)
      throw new ArgumentException(
        $"Entry name '{name}' is {byteLen} bytes; AFS metadata names are limited to {AfsConstants.MaxNameLength} bytes.",
        nameof(name));

    this._entries.Add((name, data, lastModified));
  }

  /// <summary>
  /// Writes the archive to the underlying stream and finalizes the file layout.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var count = this._entries.Count;

    // Fixed region = header + index + metadata pointer; everything past this is paid file data.
    var fixedRegionSize = AfsConstants.HeaderSize
                        + AfsConstants.IndexEntrySize * count
                        + AfsConstants.MetadataPointerSize;

    var offsets = new uint[count];
    var cursor = (long)AlignUp(fixedRegionSize, AfsConstants.Alignment);

    for (var i = 0; i < count; ++i) {
      offsets[i] = checked((uint)cursor);
      cursor += this._entries[i].Data.Length;
      cursor = AlignUp(cursor, AfsConstants.Alignment);
    }

    var metadataOffset = checked((uint)cursor);
    var metadataSize = (uint)(count * AfsConstants.MetadataRecordSize);

    this._stream.Position = 0;

    // Header
    this._stream.Write(AfsConstants.Magic);
    this._stream.Write(BitConverter.GetBytes((uint)count));

    // File index
    Span<byte> idx = stackalloc byte[AfsConstants.IndexEntrySize];
    for (var i = 0; i < count; ++i) {
      BitConverter.TryWriteBytes(idx[0..4], offsets[i]);
      BitConverter.TryWriteBytes(idx[4..8], (uint)this._entries[i].Data.Length);
      this._stream.Write(idx);
    }

    // Metadata pointer (zeroed if no entries — keeps header self-consistent).
    Span<byte> metaPtr = stackalloc byte[AfsConstants.MetadataPointerSize];
    if (count > 0) {
      BitConverter.TryWriteBytes(metaPtr[0..4], metadataOffset);
      BitConverter.TryWriteBytes(metaPtr[4..8], metadataSize);
    }
    this._stream.Write(metaPtr);

    // Pay file data with 0x800 alignment between entries
    for (var i = 0; i < count; ++i) {
      PadTo(offsets[i]);
      var data = this._entries[i].Data;
      if (data.Length > 0)
        this._stream.Write(data);
    }

    // Metadata block
    if (count > 0) {
      PadTo(metadataOffset);
      WriteMetadataBlock();
    }
  }

  private void WriteMetadataBlock() {
    Span<byte> rec = stackalloc byte[AfsConstants.MetadataRecordSize];
    foreach (var (name, data, lastModified) in this._entries) {
      rec.Clear();

      var nameBytes = Encoding.ASCII.GetBytes(name);
      nameBytes.AsSpan().CopyTo(rec[..AfsConstants.MetadataNameSize]);

      var ts = lastModified ?? default;
      // A default DateTime (year 0001) is written as all-zeros so readers correctly skip it.
      if (lastModified.HasValue) {
        BitConverter.TryWriteBytes(rec[32..34], (ushort)ts.Year);
        BitConverter.TryWriteBytes(rec[34..36], (ushort)ts.Month);
        BitConverter.TryWriteBytes(rec[36..38], (ushort)ts.Day);
        BitConverter.TryWriteBytes(rec[38..40], (ushort)ts.Hour);
        BitConverter.TryWriteBytes(rec[40..42], (ushort)ts.Minute);
        BitConverter.TryWriteBytes(rec[42..44], (ushort)ts.Second);
      }
      BitConverter.TryWriteBytes(rec[44..48], (uint)data.Length);

      this._stream.Write(rec);
    }
  }

  private void PadTo(long absoluteOffset) {
    var current = this._stream.Position;
    if (current == absoluteOffset)
      return;
    if (current > absoluteOffset)
      throw new InvalidOperationException(
        $"AFS layout corrupted — current position {current} already past target {absoluteOffset}.");

    var pad = absoluteOffset - current;
    Span<byte> zero = stackalloc byte[512];
    while (pad > 0) {
      var chunk = (int)Math.Min(pad, zero.Length);
      this._stream.Write(zero[..chunk]);
      pad -= chunk;
    }
  }

  private static long AlignUp(long value, int alignment) {
    var rem = value % alignment;
    return rem == 0 ? value : value + (alignment - rem);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished)
        Finish();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
