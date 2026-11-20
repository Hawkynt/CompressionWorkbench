using System.Text;
using Compression.Core.Dictionary.Ha;

namespace FileFormat.Ha;

/// <summary>
/// Reads an HA archive (Harri Hirvola, 1993–1995): CPY, ASC and HSC members, directory records,
/// Unix special files (devices, FIFOs, sockets, symbolic links; <see cref="ReadSpecial"/>),
/// deleted records skipped. Layout and methods: docs/HA-ON-DISK.md.
/// </summary>
public sealed class HaReader : IDisposable {
  /// <summary>The only record version HA writes and reads.</summary>
  internal const int Version = 2;

  /// <summary>First byte of a record that <c>ha d</c> deleted.</summary>
  internal const byte DeletedMarker = 0xFF;

  /// <summary>The byte HA separates path components with.</summary>
  internal const byte PathSeparator = 0xFF;

  internal static readonly Encoding NameEncoding;

  static HaReader() {
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    NameEncoding = Encoding.GetEncoding(437);
  }

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<HaEntry> _entries = [];

  /// <summary>Gets the live members, in archive order.</summary>
  public IReadOnlyList<HaEntry> Entries => this._entries;

  /// <summary>Gets the member count the archive header announces.</summary>
  public int DeclaredCount { get; }

  /// <summary>Gets whether a record could not be parsed; <see cref="Entries"/> holds those before it.</summary>
  public bool IsTruncated { get; private set; }

  /// <summary>Opens an HA archive and reads its records.</summary>
  /// <param name="stream">A seekable stream positioned anywhere; the archive starts at offset 0.</param>
  /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open on dispose.</param>
  /// <exception cref="InvalidDataException">The stream does not start with the HA signature.</exception>
  public HaReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._stream = stream;
    this._leaveOpen = leaveOpen;

    stream.Position = 0;
    Span<byte> head = stackalloc byte[4];
    if (stream.Length < 4 || stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4 || head[0] != 'H' || head[1] != 'A')
      throw new InvalidDataException("Not an HA archive (missing \"HA\" signature).");
    this.DeclaredCount = head[2] | (head[3] << 8);

