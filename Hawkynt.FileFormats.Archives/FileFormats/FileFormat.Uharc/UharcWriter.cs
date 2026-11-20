using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Uharc;

/// <summary>
/// Writes UHARC 0.6 archives (docs/UHARC-ON-DISK.md, "Writing"): the stream stored or packed
/// with RLE, LZ78, LZP, PPM or ALZ, the directory tables packed with LZP method 17 and enciphered with the default
/// password, the header scrambled — the layout <c>uharc.exe</c> itself writes, without the
/// executable and multimedia filters.
/// </summary>
internal static class UharcWriter {

  /// <summary>Stream packing the writer offers.</summary>
  public enum Mode {
    /// <summary>Method 0.</summary>
    Store,
    /// <summary>Method 1.</summary>
    Rle,
    /// <summary>Method 7: 32768 phrases, UHARC's default.</summary>
    Lz78,
    /// <summary>Methods 8..23, the smallest window that holds the stream.</summary>
    Lzp,
    /// <summary>ALZ:2, methods 56..71, the smallest window that holds the stream.</summary>
    Alz,
    /// <summary>PPM, methods 24..39, the smallest window that holds the stream.</summary>
    Ppm,
  }

  /// <summary>One archive member; <see cref="Data"/> is null for a directory.</summary>
  public sealed record Item(string Name, byte[]? Data, DateTime? LastModified);

  private const byte FileAttribute = 0x20;

  /// <summary>Writes <paramref name="items"/> as one archive.</summary>
  /// <param name="output">Receives the archive.</param>
  /// <param name="items">Files and directories, names with '/' separators.</param>
  /// <param name="mode">How the stream is packed; LZP fits its dictionary to the stream.</param>
  /// <param name="now">Archive time stamp.</param>
  /// <param name="password">
  /// Enciphers the packed stream and the three tables with this password (flags <c>0x06</c>, as
  /// <c>-pw</c> does); null or empty for none. ASCII, at most 255 characters.
  /// </param>
  /// <param name="sfxStub">
  /// A Win32 self-extractor stub (UHARC's <c>UHARCSFX.EXE</c>) written in front of the archive,
  /// which then starts where the stub's last PE section ends; null for a plain archive.
  /// </param>
  /// <param name="dictionaryLimit">
  /// Largest LZP/ALZ window in bytes, as <c>-md</c> sets it (rounded up to a power of two, 1 KiB
  /// to 32 MiB); 0 fits the window to the stream.
  /// </param>
  /// <param name="version">
  /// The archive version: 6 (UHARC 0.6, every mode), 4 or 3 (UHARC 0.4 and 0.3: every mode, LZW
  /// for <see cref="Mode.Lz78"/>; 0.3's windows reach 8 MiB), or 2 (UHARC 0.2: store, LZW, and its main coder for <see cref="Mode.Alz"/>; DOS 8.3 names,
  /// upper-cased, no password).
  /// </param>
  public static void Write(Stream output, IReadOnlyList<Item> items, Mode mode, DateTime now, string? password = null, byte[]? sfxStub = null,
    long dictionaryLimit = 0, int version = 6) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(items);
    if (version is not (2 or 3 or 4 or 6))
      throw new NotSupportedException($"UHARC: the writer writes versions 0.2, 0.3, 0.4 and 0.6, not {version}.");
    if (version == 2 && mode is not (Mode.Store or Mode.Rle or Mode.Lz78 or Mode.Alz))
      throw new NotSupportedException($"UHARC: the 0.2 writer writes store, RLE, LZW and its main coder, not {mode}.");
    if (version == 2 && !string.IsNullOrEmpty(password))
      throw new NotSupportedException("UHARC: 0.2 archives have no password.");
    if (version != 6 && sfxStub != null)
      throw new NotSupportedException($"UHARC: 0.{version} has no self-extractor stub.");
    var key = PasswordBytes(password);
    if (sfxStub != null && UharcHeader.PeImageEnd(sfxStub) != sfxStub.Length)
      throw new NotSupportedException("UHARC: the self-extractor stub must be a PE image that ends with its last section.");

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
      if (version == 2)
        path = DosPath(path);
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

    // 0.3/0.4/0.6 filters: a fax page is stored as bands whenever one is present (in every mode),
    // and LZP/LZ78 delta-code recognised BMP/WAV/VOC files; the reader undoes both per file.
    byte filters = 0;
    if (version != 2 && files.Any(f => f.Data.Length == UharcFaxFilter.PageSize))
      filters |= UharcHeader.FaxFilter;
    var multimedia = version != 2 && mode is Mode.Lzp or Mode.Lz78;

