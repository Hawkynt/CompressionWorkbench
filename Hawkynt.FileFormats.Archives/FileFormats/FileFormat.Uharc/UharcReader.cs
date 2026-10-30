using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Uharc;

/// <summary>
/// Reads UHARC 0.6 archives (magic <c>UHA\x06</c>) as written by UHARC 0.6/0.6a/0.6b.
/// Layout and codecs are described in docs/UHARC-ON-DISK.md.
/// </summary>
/// <remarks>
/// The directory is always readable; file data decodes for the store, LZP and ALZ methods.
/// PPM streams (<c>-mx</c>) and the legacy RLE/LZ78 modes raise
/// <see cref="NotSupportedException"/> rather than producing unverified bytes; every extracted
/// file is checked against its stored CRC-32.
/// </remarks>
public sealed class UharcReader {

  private readonly byte[] _archive;
  private readonly int _base;
  private readonly UharcHeader _header;
  private readonly byte[]? _password;
  private readonly List<UharcEntry> _entries = [];
  private readonly List<long> _fileEnds = [];
  private byte[]? _stream;

  /// <summary>Key in <see cref="Exception.Data"/> that marks an <see cref="InvalidDataException"/> as "wrong password".</summary>
  public const string WrongPasswordKey = "Uharc.WrongPassword";

  private static InvalidDataException WrongPassword() {
    var e = new InvalidDataException("UHARC: wrong password or corrupt directory.");
    e.Data[WrongPasswordKey] = true;
    return e;
  }

  /// <summary>Gets the entries of the archive in stored order.</summary>
  public IReadOnlyList<UharcEntry> Entries => this._entries;

  /// <summary>Gets the stream method byte (see <see cref="MethodName"/>).</summary>
  public int Method => this._header.Method;

  /// <summary>Gets the name of the compression mode, as <c>uharc l</c> prints it.</summary>
  public string MethodName => UharcMethods.Name(this._header.Method);

  /// <summary>Gets the dictionary size in bytes, 0 for the store method.</summary>
  public int DictionarySize => UharcMethods.DictionarySize(this._header.Method);

  /// <summary>Gets whether the archive was created with <c>-pw</c>.</summary>
  public bool IsEncrypted => (this._header.Flags & (UharcHeader.TablesUsePassword | UharcHeader.StreamUsesPassword)) != 0;

  /// <summary>Gets the size of the packed solid stream.</summary>
  public long PackedSize => this._header.PackedSize;

  /// <summary>Gets whether the stream's method can be decoded by this reader.</summary>
  public bool CanDecodeMethod => this._header.Method is UharcMethods.Store
    or >= UharcMethods.LzpFirst and <= UharcMethods.LzpLast
    or >= UharcMethods.AlzFirst and <= UharcMethods.AlzLast;

  /// <summary>Opens a UHARC 0.6 archive (optionally behind a self-extractor stub).</summary>
  /// <param name="stream">The archive; read completely into memory.</param>
  /// <param name="password">The <c>-pw</c> password, if the archive has one.</param>
  /// <exception cref="InvalidDataException">The data is not a well-formed UHARC 0.6 archive.</exception>
  /// <exception cref="InvalidOperationException">The directory is password protected and no password was given.</exception>
  /// <exception cref="InvalidDataException">Also thrown for a wrong password.</exception>
  public UharcReader(Stream stream, string? password = null) {
    ArgumentNullException.ThrowIfNull(stream);
    using var copy = new MemoryStream();
    if (stream.CanSeek)
      stream.Position = 0;
    stream.CopyTo(copy);
    this._archive = copy.ToArray();
    this._base = FindArchiveStart(this._archive);
    this._header = UharcHeader.Parse(this._archive.AsSpan(this._base));
    this._password = string.IsNullOrEmpty(password) ? null : Encoding.Latin1.GetBytes(password);
    if ((this._header.Flags & UharcHeader.TablesUsePassword) != 0 && this._password == null)
      throw new InvalidOperationException("UHARC: the archive is password protected; a password is required.");

    this.ReadDirectory();
  }

