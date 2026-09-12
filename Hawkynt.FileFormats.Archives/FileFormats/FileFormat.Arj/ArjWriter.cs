using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Arj;

namespace FileFormat.Arj;

/// <summary>
/// Creates ARJ archives.
/// </summary>
public sealed class ArjWriter {
  private readonly List<(string name, string comment, byte fileType, byte[] data, DateTime lastModified)> _entries = [];
  private readonly byte _method;
  private string _archiveComment = string.Empty;
  private string? _password;

  /// <summary>
  /// Initializes a new <see cref="ArjWriter"/>.
  /// </summary>
  /// <param name="method">
  /// Compression method: 0 (store), 1 (best), 2, or 3 (fastest). Default is 0 (store).
  /// </param>
  /// <param name="password">
  /// Optional password for garble (XOR) encryption. Null or empty disables encryption.
  /// </param>
  public ArjWriter(byte method = ArjConstants.MethodStore, string? password = null) {
    this._method = method;
    this._password = string.IsNullOrEmpty(password) ? null : password;
  }

  /// <summary>
  /// Gets or sets the comment embedded in the main archive header.
  /// </summary>
  public string ArchiveComment {
    get => this._archiveComment;
    set => this._archiveComment = value ?? string.Empty;
  }

  /// <summary>
  /// Adds a file entry to the archive.
  /// </summary>
  /// <param name="fileName">The file name (may include a relative path).</param>
  /// <param name="data">The raw file data.</param>
  /// <param name="lastModified">
  /// The modification timestamp. Defaults to <see cref="DateTime.Now"/> if not specified.
  /// </param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="fileName"/> or <paramref name="data"/> is <see langword="null"/>.
  /// </exception>
  public void AddFile(string fileName, byte[] data, DateTime lastModified = default) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);
    if (lastModified == default)
      lastModified = DateTime.Now;
    this._entries.Add((fileName, string.Empty, ArjConstants.FileTypeBinary, data, lastModified));
  }

  /// <summary>
  /// Adds a directory entry to the archive.
  /// </summary>
  /// <param name="dirName">The directory name (may include a relative path).</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="dirName"/> is <see langword="null"/>.
  /// </exception>
  public void AddDirectory(string dirName) {
    ArgumentNullException.ThrowIfNull(dirName);
    this._entries.Add((dirName, string.Empty, ArjConstants.FileTypeDirectory, [], DateTime.Now));
  }

  /// <summary>
  /// Writes the archive to the specified stream.
  /// </summary>
  /// <param name="output">The stream to write to.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="output"/> is <see langword="null"/>.
  /// </exception>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // Write main archive header (file type = comment/archive header).
    WriteHeader(output,
      fileName: string.Empty,
      comment: this._archiveComment,
      fileType: ArjConstants.FileTypeComment,
      method: ArjConstants.MethodStore,
      originalSize: 0,
      compressedSize: 0,
      crc32: 0,
      timestamp: MsdosTimestamp(DateTime.Now));

    // Write each entry followed by its data.
    foreach (var (name, comment, fileType, data, lastModified) in this._entries) {
      var crc32 = data.Length > 0 ? Crc32.Compute(data) : 0u;
      var method = this._method;
      byte[] writeData;

      if (method is ArjConstants.MethodCompressed1 or ArjConstants.MethodCompressed2
          or ArjConstants.MethodCompressed3 && data.Length > 0) {
        var encoder = new ArjEncoder(method);
        var compressed = encoder.Encode(data);
        if (compressed.Length < data.Length) {
          writeData = compressed;
        } else {
          writeData = data;
          method = ArjConstants.MethodStore;
        }
      } else {
        writeData = data;
        method = ArjConstants.MethodStore;
      }

      byte flags = 0;
      if (this._password != null) {
        flags |= ArjConstants.FlagGarbled;
        writeData = ApplyGarble(writeData, this._password);
      }

      WriteHeader(output,
        fileName: name,
        comment: comment,
        fileType: fileType,
        method: method,
        flags: flags,
        originalSize: (uint)data.Length,
        compressedSize: (uint)writeData.Length,
        crc32: crc32,
        timestamp: MsdosTimestamp(lastModified));

      output.Write(writeData, 0, writeData.Length);
    }

    // Write end-of-archive marker: header ID + size 0.
    WriteUInt16Le(output, ArjConstants.HeaderId);
    WriteUInt16Le(output, 0);
  }

  /// <summary>
  /// Creates the archive as a byte array.
  /// </summary>
  /// <returns>The complete ARJ archive bytes.</returns>
  public byte[] ToArray() {
    using var ms = new MemoryStream();
    this.WriteTo(ms);
    return ms.ToArray();
  }

  // -------------------------------------------------------------------------
  // Header writing
  // -------------------------------------------------------------------------

  private static void WriteHeader(
    Stream output,
    string fileName,
    string comment,
    byte fileType,
    byte method,
    uint originalSize,
    uint compressedSize,
    uint crc32,
    uint timestamp,
    byte flags = 0) {
    var nameBytes = Encoding.ASCII.GetBytes(fileName);
    var commentBytes = Encoding.ASCII.GetBytes(comment);

    // The first-header section is the fixed 30-byte block.
    // Strings (filename + NUL, comment + NUL) follow immediately after.
    const byte firstHeaderSize = ArjConstants.FirstHeaderMinSize;

    // Build the full header body that will be CRC'd.
    // Layout:
    //   [0]      firstHeaderSize  (= 30)
    //   [1]      archiverVersion
    //   [2]      minVersionToExtract
    //   [3]      hostOs
    //   [4]      arjFlags
    //   [5]      method
    //   [6]      fileType
    //   [7]      reserved
    //   [8..11]  timestamp    (uint32 LE)
    //   [12..15] compressedSize (uint32 LE)
    //   [16..19] originalSize   (uint32 LE)
    //   [20..23] crc32          (uint32 LE)
    //   [24..25] filespacePos   (uint16 LE) = 0
    //   [26..27] fileMode       (uint16 LE) = 0x20 (archive bit)
    //   [28..29] hostData       (2 bytes)   = 0
    //   [30..]   fileName\0 comment\0
    var bodyLength = firstHeaderSize + nameBytes.Length + 1 + commentBytes.Length + 1;
    var body = new byte[bodyLength];

    body[0] = firstHeaderSize;
    body[1] = ArjConstants.ArchiverVersion;
    body[2] = ArjConstants.MinVersionToExtract;
    body[3] = ArjConstants.OsDos;
    body[4] = flags;
    body[5] = method;
    body[6] = fileType;
    body[7] = 0; // reserved
    WriteUInt32Le(body, 8, timestamp);
    WriteUInt32Le(body, 12, compressedSize);
    WriteUInt32Le(body, 16, originalSize);
    WriteUInt32Le(body, 20, crc32);
    WriteUInt16Le(body, 24, 0); // filespec position
    WriteUInt16Le(body, 26, fileType == ArjConstants.FileTypeDirectory ? (ushort)0x10 : (ushort)0x20);
    body[28] = 0; // host data
    body[29] = 0;

    // Copy filename and comment (both null-terminated).
    int pos = firstHeaderSize;
    nameBytes.CopyTo(body, pos);
    pos += nameBytes.Length;
    body[pos++] = 0; // NUL terminator
    commentBytes.CopyTo(body, pos);
    pos += commentBytes.Length;
    body[pos] = 0; // NUL terminator

    // Compute header CRC-32.
    var headerCrc = Crc32.Compute(body);

    // Write to output stream:
    //   header ID (2 bytes)
    //   basic header size (2 bytes) — length of body
    //   body
    //   header CRC-32 (4 bytes)
    //   extended header size = 0 (2 bytes)
    WriteUInt16Le(output, ArjConstants.HeaderId);
    WriteUInt16Le(output, (ushort)body.Length);
    output.Write(body, 0, body.Length);
    WriteUInt32Le(output, headerCrc);
    WriteUInt16Le(output, 0); // no extended headers
  }

  // -------------------------------------------------------------------------
  // Timestamp helpers
  // -------------------------------------------------------------------------

  private static uint MsdosTimestamp(DateTime dt) {
    var time = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
    var date = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
    return (uint)((date << 16) | time);
  }

  // -------------------------------------------------------------------------
  // Low-level write helpers
  // -------------------------------------------------------------------------

  /// <summary>
  /// Applies ARJ garble (XOR) encryption/decryption. The operation is symmetric.
  /// </summary>
  internal static byte[] ApplyGarble(byte[] data, string password) {
    if (data.Length == 0 || string.IsNullOrEmpty(password))
      return data;

    var passwordBytes = Encoding.ASCII.GetBytes(password);
    var result = new byte[data.Length];
    for (var i = 0; i < data.Length; ++i)
      result[i] = (byte)(data[i] ^ passwordBytes[i % passwordBytes.Length]);
    return result;
  }

  private static void WriteUInt16Le(Stream s, ushort value) {
    s.WriteByte((byte)(value & 0xFF));
    s.WriteByte((byte)(value >> 8));
  }

  private static void WriteUInt32Le(Stream s, uint value) {
    s.WriteByte((byte)(value & 0xFF));
    s.WriteByte((byte)((value >> 8) & 0xFF));
    s.WriteByte((byte)((value >> 16) & 0xFF));
    s.WriteByte((byte)(value >> 24));
  }

  private static void WriteUInt16Le(byte[] buf, int offset, ushort value) {
    buf[offset] = (byte)(value & 0xFF);
    buf[offset + 1] = (byte)(value >> 8);
  }

  /// <summary>
  /// Creates an ARJ archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <param name="method">Compression method (0=store, 1=best, etc.).</param>
  /// <param name="password">Optional password for garble encryption.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      byte method = ArjConstants.MethodStore,
      string? password = null) {
    var writer = new ArjWriter(method, password);
    foreach (var (name, data) in entries)
      writer.AddFile(name, data);
    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(writer.ToArray(), maxVolumeSize);
  }

  private static void WriteUInt32Le(byte[] buf, int offset, uint value) {
    buf[offset] = (byte)(value & 0xFF);
    buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    buf[offset + 2] = (byte)((value >> 16) & 0xFF);
    buf[offset + 3] = (byte)(value >> 24);
  }
}