    var stream = new byte[total];
    var fileEnds = new List<long>(n);
    long offset = 0;
    foreach (var f in files) {
      f.Data.CopyTo(stream, offset);
      var slice = stream.AsSpan((int)offset, f.Data.Length);
      // UHARC 0.2 undoes its executable filter on every packed MZ file, so it is applied here.
      if (version == 2 && mode != Mode.Store)
        UharcExecutableFilter.ApplyLegacy(slice);
      else if ((filters & UharcHeader.FaxFilter) != 0 && slice.Length == UharcFaxFilter.PageSize)
        UharcFaxFilter.Apply(slice);
      else if (multimedia && UharcMultimediaFilter.Apply(slice))
        filters |= UharcHeader.MultimediaFilter;
      offset += f.Data.LongLength;
      fileEnds.Add(offset);
    }

    var method = total == 0 ? UharcMethods.Store : version == 2 ? mode switch {
      Mode.Store => UharcMethods.Store,
      Mode.Rle => UharcMethods.Rle,
      Mode.Lz78 => UharcMethods.Lz78Last,
      _ => 20 + Math.Min(LzpMethodFor(total, dictionaryLimit) - UharcMethods.LzpFirst, 11),
    } : version == 3 ? mode switch {
      Mode.Store => UharcMethods.Store,
      Mode.Rle => UharcMethods.Rle,
      Mode.Lz78 => UharcMethods.Lz78Last,
      Mode.Lzp => UharcMethods.LzpFirst + Window03(total, dictionaryLimit),
      Mode.Alz => UharcMethods.Alz03First + 14 + Window03(total, dictionaryLimit),
      Mode.Ppm => UharcMethods.Ppm03First + Window03(total, dictionaryLimit),
      _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    } : mode switch {
      Mode.Store => UharcMethods.Store,
      Mode.Rle => UharcMethods.Rle,
      Mode.Lz78 => UharcMethods.Lz78Last,
      Mode.Lzp => LzpMethodFor(total, dictionaryLimit),
      Mode.Alz => AlzMethodFor(total, dictionaryLimit),
      Mode.Ppm => LzpMethodFor(total, dictionaryLimit) - UharcMethods.LzpFirst + UharcMethods.PpmFirst,
      _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    var packed = version == 2 ? method switch {
      UharcMethods.Store => stream,
      UharcMethods.Rle => UharcRleEncoder.Encode(stream, byteFirst: true),
      >= UharcMethods.Lz78First and <= UharcMethods.Lz78Last => Uharc02LzwEncoder.Encode(stream, method),
      _ => Uharc02Encoder.Encode(stream, method),
    } : version == 3 ? method switch {
      UharcMethods.Store => stream,
      UharcMethods.Rle => UharcRleEncoder.Encode(stream),
      >= UharcMethods.Lz78First and <= UharcMethods.Lz78Last => Uharc02LzwEncoder.Encode(stream, method),
      >= UharcMethods.Alz03First => Uharc03AlzEncoder.Encode(stream, method),
      >= UharcMethods.Ppm03First => Uharc03PpmEncoder.Encode(stream, method, fileEnds),
      _ => Uharc03LzpEncoder.Encode(stream, method, fileEnds),
    } : version == 4 ? method switch {
      UharcMethods.Store => stream,
      UharcMethods.Rle => UharcRleEncoder.Encode(stream),
      >= UharcMethods.Lz78First and <= UharcMethods.Lz78Last => Uharc02LzwEncoder.Encode(stream, method),
      >= UharcMethods.AlzFirst and <= UharcMethods.AlzLast => Uharc04AlzEncoder.Encode(stream, method, fileEnds),
      >= UharcMethods.PpmFirst and <= UharcMethods.PpmLast => Uharc04PpmEncoder.Encode(stream, method, fileEnds),
      _ => Uharc04LzpEncoder.Encode(stream, method, fileEnds),
    } : method switch {
      UharcMethods.Store => stream,
      UharcMethods.Rle => UharcRleEncoder.Encode(stream),
      >= UharcMethods.Lz78First and <= UharcMethods.Lz78Last => UharcLz78Encoder.Encode(stream, method),
      >= UharcMethods.AlzFirst and <= UharcMethods.AlzLast => UharcAlzEncoder.Encode(stream, method, fileEnds),
      >= UharcMethods.PpmFirst and <= UharcMethods.PpmLast => UharcPpmEncoder.Encode(stream, method, fileEnds),
      _ => UharcLzpEncoder.Encode(stream, method, fileEnds),
    };

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

    if (version == 2) {
      WriteVersion2(output, now, method, directories.Count, total, packed, crcs, nameTable, fileTable, n);
      return;
    }

    if (version != 6) {
      WriteVersion4(output, version, now, key, filters, method, directories.Count, total, packed, crcs, nameTable, fileTable, n);
      return;
    }

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
    if (key != null)
      header[8] = (byte)(UharcHeader.TablesUsePassword | UharcHeader.StreamUsesPassword);
    header[9] = filters;
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

    var tableKey = key ?? UharcCipher.DefaultPassword.ToArray();
    UharcCipher.Apply(crcs, tableKey);
    UharcCipher.Apply(packedNames, tableKey);
    UharcCipher.Apply(packedFiles, tableKey);
    if (key != null)
      UharcCipher.Apply(packed, key);

    if (sfxStub != null)
      output.Write(sfxStub);
    output.Write(header);
    output.Write(packed);
    output.Write(crcs);
    output.Write(packedNames);
    output.Write(packedFiles);
  }