  /// <summary>
  /// Locates the archive: offset 0 for a plain archive, or the <c>UHA\x06</c> header that
  /// follows the last PE section of a self-extractor stub.
  /// </summary>
  private static int FindArchiveStart(byte[] data) {
    if (UharcHeader.HasMagic(data) || data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z')
      return 0;

    var pe = BitConverter.ToInt32(data, 0x3C);
    if (pe <= 0 || pe > data.Length - 24 || data[pe] != 'P' || data[pe + 1] != 'E')
      return 0;

    var sections = BitConverter.ToUInt16(data, pe + 6);
    var optional = BitConverter.ToUInt16(data, pe + 20);
    var table = pe + 24 + optional;
    var end = 0L;
    for (var i = 0; i < sections; ++i) {
      var entry = table + i * 40;
      if (entry + 40 > data.Length)
        return 0;
      end = Math.Max(end, (long)BitConverter.ToUInt32(data, entry + 20) + BitConverter.ToUInt32(data, entry + 16));
    }

    return end > 0 && end < data.Length && UharcHeader.HasMagic(data.AsSpan((int)end)) ? (int)end : 0;
  }

  private ReadOnlySpan<byte> TablePassword
    => (this._header.Flags & UharcHeader.TablesUsePassword) != 0 ? this._password : UharcCipher.DefaultPassword;

  private byte[] ReadTable(ref int offset, int length, int unpackedSize, bool compressed) {
    if (length < 0 || offset > this._archive.Length - length)
      throw new InvalidDataException("UHARC: archive directory is truncated.");

    var table = this._archive.AsSpan(offset, length).ToArray();
    offset += length;
    UharcCipher.Apply(table, this.TablePassword);
    if (!compressed)
      return table;

    try {
      return UharcLzpDecoder.Decode(table, 0, table.Length, UharcMethods.Table, unpackedSize, []);
    } catch (InvalidDataException) when ((this._header.Flags & UharcHeader.TablesUsePassword) != 0) {
      // A wrong password turns the table into noise long before its check can be read.
      throw WrongPassword();
    }
  }

  private void ReadDirectory() {
    var h = this._header;
    var n = h.FileCount;
    var offset = this._base + UharcHeader.Size;
    if (h.PackedSize > this._archive.Length - offset)
      throw new InvalidDataException("UHARC: packed stream is truncated.");

    offset += (int)h.PackedSize;
    var crcs = this.ReadTable(ref offset, 4 * n, 4 * n, compressed: false);
    var names = this.ReadTable(ref offset, h.NamesPackedSize, h.NamesSize, compressed: true);
    var tableCrc = new Crc32();
    tableCrc.Update(crcs);
    tableCrc.Update(names);
    if ((ushort)tableCrc.Value != h.CrcTable)
      throw (h.Flags & UharcHeader.TablesUsePassword) != 0
        ? WrongPassword()
        : new InvalidDataException("UHARC: name table check failed.");

    var meta = this.ReadTable(ref offset, h.MetadataPackedSize, 11 * n, compressed: true);
    if ((ushort)Crc32.Compute(meta) != h.MetadataCrc)
      throw new InvalidDataException("UHARC: file table check failed.");

    var position = 0;
    var directories = new string[h.DirectoryCount];
    for (var i = 0; i < directories.Length; ++i)
      directories[i] = ReadName(names, ref position);

    var emitted = new HashSet<string>(StringComparer.Ordinal);
    foreach (var directory in directories)
      this.AddDirectory(directory, emitted);

    long size = 0, streamOffset = 0;
    for (var i = 0; i < n; ++i) {
      var name = ReadName(names, ref position);
      var date = (ushort)(meta[2 * i] | meta[2 * i + 1] << 8);
      var time = (ushort)(meta[2 * n + 2 * i] | meta[2 * n + 2 * i + 1] << 8);
      var directory = meta[4 * n + i] | meta[5 * n + i] << 8;
      var delta = (uint)(meta[6 * n + i] | meta[7 * n + i] << 8 | meta[8 * n + i] << 16 | meta[9 * n + i] << 24);
      var attributes = meta[10 * n + i];
      size = (uint)(size - delta);
      if (directory >= directories.Length)
        throw new InvalidDataException("UHARC: file refers to a missing directory.");

      var prefix = directories[directory];
      this._entries.Add(new UharcEntry {
        FileName = prefix.Length == 0 ? name : prefix + "/" + name,
        Size = size,
        Crc32 = BitConverter.ToUInt32(crcs, 4 * i),
        LastModified = UharcEntry.FromDos(date, time),
        Attributes = attributes,
        StreamOffset = streamOffset,
      });
      streamOffset += size;
      this._fileEnds.Add(streamOffset);
    }

    if (streamOffset != h.UnpackedSize)
      throw new InvalidDataException("UHARC: file sizes do not add up to the stream size.");
  }

  private void AddDirectory(string directory, HashSet<string> emitted) {
    if (directory.Length == 0)
      return;

    var slash = directory.LastIndexOf('/');
    if (slash > 0)
      this.AddDirectory(directory[..slash], emitted);
    if (emitted.Add(directory))
      this._entries.Add(new UharcEntry { FileName = directory, IsDirectory = true });
  }

  private static string ReadName(byte[] table, ref int position) {
    if (position >= table.Length)
      throw new InvalidDataException("UHARC: name table is truncated.");

    var length = table[position++];
    if (length > table.Length - position)
      throw new InvalidDataException("UHARC: name table is truncated.");

    var name = Encoding.Latin1.GetString(table, position, length).Replace('\\', '/').Trim('/');
    position += length;
    return name;
  }

  /// <summary>Extracts and CRC-verifies one file.</summary>
  /// <exception cref="NotSupportedException">The archive's compression mode is not supported.</exception>
  /// <exception cref="InvalidDataException">The data is corrupt or fails its CRC.</exception>
  public byte[] Extract(UharcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory || entry.Size == 0)
      return [];

    var stream = this.DecodeStream();
    var data = stream.AsSpan((int)entry.StreamOffset, (int)entry.Size).ToArray();
    if (this._header.Method != UharcMethods.Store) {
      if ((this._header.Filters & UharcHeader.ExecutableFilter) != 0)
        UharcExecutableFilter.Restore(data);
      if ((this._header.Filters & UharcHeader.MultimediaFilter) != 0)
        UharcMultimediaFilter.Restore(data);
    }

    if (Crc32.Compute(data) != entry.Crc32)
      throw new InvalidDataException($"UHARC: CRC mismatch in '{entry.FileName}'.");

    return data;
  }