    long position = 4;
    while (position < stream.Length) {
      var record = TryReadRecord(stream, position, out var deleted);
      if (record == null) {
        this.IsTruncated = true;
        break;
      }

      var end = record.DataOffset + record.CompressedSize;
      if (end > stream.Length) {
        this.IsTruncated = true;
        break;
      }

      if (!deleted)
        this._entries.Add(record);
      position = end;
    }
  }

  /// <summary>Parses the record at <paramref name="offset"/>, or returns null when it is malformed or truncated.</summary>
  internal static HaEntry? TryReadRecord(Stream stream, long offset, out bool deleted) {
    deleted = false;
    stream.Position = offset;
    Span<byte> fixedPart = stackalloc byte[17];
    if (stream.ReadAtLeast(fixedPart, 17, throwOnEndOfStream: false) < 17)
      return null;

    var typeByte = fixedPart[0];
    deleted = typeByte == DeletedMarker;
    var type = typeByte & 0x0F;
    if (!deleted && ((typeByte >> 4) != Version || (type > HaEntry.MethodHsc && type != HaEntry.MethodDirectory && type != HaEntry.MethodSpecial)))
      return null;

    var path = ReadCString(stream);
    var name = path == null ? null : ReadCString(stream);
    var machineLength = name == null ? -1 : stream.ReadByte();
    if (machineLength < 0)
      return null;
    var machine = new byte[machineLength];
    if (stream.ReadAtLeast(machine, machineLength, throwOnEndOfStream: false) < machineLength)
      return null;

    var seconds = BitConverter.ToUInt32(fixedPart[13..]);
    return new HaEntry {
      FileName = ToName(path!, name!),
      Method = type,
      CompressedSize = BitConverter.ToUInt32(fixedPart[1..]),
      OriginalSize = BitConverter.ToUInt32(fixedPart[5..]),
      Crc32 = BitConverter.ToUInt32(fixedPart[9..]),
      LastModified = DateTime.UnixEpoch.AddSeconds(seconds),
      MachineInfo = machine,
      RecordOffset = offset,
      HeaderLength = 20 + path!.Length + name!.Length + machineLength,
    };
  }

  private static byte[]? ReadCString(Stream stream) {
    var bytes = new List<byte>();
    while (true) {
      var b = stream.ReadByte();
      if (b < 0)
        return null;
      if (b == 0)
        return [.. bytes];
      bytes.Add((byte)b);
    }
  }

  private static string ToName(byte[] path, byte[] name) {
    var sb = new StringBuilder();
    var start = 0;
    for (var i = 0; i <= path.Length; ++i) {
      if (i < path.Length && path[i] != PathSeparator)
        continue;
      if (i > start)
        sb.Append(NameEncoding.GetString(path, start, i - start)).Append('/');
      start = i + 1;
    }

    return sb.Append(NameEncoding.GetString(name)).ToString();
  }

  /// <summary>Decodes a member and checks its CRC-32.</summary>
  /// <param name="entry">A member of this archive.</param>
  /// <returns>The member's bytes (empty for a directory).</returns>
  /// <exception cref="NotSupportedException">The record is a Unix special file; read it with <see cref="ReadSpecial"/>.</exception>
  /// <exception cref="InvalidDataException">The data is corrupt or its CRC-32 does not match.</exception>
  public byte[] Extract(HaEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      return [];
    if (entry.IsSpecial)
      throw new NotSupportedException($"HA special-file record '{entry.FileName}' (device, FIFO, socket or link) has no file content; use ReadSpecial.");
    if (entry.OriginalSize > Array.MaxLength)
      throw new NotSupportedException($"HA member '{entry.FileName}' is larger than a single buffer.");

    this._stream.Position = entry.DataOffset;
    var stored = new byte[entry.CompressedSize];
    this._stream.ReadExactly(stored);

    var length = (int)entry.OriginalSize;
    var data = length == 0 ? [] : entry.Method switch {
      HaEntry.MethodCpy when stored.Length == length => stored,
      HaEntry.MethodCpy => throw new InvalidDataException($"HA stored member '{entry.FileName}' holds {stored.Length} bytes, not {length}."),
      HaEntry.MethodAsc => HaAsc.Decode(stored, length),
      _ => HaHsc.Decode(stored, length),
    };

    var crc = Compression.Core.Checksums.Crc32.Compute(data);
    if (crc != entry.Crc32)
      throw new InvalidDataException($"CRC-32 mismatch for HA member '{entry.FileName}': stored 0x{entry.Crc32:X8}, computed 0x{crc:X8}.");
    return data;
  }

  /// <summary>Largest special-file payload accepted; a link text is at most a path, a device number 8 bytes.</summary>
  internal const int MaxSpecialLength = 65536;

  /// <summary>Reads and decodes a Unix special-file record (docs/HA-ON-DISK.md §1.4).</summary>
  /// <param name="entry">A special-file member of this archive.</param>
  /// <returns>The record's kind and decoded content.</returns>
  /// <exception cref="ArgumentException"><paramref name="entry"/> is not a special-file record.</exception>
  /// <exception cref="InvalidDataException">The stored bytes do not have the shape the record's kind requires.</exception>
  public HaSpecialFile ReadSpecial(HaEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (!entry.IsSpecial)
      throw new ArgumentException($"HA member '{entry.FileName}' is not a special-file record.", nameof(entry));
    if (entry.CompressedSize != entry.OriginalSize || entry.CompressedSize > MaxSpecialLength)
      throw new InvalidDataException($"HA special-file record '{entry.FileName}' stores {entry.CompressedSize} bytes for {entry.OriginalSize}.");

    this._stream.Position = entry.DataOffset;
    var data = new byte[entry.CompressedSize];
    this._stream.ReadExactly(data);

    var kind = entry.SpecialKind!.Value;
    switch (kind) {
      case HaSpecialKind.SymbolicLink:
        if (data.Length < 2 || data[^1] != 0 || Array.IndexOf(data, (byte)0) != data.Length - 1)
          throw new InvalidDataException($"HA symbolic link '{entry.FileName}' does not hold a non-empty NUL-terminated link text.");
        return new(kind, data, LinkTarget: NameEncoding.GetString(data, 0, data.Length - 1));

      case HaSpecialKind.CharacterDevice or HaSpecialKind.BlockDevice:
        if (data.Length is not (2 or 4 or 8))
          throw new InvalidDataException($"HA device '{entry.FileName}' stores a {data.Length}-byte device number.");
        ulong number = 0;
        for (var i = data.Length - 1; i >= 0; --i)
          number = (number << 8) | data[i];
        if (data.Length != 8)
          return new(kind, data, DeviceNumber: number);
        // Linux (glibc) dev_t: minor bits 0-7 and 20-43, major bits 8-19 and 44-63.
        var major = (uint)(((number >> 8) & 0xFFF) | ((number >> 32) & 0xFFFFF000));
        var minor = (uint)((number & 0xFF) | ((number >> 12) & 0xFFFFFF00));
        return new(kind, data, DeviceNumber: number, DeviceMajor: major, DeviceMinor: minor);

      case HaSpecialKind.Fifo or HaSpecialKind.Socket:
        if (data.Length != 0)
          throw new InvalidDataException($"HA {(kind == HaSpecialKind.Fifo ? "FIFO" : "socket")} '{entry.FileName}' stores {data.Length} bytes; it should store none.");
        return new(kind, data);

      default:
        return new(kind, data);
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