  /// <summary>
  /// The LZP method whose window is the smallest power of two that holds the stream (1 KiB to
  /// 32 MiB), at most <paramref name="limit"/> rounded up (0 for no limit).
  /// </summary>
  internal static int LzpMethodFor(long size, long limit = 0) {
    var shift = 10;
    while (shift < 25 && 1L << shift < size && (limit <= 0 || 1L << shift < limit))
      ++shift;
    return UharcMethods.LzpFirst + shift - 10;
  }

  /// <summary>The ALZ:2 method for the same window as <see cref="LzpMethodFor"/>.</summary>
  internal static int AlzMethodFor(long size, long limit = 0) => LzpMethodFor(size, limit) - UharcMethods.LzpFirst + UharcMethods.AlzFirst + 16;

  private static byte[] PackTable(byte[] table) => UharcLzpEncoder.Encode(table, UharcMethods.Table, []);

  /// <summary>
  /// <paramref name="path"/> as UHARC 0.2 stores names: every component a DOS 8.3 name, upper case.
  /// </summary>
  /// <exception cref="NotSupportedException">A component is no 8.3 name.</exception>
  private static string DosPath(string path) {
    foreach (var component in path.Split('/')) {
      var dot = component.IndexOf('.');
      var stem = dot < 0 ? component : component[..dot];
      var extension = dot < 0 ? string.Empty : component[(dot + 1)..];
      if (stem.Length is 0 or > 8 || extension.Length > 3 || extension.Contains('.')
          || component.Any(c => c is < '!' or > '~' or '"' or '*' or '+' or ',' or '/' or ':' or ';' or '<' or '=' or '>' or '?' or '[' or '\\' or ']' or '|'))
        throw new NotSupportedException($"UHARC: 0.2 stores DOS 8.3 names only, not '{component}'.");
    }

    return path.ToUpperInvariant();
  }