  private byte[] DecodeStream() {
    if (this._stream != null)
      return this._stream;

    var h = this._header;
    if (!this.CanDecodeMethod)
      throw new NotSupportedException($"UHARC: {UharcMethods.Name(h.Method)} streams (method {h.Method}) are not supported.");
    if (h.UnpackedSize > Array.MaxLength)
      throw new NotSupportedException("UHARC: archive is too large to unpack in memory.");

    var offset = this._base + UharcHeader.Size;
    var length = (int)h.PackedSize;
    var packed = this._archive;
    if ((h.Flags & UharcHeader.StreamUsesPassword) != 0) {
      if (this._password == null)
        throw new InvalidOperationException("UHARC: the archive data is password protected; a password is required.");

      packed = this._archive.AsSpan(offset, length).ToArray();
      UharcCipher.Apply(packed, this._password);
      offset = 0;
    }

    var size = (int)h.UnpackedSize;
    try {
      this._stream = h.Method switch {
        UharcMethods.Store when length == size => packed.AsSpan(offset, size).ToArray(),
        UharcMethods.Store => throw new InvalidDataException("UHARC: stored stream size does not match."),
        >= UharcMethods.LzpFirst and <= UharcMethods.LzpLast => UharcLzpDecoder.Decode(packed, offset, length, h.Method, size, this._fileEnds),
        _ => UharcAlzDecoder.Decode(packed, offset, length, h.Method, size, this._fileEnds),
      };
    } catch (IndexOutOfRangeException e) {
      // Only a damaged stream can steer the models outside their tables.
      throw new InvalidDataException("UHARC: packed stream is corrupt.", e);
    }

    return this._stream;
  }
}
