using System.Text;

namespace FileFormat.Tar;

/// <summary>
/// Creates a TAR archive by writing entries sequentially.
/// </summary>
public sealed class TarWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="TarWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the TAR archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public TarWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
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
  /// Adds an entry to the archive with data from a byte span.
  /// </summary>
  /// <param name="entry">The entry metadata.</param>
  /// <param name="data">The entry data.</param>
  public void AddEntry(TarEntry entry, ReadOnlySpan<byte> data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(entry);

    byte[] dataBytes = data.ToArray();
    entry.Size = dataBytes.Length;

    WriteEntryInternal(entry, dataBytes);
  }

  /// <summary>
  /// Writes the end-of-archive marker (two 512-byte zero blocks).
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    // Write two 512-byte zero blocks as end-of-archive marker
    byte[] zeroBlock = new byte[TarConstants.BlockSize];
    this._stream.Write(zeroBlock, 0, TarConstants.BlockSize);
    this._stream.Write(zeroBlock, 0, TarConstants.BlockSize);
    this._stream.Flush();
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

  private void WriteEntryInternal(TarEntry entry, byte[]? data) {
    // Collect PAX attributes for fields that can't fit in the standard header
    var paxAttrs = new Dictionary<string, string>();

    byte[] nameUtf8 = Encoding.UTF8.GetBytes(entry.Name);
    if (nameUtf8.Length > TarConstants.NameLength || !IsAscii(nameUtf8))
      paxAttrs["path"] = entry.Name;

    if (!string.IsNullOrEmpty(entry.LinkName)) {
      byte[] linkUtf8 = Encoding.UTF8.GetBytes(entry.LinkName);
      if (linkUtf8.Length > TarConstants.LinkNameLength || !IsAscii(linkUtf8))
        paxAttrs["linkpath"] = entry.LinkName;
    }

    if (data != null && entry.Size > 0x1FFFFFFFFFL)
      paxAttrs["size"] = entry.Size.ToString();

    // Write PAX extended header if needed, otherwise fall back to GNU long names
    if (paxAttrs.Count > 0) {
      WritePaxHeader(paxAttrs);
    }
    else {
      if (nameUtf8.Length > TarConstants.NameLength)
        WriteGnuLongName(entry.Name);

      if (!string.IsNullOrEmpty(entry.LinkName) &&
        Encoding.UTF8.GetByteCount(entry.LinkName) > TarConstants.LinkNameLength)
        WriteGnuLongLink(entry.LinkName);
    }

    // Write the entry header
    TarHeader.WriteHeader(this._stream, entry);

    // Write data
    if (data != null && data.Length > 0) {
      this._stream.Write(data, 0, data.Length);

      // Pad to 512-byte boundary
      int padding = (TarConstants.BlockSize - (data.Length % TarConstants.BlockSize)) % TarConstants.BlockSize;
      if (padding > 0) {
        byte[] zeroPad = new byte[padding];
        this._stream.Write(zeroPad, 0, padding);
      }
    }
  }

  private void WriteGnuLongName(string longName) {
    byte[] nameBytes = Encoding.UTF8.GetBytes(longName);
    // Include a null terminator in the data
    byte[] nameData = new byte[nameBytes.Length + 1];
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
    int padding = (TarConstants.BlockSize - (nameData.Length % TarConstants.BlockSize)) % TarConstants.BlockSize;
    if (padding > 0) {
      byte[] zeroPad = new byte[padding];
      this._stream.Write(zeroPad, 0, padding);
    }
  }

  private void WriteGnuLongLink(string longLink) {
    byte[] linkBytes = Encoding.UTF8.GetBytes(longLink);
    byte[] linkData = new byte[linkBytes.Length + 1];
    linkBytes.AsSpan().CopyTo(linkData);

    var longLinkEntry = new TarEntry {
      Name = "././@LongLink",
      TypeFlag = TarConstants.TypeGnuLongLink,
      Size = linkData.Length,
      Mode = 0,
    };

    TarHeader.WriteHeader(this._stream, longLinkEntry);
    this._stream.Write(linkData, 0, linkData.Length);

    int padding = (TarConstants.BlockSize - (linkData.Length % TarConstants.BlockSize)) % TarConstants.BlockSize;
    if (padding > 0) {
      byte[] zeroPad = new byte[padding];
      this._stream.Write(zeroPad, 0, padding);
    }
  }

  private void WritePaxHeader(Dictionary<string, string> attrs) {
    // Build PAX data: each record is "<length> <key>=<value>\n"
    using var paxData = new MemoryStream();
    foreach (var (key, value) in attrs) {
      byte[] record = FormatPaxRecord(key, value);
      paxData.Write(record, 0, record.Length);
    }

    byte[] paxBytes = paxData.ToArray();

    var paxEntry = new TarEntry {
      Name = "PaxHeader/pax",
      TypeFlag = TarConstants.TypePaxHeader,
      Size = paxBytes.Length,
      Mode = 0,
    };

    TarHeader.WriteHeader(this._stream, paxEntry);
    this._stream.Write(paxBytes, 0, paxBytes.Length);

    int padding = (TarConstants.BlockSize - (paxBytes.Length % TarConstants.BlockSize)) % TarConstants.BlockSize;
    if (padding > 0) {
      byte[] zeroPad = new byte[padding];
      this._stream.Write(zeroPad, 0, padding);
    }
  }

  private static byte[] FormatPaxRecord(string key, string value) {
    // Format: "<length> <key>=<value>\n"
    // Length is in bytes and includes everything (the length digits, space, key, '=', value, '\n')
    byte[] payload = Encoding.UTF8.GetBytes($" {key}={value}\n");
    int len = payload.Length + 1; // start assuming 1 digit for length
    while (Encoding.UTF8.GetByteCount(len.ToString()) + payload.Length != len)
      len = Encoding.UTF8.GetByteCount(len.ToString()) + payload.Length;
    byte[] prefix = Encoding.UTF8.GetBytes(len.ToString());
    byte[] record = new byte[prefix.Length + payload.Length];
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
    foreach (byte b in data) {
      if (b > 127) return false;
    }
    return true;
  }
}
