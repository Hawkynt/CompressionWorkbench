// Directory parsing converted from XADMaster (https://github.com/MacPaw/XADMaster, XADCompactProParser.m,
// LGPL-2.1-or-later, Copyright (c) 2017-present MacPaw Way Ltd.). See THIRD-PARTY-NOTICE.XADMaster.txt.

using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.CompactPro;

/// <summary>
/// Reads Compact Pro (.cpt) archives: an 8-byte header (0x01, volume, magic, directory offset), the
/// packed forks, then the directory — a CRC-32 over the rest of the directory, the entry count, an
/// optional comment and the entries (folders carry the number of entries nested in them).
/// </summary>
public sealed class CompactProReader {
  private readonly Stream _stream;
  private readonly List<CompactProEntry> _entries = [];

  public IReadOnlyList<CompactProEntry> Entries => this._entries;

  public string? Comment { get; }

  public static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  internal static Encoding MacRoman {
    get {
      Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
      return Encoding.GetEncoding(10000);
    }
  }

  /// <summary>The archive CRC convention: the IEEE CRC-32 register without the final inversion.</summary>
  internal static uint Crc(ReadOnlySpan<byte> data) => ~Compression.Core.Checksums.Crc32.Compute(data);

  public CompactProReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    var length = stream.Length;
    if (length < 8)
      throw new InvalidDataException("Too short for a Compact Pro archive.");
    stream.Position = 0;
    Span<byte> head = stackalloc byte[8];
    stream.ReadExactly(head);
    if (head[0] != 1)
      throw new InvalidDataException("Not a Compact Pro archive (first byte is not 1).");
    var offset = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
    if (offset < 8 || offset + 7 > length)
      throw new InvalidDataException("Compact Pro directory offset lies outside the file.");

    var dir = new byte[length - offset];
    stream.Position = offset;
    stream.ReadExactly(dir);
    var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(dir);
    var numEntries = BinaryPrimitives.ReadUInt16BigEndian(dir.AsSpan(4));
    int commentLength = dir[6];
    var pos = 7;
    if (pos + commentLength > dir.Length)
      throw new InvalidDataException("Truncated Compact Pro comment.");
    if (commentLength > 0)
      this.Comment = MacRoman.GetString(dir, pos, commentLength);
    pos += commentLength;