  /// <summary>
  /// The UHARC 0.2 container (docs/UHARC-ON-DISK.md, "UHARC 0.2"): a 48-byte header, the stream,
  /// the stored CRC table, the name table as LZW method 7, the dates-then-times table as LZW method
  /// 2 and the size table (32-bit size deltas, 16-bit directory indices, attributes) as LZW
  /// method 7, each enciphered with the 0.2 table cipher.
  /// </summary>
  private static void WriteVersion2(Stream output, DateTime now, int method, int directoryCount, long total, byte[] packed,
    byte[] crcs, byte[] nameTable, byte[] fileTable, int n) {
    var times = fileTable.AsSpan(0, 4 * n).ToArray();
    var sizes = new byte[7 * n];
    for (var i = 0; i < n; ++i) {
      for (var plane = 0; plane < 4; ++plane)
        sizes[4 * i + plane] = fileTable[(6 + plane) * n + i];
      sizes[4 * n + 2 * i] = fileTable[4 * n + i];
      sizes[4 * n + 2 * i + 1] = fileTable[5 * n + i];
      sizes[6 * n + i] = fileTable[10 * n + i];
    }

    var tablesCrc = new Crc32();
    tablesCrc.Update(crcs);
    tablesCrc.Update(nameTable);
    tablesCrc.Update(times);
    tablesCrc.Update(sizes);
    var packedNames = Uharc02LzwEncoder.Encode(nameTable, 7);
    var packedTimes = Uharc02LzwEncoder.Encode(times, 2);
    var packedSizes = Uharc02LzwEncoder.Encode(sizes, 7);

    var header = new byte[Uharc02Header.Size];
    header[0] = (byte)'U';
    header[1] = (byte)'H';
    header[2] = (byte)'A';
    header[3] = Uharc02Header.Version;
    var (archiveDate, archiveTime) = ToDos(now);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), archiveTime);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), archiveDate);
    header[9] = (byte)method;
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)directoryCount);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)n);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)total);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)packed.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)nameTable.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)packedNames.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), (uint)packedTimes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), (uint)packedSizes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), tablesCrc.Value);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), Crc32.Compute(header.AsSpan(4, 40)));
    for (var i = 0; i < 40; ++i)
      header[4 + i] ^= (byte)(header[44] + i * i);

    Uharc02Cipher.Apply(crcs);
    Uharc02Cipher.Apply(packedNames);
    Uharc02Cipher.Apply(packedTimes);
    Uharc02Cipher.Apply(packedSizes);
    output.Write(header);
    output.Write(packed);
    output.Write(crcs);
    output.Write(packedNames);
    output.Write(packedTimes);
    output.Write(packedSizes);
  }

  /// <summary>The 0.3 window offset (0..13: 1 KiB to 8 MiB) that holds the stream, at most the limit.</summary>
  private static int Window03(long size, long limit) => Math.Min(LzpMethodFor(size, limit) - UharcMethods.LzpFirst, 13);

  /// <summary>
  /// The UHARC 0.4 container, which 0.3 shares byte for byte (docs/UHARC-ON-DISK.md, "UHARC 0.4"
  /// and "UHARC 0.3"): a 44-byte header, the stream, the stored CRC table, the name table as the
  /// version's LZP method 17, the dates-then-times table as LZW method 2 and the size table
  /// (directory planes, size-delta planes, attributes) as LZW method 7.
  /// </summary>
  private static void WriteVersion4(Stream output, int version, DateTime now, byte[]? key, byte filters, int method, int directoryCount, long total, byte[] packed,
    byte[] crcs, byte[] nameTable, byte[] fileTable, int n) {
    // The 0.6 file table holds the same planes: dates, times, directory, size delta, attribute.
    var dates = fileTable.AsSpan(0, 4 * n).ToArray();
    var sizes = fileTable.AsSpan(4 * n, 7 * n).ToArray();
    var packedNames = version == 3
      ? Uharc03LzpEncoder.Encode(nameTable, UharcMethods.Table, [])
      : Uharc04LzpEncoder.Encode(nameTable, UharcMethods.Table, []);
    var packedDates = Uharc02LzwEncoder.Encode(dates, 2);
    var packedSizes = Uharc02LzwEncoder.Encode(sizes, 7);
    if (packedNames.Length >= 1 << 24 || packedDates.Length >= 1 << 24 || packedSizes.Length >= 1 << 24)
      throw new NotSupportedException("UHARC: directory is too large.");

    var namesCrc = new Crc32();
    namesCrc.Update(crcs);
    namesCrc.Update(nameTable);

    var header = new byte[Uharc04Header.Size];
    header[0] = (byte)'U';
    header[1] = (byte)'H';
    header[2] = (byte)'A';
    header[3] = (byte)version;
    var (archiveDate, archiveTime) = ToDos(now);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), archiveTime);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), archiveDate);
    if (key != null)
      header[8] = Uharc04Header.UsesPassword;
    header[9] = filters;
    header[10] = (byte)method;
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(11), (ushort)directoryCount);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(13), (uint)total);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(17), (uint)packed.Length);
    WriteUInt24(header.AsSpan(21), n);
    WriteUInt24(header.AsSpan(24), nameTable.Length);
    WriteUInt24(header.AsSpan(27), packedNames.Length);
    WriteUInt24(header.AsSpan(30), packedDates.Length);
    WriteUInt24(header.AsSpan(33), packedSizes.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(36), (ushort)namesCrc.Value);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(38), (ushort)Crc32.Compute(dates));
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40), (ushort)Crc32.Compute(sizes));
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(42), (ushort)Crc32.Compute(header.AsSpan(4, 36)));
    for (var i = 0; i < 36; ++i)
      header[4 + i] ^= (byte)(header[42] + i * i);

    var tableKey = key ?? UharcCipher.DefaultPassword.ToArray();
    UharcCipher.Apply(crcs, tableKey);
    UharcCipher.Apply(packedNames, tableKey);
    UharcCipher.Apply(packedDates, tableKey);
    UharcCipher.Apply(packedSizes, tableKey);
    if (key != null)
      UharcCipher.Apply(packed, key);

    output.Write(header);
    output.Write(packed);
    output.Write(crcs);
    output.Write(packedNames);
    output.Write(packedDates);
    output.Write(packedSizes);
  }

  private static byte[]? PasswordBytes(string? password) {
    if (string.IsNullOrEmpty(password))
      return null;
    if (password.Length > byte.MaxValue || password.Any(c => c is < ' ' or > '~'))
      throw new NotSupportedException("UHARC: passwords are printable ASCII, at most 255 characters.");
    return Encoding.ASCII.GetBytes(password);
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
