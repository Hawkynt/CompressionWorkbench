using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Uharc;

/// <summary>
/// Writes UHARC 0.6 archives (docs/UHARC-ON-DISK.md, "Writing"): the stream stored or packed
/// with LZP, the directory tables packed with LZP method 17 and enciphered with the default
/// password, the header scrambled — the layout <c>uharc.exe</c> itself writes, without the
/// executable and multimedia filters.
/// </summary>
internal static class UharcWriter {

  /// <summary>One archive member; <see cref="Data"/> is null for a directory.</summary>
  public sealed record Item(string Name, byte[]? Data, DateTime? LastModified);

  private const byte FileAttribute = 0x20;

  /// <summary>Writes <paramref name="items"/> as one archive.</summary>
  /// <param name="output">Receives the archive.</param>
  /// <param name="items">Files and directories, names with '/' separators.</param>
  /// <param name="compress">LZP-pack the stream (the dictionary fitted to its size) rather than store it.</param>
  /// <param name="now">Archive time stamp.</param>
  public static void Write(Stream output, IReadOnlyList<Item> items, bool compress, DateTime now) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(items);

    var directories = new List<string> { string.Empty };
    var directoryIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = 0 };
    void AddDirectory(string directory) {
      if (directory.Length == 0 || directoryIndex.ContainsKey(directory))
        return;
      var slash = directory.LastIndexOf('/');
      if (slash > 0)
        AddDirectory(directory[..slash]);
      directoryIndex[directory] = directories.Count;
      directories.Add(directory);
    }

    var files = new List<(string Name, int Directory, byte[] Data, DateTime? Modified)>();
    foreach (var item in items) {
      var path = Normalize(item.Name);
      if (path.Length == 0)
        continue;
      if (item.Data == null) {
        AddDirectory(path);
        continue;
      }

      var slash = path.LastIndexOf('/');
      var directory = slash > 0 ? path[..slash] : string.Empty;
      AddDirectory(directory);
      files.Add((path[(slash + 1)..], directoryIndex[directory], item.Data, item.LastModified));
    }

    var n = files.Count;
    long total = 0;
    foreach (var f in files)
      total += f.Data.LongLength;
    if (total > uint.MaxValue || total > Array.MaxLength)
      throw new NotSupportedException("UHARC: archives hold at most 4 GiB.");
    if (n >= 1 << 24)
      throw new NotSupportedException("UHARC: too many files.");

    var stream = new byte[total];
    var fileEnds = new List<long>(n);
    long offset = 0;
    foreach (var f in files) {
      f.Data.CopyTo(stream, offset);
      offset += f.Data.LongLength;
      fileEnds.Add(offset);
    }

    var method = compress && total > 0 ? LzpMethodFor(total) : UharcMethods.Store;
    var packed = method == UharcMethods.Store ? stream : UharcLzpEncoder.Encode(stream, method, fileEnds);

    // CRC table, name table, file table.
    var crcs = new byte[4 * n];
    for (var i = 0; i < n; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(crcs.AsSpan(4 * i), Crc32.Compute(files[i].Data));

    var names = new MemoryStream();
    foreach (var directory in directories)
      WriteName(names, directory.Length == 0 ? string.Empty : directory.Replace('/', '\\') + "\\");
    foreach (var f in files)
      WriteName(names, f.Name);
    var nameTable = names.ToArray();

    var fileTable = new byte[11 * n];
    uint previous = 0;
    for (var i = 0; i < n; ++i) {
      var (date, time) = ToDos(files[i].Modified ?? now);
      BinaryPrimitives.WriteUInt16LittleEndian(fileTable.AsSpan(2 * i), date);
      BinaryPrimitives.WriteUInt16LittleEndian(fileTable.AsSpan(2 * n + 2 * i), time);
      fileTable[4 * n + i] = (byte)files[i].Directory;
      fileTable[5 * n + i] = (byte)(files[i].Directory >> 8);
      var size = (uint)files[i].Data.Length;
      var delta = previous - size;
      for (var plane = 0; plane < 4; ++plane)
        fileTable[(6 + plane) * n + i] = (byte)(delta >> (8 * plane));
      fileTable[10 * n + i] = FileAttribute;
      previous = size;
    }

    if (directories.Count > ushort.MaxValue || nameTable.Length >= 1 << 24)
      throw new NotSupportedException("UHARC: directory is too large.");

    var packedNames = PackTable(nameTable);
    var packedFiles = PackTable(fileTable);
    if (packedNames.Length >= 1 << 24 || packedFiles.Length >= 1 << 24)
      throw new NotSupportedException("UHARC: directory is too large.");

    var namesCrc = new Crc32();
    namesCrc.Update(crcs);
    namesCrc.Update(nameTable);

    var header = new byte[UharcHeader.Size];
    header[0] = (byte)'U';
    header[1] = (byte)'H';
    header[2] = (byte)'A';
    header[3] = UharcHeader.Version;
    var (archiveDate, archiveTime) = ToDos(now);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), archiveTime);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), archiveDate);
    header[10] = (byte)method;
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(11), (ushort)directories.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(13), (uint)total);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(17), (uint)packed.Length);
    WriteUInt24(header.AsSpan(21), n);
    WriteUInt24(header.AsSpan(24), nameTable.Length);
    WriteUInt24(header.AsSpan(27), packedNames.Length);
    WriteUInt24(header.AsSpan(30), packedFiles.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(33), (ushort)namesCrc.Value);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(35), (ushort)Crc32.Compute(fileTable));
    var check = (ushort)Crc32.Compute(header.AsSpan(UharcHeader.ScrambledOffset, UharcHeader.ScrambledLength));
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(37), check);
    UharcCipher.DescrambleHeader(header, header[37]);

    UharcCipher.Apply(crcs, UharcCipher.DefaultPassword);
    output.Write(header);
    output.Write(packed);
    output.Write(crcs);
    output.Write(packedNames);
    output.Write(packedFiles);
  }

  /// <summary>The LZP method whose window is the smallest power of two that holds the stream (1 KiB to 32 MiB).</summary>
  internal static int LzpMethodFor(long size) {
    var shift = 10;
    while (shift < 25 && 1L << shift < size)
      ++shift;
    return UharcMethods.LzpFirst + shift - 10;
  }

  private static byte[] PackTable(byte[] table) {
    var packed = UharcLzpEncoder.Encode(table, UharcMethods.Table, []);
    UharcCipher.Apply(packed, UharcCipher.DefaultPassword);
    return packed;
  }

  private static string Normalize(string name) {
    var path = name.Replace('\\', '/').Trim('/');
    while (path.Contains("//", StringComparison.Ordinal))
      path = path.Replace("//", "/", StringComparison.Ordinal);
    return path;
  }

  private static void WriteName(Stream table, string name) {
    var bytes = Encoding.Latin1.GetBytes(name);
    if (bytes.Length > byte.MaxValue)
      throw new NotSupportedException($"UHARC: name '{name}' is longer than 255 bytes.");
    table.WriteByte((byte)bytes.Length);
    table.Write(bytes);
  }

  private static void WriteUInt24(Span<byte> target, int value) {
    target[0] = (byte)value;
    target[1] = (byte)(value >> 8);
    target[2] = (byte)(value >> 16);
  }

  /// <summary>DOS date and time of <paramref name="value"/> in local time, clamped to 1980..2107.</summary>
  internal static (ushort Date, ushort Time) ToDos(DateTime value) {
    var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
    if (local.Year < 1980)
      local = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
    if (local.Year > 2107)
      local = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
    var date = (ushort)((local.Year - 1980) << 9 | local.Month << 5 | local.Day);
    var time = (ushort)(local.Hour << 11 | local.Minute << 5 | local.Second / 2);
    return (date, time);
  }
}
