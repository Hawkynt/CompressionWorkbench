using System.Text;

namespace FileFormat.Tar;

/// <summary>
/// Creates a TAR archive by writing entries sequentially.
/// </summary>
/// <summary>TAR header dialect. Influences how out-of-band fields (long names, large sizes) are encoded.</summary>
public enum TarHeaderFormat {
  /// <summary>POSIX 1003.1-1988 ustar. Long names fall back to GNU LongName when needed.</summary>
  Ustar,
  /// <summary>GNU extensions. Long names use the GNU @LongLink convention.</summary>
  Gnu,
  /// <summary>POSIX 1003.1-2001 PAX. Long names + large sizes use PAX extended headers.</summary>
  Pax,
}

/// <summary>
/// Writes a TAR archive to a destination stream. Defaults to the
/// <see cref="TarHeaderFormat.Ustar"/> dialect; switchable to GNU/PAX via
/// the constructor for long-name or large-size scenarios.
/// </summary>
public sealed class TarWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly TarHeaderFormat _format;
  private readonly int _blockingFactor;
  private long _bytesWritten;
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="TarWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the TAR archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="format">Header dialect to prefer (ustar / gnu / pax). Default ustar.</param>
  /// <param name="blockingFactor">Output is padded to this many 512-byte blocks on <see cref="Finish"/>.
  /// Default 1 (no extra record-level padding beyond the two end-of-archive zero blocks).
  /// The descriptor's <c>BlockingFactor</c> schema knob defaults to 20 (the classic 10 KiB
  /// record), but library callers that construct <see cref="TarWriter"/> directly keep the
  /// pre-existing byte-exact behavior.</param>
  public TarWriter(Stream stream, bool leaveOpen = false,
      TarHeaderFormat format = TarHeaderFormat.Ustar, int blockingFactor = 1) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._format = format;
    this._blockingFactor = blockingFactor < 1 ? 1 : blockingFactor;
  }

  /// <summary>
  /// Adds an entry to the archive with data from a stream.
  /// </summary>
  /// <param name="entry">The entry metadata.</param>
  /// <param name="data">An optional stream containing the entry data. May be <see langword="null"/> for directories or empty files.</param>
  public void AddEntry(TarEntry entry, Stream? data = null) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(entry);

    byte[]? dataBytes = null;
    if (data != null) {
      using var ms = new MemoryStream();
      data.CopyTo(ms);
      dataBytes = ms.ToArray();
      entry.Size = dataBytes.Length;
    }

    WriteEntryInternal(entry, dataBytes);
  }

  /// <summary>
  /// Adds an entry whose payload is streamed from <paramref name="data"/> in
  /// bounded chunks rather than buffered into RAM. The entry's logical
  /// <paramref name="size"/> must be known up front (TAR encodes it in the
  /// header before any payload byte), so this writes the header with the
  /// supplied size, then copies exactly <paramref name="size"/> bytes from
  /// <paramref name="data"/> in 64 KB chunks, then the 512-byte padding.
  /// </summary>
  /// <remarks>
  /// Produces byte-identical output to <see cref="AddEntry(TarEntry, ReadOnlySpan{byte})"/>
  /// for the same name/size/payload: identical PAX/GNU long-name handling,
  /// identical header, identical padding. Peak memory is the 64 KB copy buffer
  /// regardless of <paramref name="size"/>.
  /// </remarks>
  /// <param name="entry">The entry metadata. Its <see cref="TarEntry.Size"/> is set to <paramref name="size"/>.</param>
  /// <param name="size">The entry's logical byte size.</param>
  /// <param name="data">The source stream supplying exactly <paramref name="size"/> bytes.</param>
  public void AddStreamingEntry(TarEntry entry, long size, Stream data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(data);

    entry.Size = size;
    WriteEntryHeaderOnly(entry);

    // Copy the payload in bounded chunks.
    if (size > 0) {
      var buffer = new byte[64 * 1024];
      var remaining = size;
      while (remaining > 0) {
        var toRead = (int)Math.Min(buffer.Length, remaining);
        var read = data.Read(buffer, 0, toRead);
        if (read <= 0)
          throw new EndOfStreamException(
            $"TAR streaming entry '{entry.Name}': source ended {remaining} bytes short of the declared size {size}.");
        this._stream.Write(buffer, 0, read);
        this._bytesWritten += read;
        remaining -= read;
      }

      // Pad to the 512-byte block boundary, matching WriteEntryInternal.
      var padding = (int)((TarConstants.BlockSize - (size % TarConstants.BlockSize)) % TarConstants.BlockSize);
      if (padding > 0) {
        var zeroPad = new byte[padding];
        this._stream.Write(zeroPad, 0, padding);
        this._bytesWritten += padding;
      }
    }
  }

  /// <summary>
  /// Writes only the entry's header blocks (any PAX/GNU long-name prelude plus
  /// the 512-byte ustar header) without touching the payload — the shared
  /// header path for both buffered and streaming writes.
  /// </summary>
  private void WriteEntryHeaderOnly(TarEntry entry) {
    var paxAttrs = new Dictionary<string, string>();
    var nameUtf8 = Encoding.UTF8.GetBytes(entry.Name);
    var nameNeedsExt = nameUtf8.Length > TarConstants.NameLength || !IsAscii(nameUtf8);
    var linkUtf8 = !string.IsNullOrEmpty(entry.LinkName) ? Encoding.UTF8.GetBytes(entry.LinkName) : [];
    var linkNeedsExt = linkUtf8.Length > 0 && (linkUtf8.Length > TarConstants.LinkNameLength || !IsAscii(linkUtf8));

    if (this._format == TarHeaderFormat.Pax) {
      if (nameNeedsExt) paxAttrs["path"] = entry.Name;
      if (linkNeedsExt) paxAttrs["linkpath"] = entry.LinkName;
    } else if (this._format == TarHeaderFormat.Ustar) {
      if (nameNeedsExt) paxAttrs["path"] = entry.Name;
      if (linkNeedsExt) paxAttrs["linkpath"] = entry.LinkName;
    }

    if (entry.Size > 0x1FFFFFFFFFL)
      paxAttrs["size"] = entry.Size.ToString();

    if (paxAttrs.Count > 0) {
      WritePaxHeader(paxAttrs);
    } else {
      if (nameNeedsExt) WriteGnuLongName(entry.Name);
      if (linkNeedsExt) WriteGnuLongLink(entry.LinkName);
    }

    TarHeader.WriteHeader(this._stream, entry);
    this._bytesWritten += TarConstants.BlockSize;
  }

  /// <summary>
  /// Adds an entry to the archive with data from a byte span.
  /// </summary>
  /// <param name="entry">The entry metadata.</param>
  /// <param name="data">The entry data.</param>
  public void AddEntry(TarEntry entry, ReadOnlySpan<byte> data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(entry);

    var dataBytes = data.ToArray();
    entry.Size = dataBytes.Length;

    WriteEntryInternal(entry, dataBytes);
  }

  /// <summary>
  /// Writes the end-of-archive marker (two 512-byte zero blocks) and pads the
  /// output to a multiple of <c>blockingFactor * 512</c> bytes.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    // Write two 512-byte zero blocks as end-of-archive marker
    var zeroBlock = new byte[TarConstants.BlockSize];
    this._stream.Write(zeroBlock, 0, TarConstants.BlockSize);
    this._stream.Write(zeroBlock, 0, TarConstants.BlockSize);
    this._bytesWritten += 2L * TarConstants.BlockSize;

    // Pad up to the blocking-factor boundary.
    var recordSize = (long)this._blockingFactor * TarConstants.BlockSize;
    var tail = this._bytesWritten % recordSize;
    if (tail != 0) {
      var pad = (int)(recordSize - tail);
      var buf = new byte[Math.Min(pad, TarConstants.BlockSize)];
      var remaining = pad;
      while (remaining > 0) {
        var n = Math.Min(remaining, buf.Length);
        this._stream.Write(buf, 0, n);
        remaining -= n;
      }
      this._bytesWritten += pad;
    }
    this._stream.Flush();
  }

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished)
        Finish();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  private void WriteEntryInternal(TarEntry entry, byte[]? data) {
    // Header (PAX/GNU prelude + 512-byte ustar header) is written by the
    // shared helper so the buffered and streaming paths emit identical bytes.
    // The buffered path passes data==null for directories where the size-PAX
    // promotion never applies; the shared helper keys solely on entry.Size,
    // which is 0 for those, so behavior is preserved.
    WriteEntryHeaderOnly(entry);

    // Write data
    if (data != null && data.Length > 0) {
      this._stream.Write(data, 0, data.Length);
      this._bytesWritten += data.Length;

      // Pad to 512-byte boundary
      var padding = (TarConstants.BlockSize - (data.Length % TarConstants.BlockSize)) % TarConstants.BlockSize;
      if (padding > 0) {
        var zeroPad = new byte[padding];
        this._stream.Write(zeroPad, 0, padding);
        this._bytesWritten += padding;
      }
    }
  }

  private void WriteGnuLongName(string longName) {
    var nameBytes = Encoding.UTF8.GetBytes(longName);
    // Include a null terminator in the data
    var nameData = new byte[nameBytes.Length + 1];
    nameBytes.AsSpan().CopyTo(nameData);

    var longNameEntry = new TarEntry {
      Name = "././@LongLink",
      TypeFlag = TarConstants.TypeGnuLongName,
      Size = nameData.Length,
      Mode = 0,
    };

    TarHeader.WriteHeader(this._stream, longNameEntry);
    this._stream.Write(nameData, 0, nameData.Length);

    // Pad to 512-byte boundary
    var padding = (TarConstants.BlockSize - (nameData.Length % TarConstants.BlockSize)) % TarConstants.BlockSize;
    if (padding > 0) {
      var zeroPad = new byte[padding];
      this._stream.Write(zeroPad, 0, padding);
    }
  }

  private void WriteGnuLongLink(string longLink) {
    var linkBytes = Encoding.UTF8.GetBytes(longLink);
    var linkData = new byte[linkBytes.Length + 1];
    linkBytes.AsSpan().CopyTo(linkData);

    var longLinkEntry = new TarEntry {
      Name = "././@LongLink",
      TypeFlag = TarConstants.TypeGnuLongLink,
      Size = linkData.Length,
      Mode = 0,
    };

    TarHeader.WriteHeader(this._stream, longLinkEntry);
    this._stream.Write(linkData, 0, linkData.Length);

    var padding = (TarConstants.BlockSize - (linkData.Length % TarConstants.BlockSize)) % TarConstants.BlockSize;
    if (padding > 0) {
      var zeroPad = new byte[padding];
      this._stream.Write(zeroPad, 0, padding);
    }
  }

  private void WritePaxHeader(Dictionary<string, string> attrs) {
    // Build PAX data: each record is "<length> <key>=<value>\n"
    using var paxData = new MemoryStream();
    foreach (var (key, value) in attrs) {
      var record = FormatPaxRecord(key, value);
      paxData.Write(record, 0, record.Length);
    }

    var paxBytes = paxData.ToArray();

    var paxEntry = new TarEntry {
      Name = "PaxHeader/pax",
      TypeFlag = TarConstants.TypePaxHeader,
      Size = paxBytes.Length,
      Mode = 0,
    };

    TarHeader.WriteHeader(this._stream, paxEntry);
    this._stream.Write(paxBytes, 0, paxBytes.Length);

    var padding = (TarConstants.BlockSize - (paxBytes.Length % TarConstants.BlockSize)) % TarConstants.BlockSize;
    if (padding > 0) {
      var zeroPad = new byte[padding];
      this._stream.Write(zeroPad, 0, padding);
    }
  }

  private static byte[] FormatPaxRecord(string key, string value) {
    // Format: "<length> <key>=<value>\n"
    // Length is in bytes and includes everything (the length digits, space, key, '=', value, '\n')
    var payload = Encoding.UTF8.GetBytes($" {key}={value}\n");
    var len = payload.Length + 1; // start assuming 1 digit for length
    while (Encoding.UTF8.GetByteCount(len.ToString()) + payload.Length != len)
      len = Encoding.UTF8.GetByteCount(len.ToString()) + payload.Length;
    var prefix = Encoding.UTF8.GetBytes(len.ToString());
    var record = new byte[prefix.Length + payload.Length];
    prefix.CopyTo(record, 0);
    payload.CopyTo(record, prefix.Length);
    return record;
  }

  /// <summary>
  /// Creates a TAR archive split into multiple volumes with GNU multi-volume continuation headers.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries) {
    using var ms = new MemoryStream();
    using (var writer = new TarWriter(ms, leaveOpen: true)) {
      foreach (var (name, data) in entries)
        writer.AddEntry(new TarEntry { Name = name }, data.AsSpan());
      writer.Finish();
    }

    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }

  private static bool IsAscii(byte[] data) {
    foreach (var b in data) {
      if (b > 127) return false;
    }
    return true;
  }
}