    var end = this.ParseDirectory(dir, ref pos, "", numEntries);
    if (Crc(dir.AsSpan(4, end - 4)) != storedCrc)
      throw new InvalidDataException("Compact Pro directory CRC mismatch.");
  }

  private int ParseDirectory(byte[] dir, ref int pos, string parent, int numEntries) {
    while (numEntries > 0) {
      if (pos >= dir.Length)
        throw new InvalidDataException("Truncated Compact Pro directory.");
      var nameLength = dir[pos++];
      var nameSize = nameLength & 0x7F;
      if (pos + nameSize > dir.Length)
        throw new InvalidDataException("Truncated Compact Pro entry name.");
      var name = MacRoman.GetString(dir, pos, nameSize).Replace('/', ':');
      pos += nameSize;
      var path = parent.Length == 0 ? name : parent + "/" + name;

      if ((nameLength & 0x80) != 0) {
        if (pos + 2 > dir.Length)
          throw new InvalidDataException("Truncated Compact Pro folder.");
        var nested = BinaryPrimitives.ReadUInt16BigEndian(dir.AsSpan(pos));
        pos += 2;
        if (nested + 1 > numEntries)
          throw new InvalidDataException("Compact Pro folder claims more entries than remain.");
        this._entries.Add(new CompactProEntry { FileName = path, IsDirectory = true });
        this.ParseDirectory(dir, ref pos, path, nested);
        numEntries -= nested + 1;
        continue;
      }

      if (pos + 45 > dir.Length)
        throw new InvalidDataException("Truncated Compact Pro file entry.");
      var s = dir.AsSpan(pos);
      var entry = new CompactProEntry {
        FileName = path,
        Volume = s[0],
        FileOffset = BinaryPrimitives.ReadUInt32BigEndian(s[1..]),
        FileType = BinaryPrimitives.ReadUInt32BigEndian(s[5..]),
        FileCreator = BinaryPrimitives.ReadUInt32BigEndian(s[9..]),
        CreatedDate = MacEpoch.AddSeconds(BinaryPrimitives.ReadUInt32BigEndian(s[13..])),
        ModifiedDate = MacEpoch.AddSeconds(BinaryPrimitives.ReadUInt32BigEndian(s[17..])),
        FinderFlags = BinaryPrimitives.ReadUInt16BigEndian(s[21..]),
        Crc = BinaryPrimitives.ReadUInt32BigEndian(s[23..]),
        Flags = BinaryPrimitives.ReadUInt16BigEndian(s[27..]),
        ResourceForkSize = BinaryPrimitives.ReadUInt32BigEndian(s[29..]),
        DataForkSize = BinaryPrimitives.ReadUInt32BigEndian(s[33..]),
        ResourceForkCompressedSize = BinaryPrimitives.ReadUInt32BigEndian(s[37..]),
        DataForkCompressedSize = BinaryPrimitives.ReadUInt32BigEndian(s[41..]),
      };
      pos += 45;
      if ((long)entry.FileOffset + entry.ResourceForkCompressedSize + entry.DataForkCompressedSize > this._stream.Length)
        throw new InvalidDataException($"Compact Pro entry '{path}' overruns the archive.");
      this._entries.Add(entry);
      --numEntries;
    }
    return pos;
  }

  private byte[] Read(long offset, uint size) {
    var buf = new byte[size];
    this._stream.Position = offset;
    this._stream.ReadExactly(buf);
    return buf;
  }

  /// <summary>Decodes both forks and verifies the entry's CRC (computed over resource, then data fork).</summary>
  /// <param name="entry">The file to extract.</param>
  /// <param name="password">The password of an encrypted entry; ignored for the others.</param>
  /// <exception cref="ArchivePasswordException">
  /// The entry is encrypted and no password was given, or the password is wrong. Compact Pro stores
  /// no password check, so a wrong password shows only as a stream that does not decode to the
  /// entry's CRC (which a damaged entry would also do).
  /// </exception>
  public (byte[] Data, byte[] Resource) ExtractForks(CompactProEntry entry, string? password = null) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      return ([], []);
    var packedResource = entry.ResourceForkCompressedSize == 0 ? [] : this.Read(entry.ResourceOffset, entry.ResourceForkCompressedSize);
    var packedData = entry.DataForkCompressedSize == 0 ? [] : this.Read(entry.DataOffset, entry.DataForkCompressedSize);
    if (!entry.IsEncrypted)
      return this.Decode(entry, packedResource, packedData);

    if (password == null)
      throw new ArchivePasswordException($"Compact Pro entry '{entry.FileName}' is encrypted; give its password.", passwordWasSupplied: false);
    // One register runs over the resource fork and then the data fork; the first non-empty fork
    // opens with the preamble, which is decrypted (it advances the register) and dropped.
    var cipher = new CompactProCipher(MacRoman.GetBytes(password));
    cipher.Decrypt(packedResource);
    cipher.Decrypt(packedData);
    if (packedResource.Length > 0)
      packedResource = packedResource.Length < CompactProCipher.PreambleLength ? throw WrongPassword(entry) : packedResource[CompactProCipher.PreambleLength..];
    else
      packedData = packedData.Length < CompactProCipher.PreambleLength ? throw WrongPassword(entry) : packedData[CompactProCipher.PreambleLength..];
    try {
      return this.Decode(entry, packedResource, packedData);
    } catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException) {
      throw WrongPassword(entry);
    }
  }

  private static ArchivePasswordException WrongPassword(CompactProEntry entry)
    => new($"Compact Pro entry '{entry.FileName}': the password is wrong (or the entry is damaged).", passwordWasSupplied: true);

  private (byte[] Data, byte[] Resource) Decode(CompactProEntry entry, byte[] packedResource, byte[] packedData) {
    var resource = packedResource.Length == 0 ? [] : CompactProCodec.DecodeFork(packedResource, entry.ResourceForkSize, entry.ResourceForkLzh);
    var data = packedData.Length == 0 ? [] : CompactProCodec.DecodeFork(packedData, entry.DataForkSize, entry.DataForkLzh);
    if (resource.Length != entry.ResourceForkSize || data.Length != entry.DataForkSize)
      throw new InvalidDataException($"Compact Pro entry '{entry.FileName}': fork size mismatch.");
    var all = new byte[resource.Length + data.Length];
    resource.CopyTo(all, 0);
    data.CopyTo(all, resource.Length);
    if (Crc(all) != entry.Crc)
      throw new InvalidDataException($"Compact Pro entry '{entry.FileName}': CRC mismatch.");
    return (data, resource);
  }
}
