#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Squeeze;

namespace FileFormat.BinaryII;

internal static class BinaryIIConstants {
  public const int HeaderSize = 128;
  public const int Alignment = 128;
  public const int MaxRecords = 256;
  public const int MaxNameLength = 64;

  public const byte ProDosAccessDefault = 0xE3;
  public const byte ProDosFileTypeBinary = 0x06;
  public const byte ProDosFileTypeDirectory = 0x0F;
  public const byte ProDosStorageSeedling = 0x01;
  public const byte ProDosStorageSapling = 0x02;
  public const byte ProDosStorageTree = 0x03;
  public const byte ProDosStorageDirectory = 0x0D;

  public const byte DataFlagSparse = 0x01;
  public const byte DataFlagEncrypted = 0x40;
  public const byte DataFlagCompressed = 0x80;

  public static int RoundUp128(int value) => checked((value + 127) & ~127);
}

internal sealed record BinaryIIRecord(
  string Name,
  bool IsDirectory,
  bool IsPhantom,
  bool IsCompressed,
  bool IsEncrypted,
  bool IsSparse,
  byte FileType,
  byte StorageType,
  byte DataFlags,
  int StoredLength,
  long HeaderOffset,
  long DataOffset,
  int PhysicalLength,
  byte FilesToFollow
);

internal enum BinaryIICompressionMode {
  Stored,
  Squeeze,
  Auto,
}

internal sealed record BinaryIIWriteRecord(
  string Name,
  bool IsDirectory,
  byte[] Data,
  bool Compress
);

internal sealed class BinaryIIReader {
  private readonly byte[] _data;
  private readonly List<BinaryIIRecord> _records = [];

  public IReadOnlyList<BinaryIIRecord> PhysicalRecords => this._records;
  public IEnumerable<BinaryIIRecord> Entries => this._records.Where(r => !r.IsPhantom);

  public BinaryIIReader(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    if (input.CanSeek) input.Position = 0;
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  public byte[] Extract(BinaryIIRecord entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory)
      return [];
    if (entry.IsEncrypted)
      throw new NotSupportedException($"Binary II entry '{entry.Name}' is encrypted; the format never standardized an encryption method.");
    if (entry.IsSparse)
      throw new NotSupportedException($"Binary II entry '{entry.Name}' is marked sparse; the Binary II specification does not define sparse reconstruction semantics.");

    var stored = this._data.AsSpan((int)entry.DataOffset, entry.StoredLength).ToArray();
    if (!entry.IsCompressed)
      return stored;

    using var src = new MemoryStream(stored, writable: false);
    using var dst = new MemoryStream();
    SqueezeStream.Decompress(src, dst);
    return dst.ToArray();
  }

  private void Parse() {
    if (this._data.Length == 0)
      return;
    if (this._data.Length < BinaryIIConstants.HeaderSize)
      throw new InvalidDataException("Binary II: archive is shorter than one 128-byte record header.");

    var offset = 0;
    for (var recordIndex = 0; recordIndex < BinaryIIConstants.MaxRecords; recordIndex++) {
      if (offset + BinaryIIConstants.HeaderSize > this._data.Length)
        throw new InvalidDataException("Binary II: truncated record header.");

      var h = this._data.AsSpan(offset, BinaryIIConstants.HeaderSize);
      if (h[0] != 0x0A || h[1] != 0x47 || h[2] != 0x4C || h[0x12] != 0x02)
        throw new InvalidDataException($"Binary II: invalid record signature at offset 0x{offset:X}.");

      var nameLength = h[0x17];
      if (nameLength > BinaryIIConstants.MaxNameLength)
        throw new InvalidDataException($"Binary II: record at 0x{offset:X} has invalid filename length {nameLength}.");
      var name = Encoding.ASCII.GetString(h.Slice(0x18, nameLength)).Replace('\\', '/');

      var lowEof = (uint)(h[0x14] | (h[0x15] << 8) | (h[0x16] << 16));
      var eof = lowEof | ((uint)h[0x74] << 24);
      if (eof > int.MaxValue)
        throw new InvalidDataException($"Binary II: entry '{name}' is too large for this in-memory reader.");
      var storedLength = (int)eof;
      var paddedLength = BinaryIIConstants.RoundUp128(storedLength);
      var dataOffset = checked(offset + BinaryIIConstants.HeaderSize);
      var physicalLength = checked(BinaryIIConstants.HeaderSize + paddedLength);
      if ((long)dataOffset + storedLength > this._data.LongLength)
        throw new InvalidDataException($"Binary II: entry '{name}' extends beyond end of archive.");

      var fileType = h[0x04];
      var storageType = h[0x07];
      var flags = h[0x7D];
      var phantom = h[0x7C] != 0;
      var directory = fileType == BinaryIIConstants.ProDosFileTypeDirectory || storageType == BinaryIIConstants.ProDosStorageDirectory;
      var compressed = (flags & BinaryIIConstants.DataFlagCompressed) != 0
        || name.EndsWith(".QQ", StringComparison.OrdinalIgnoreCase);
      var encrypted = (flags & BinaryIIConstants.DataFlagEncrypted) != 0;
      var sparse = (flags & BinaryIIConstants.DataFlagSparse) != 0;
      var follows = h[0x7F];

      this._records.Add(new BinaryIIRecord(
        name,
        directory,
        phantom,
        compressed,
        encrypted,
        sparse,
        fileType,
        storageType,
        flags,
        storedLength,
        offset,
        dataOffset,
        physicalLength,
        follows
      ));

      var nextOffset = checked(offset + physicalLength);
      if (nextOffset + BinaryIIConstants.HeaderSize > this._data.Length)
        return;
      var next = this._data.AsSpan(nextOffset, BinaryIIConstants.HeaderSize);
      if (next[0] != 0x0A || next[1] != 0x47 || next[2] != 0x4C || next[0x12] != 0x02)
        return;
      offset = nextOffset;
    }

    throw new InvalidDataException("Binary II: archive exceeds the 256-record limit imposed by the files-to-follow byte.");
  }
}

internal static class BinaryIIWriter {
  public static byte[] Build(IReadOnlyList<Compression.Registry.ArchiveInputInfo> inputs, BinaryIICompressionMode mode) {
    ArgumentNullException.ThrowIfNull(inputs);
    var records = PrepareRecords(inputs, mode);
    using var ms = new MemoryStream();
    for (var i = 0; i < records.Count; i++) {
      var bytes = CreatePhysicalRecord(records[i], records.Count - i - 1);
      ms.Write(bytes);
    }
    return ms.ToArray();
  }

  public static List<BinaryIIWriteRecord> PrepareRecords(
    IReadOnlyList<Compression.Registry.ArchiveInputInfo> inputs,
    BinaryIICompressionMode mode
  ) {
    var result = new List<BinaryIIWriteRecord>();
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var input in inputs.OrderBy(i => i.ArchiveName, StringComparer.OrdinalIgnoreCase)) {
      var normalized = NormalizePath(input.ArchiveName);
      var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0)
        continue;

      var parent = "";
      for (var i = 0; i < parts.Length - (input.IsDirectory ? 0 : 1); i++) {
        parent = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
        if (names.Add(parent))
          result.Add(new BinaryIIWriteRecord(parent, true, [], false));
      }

      if (input.IsDirectory) {
        if (names.Add(normalized))
          result.Add(new BinaryIIWriteRecord(normalized, true, [], false));
        continue;
      }

      var unique = MakeUnique(normalized, names);
      names.Add(unique);
      var data = input.ReadContent();
      var compress = ShouldCompress(unique, data, mode);
      result.Add(new BinaryIIWriteRecord(unique, false, data, compress));
    }

    if (result.Count > BinaryIIConstants.MaxRecords)
      throw new InvalidDataException($"Binary II supports at most {BinaryIIConstants.MaxRecords} physical records.");
    return result;
  }

  public static byte[] CreatePhysicalRecord(BinaryIIWriteRecord record, int filesToFollow) {
    ArgumentNullException.ThrowIfNull(record);
    if (filesToFollow is < 0 or > 255)
      throw new ArgumentOutOfRangeException(nameof(filesToFollow));

    var nameBytes = Encoding.ASCII.GetBytes(record.Name);
    if (nameBytes.Length is < 1 or > BinaryIIConstants.MaxNameLength)
      throw new InvalidDataException($"Binary II filename '{record.Name}' is outside the 1..64 byte range.");

    byte[] stored;
    var compressed = false;
    if (!record.IsDirectory && record.Compress) {
      using var src = new MemoryStream(record.Data, writable: false);
      using var dst = new MemoryStream();
      SqueezeStream.Compress(src, dst, Path.GetFileName(record.Name));
      stored = dst.ToArray();
      compressed = true;
    } else {
      stored = record.IsDirectory ? [] : record.Data;
    }

    var padded = BinaryIIConstants.RoundUp128(stored.Length);
    var output = new byte[BinaryIIConstants.HeaderSize + padded];
    var h = output.AsSpan(0, BinaryIIConstants.HeaderSize);

    h[0] = 0x0A;
    h[1] = 0x47;
    h[2] = 0x4C;
    h[0x03] = BinaryIIConstants.ProDosAccessDefault;
    h[0x04] = record.IsDirectory ? BinaryIIConstants.ProDosFileTypeDirectory : BinaryIIConstants.ProDosFileTypeBinary;
    BinaryPrimitives.WriteUInt16LittleEndian(h[0x05..], 0);
    h[0x07] = record.IsDirectory ? BinaryIIConstants.ProDosStorageDirectory : StorageTypeForLength(record.Data.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(h[0x08..], 0);
    h[0x12] = 0x02;

    WriteUInt24LittleEndian(h[0x14..], stored.Length);
    h[0x17] = (byte)nameBytes.Length;
    nameBytes.AsSpan().CopyTo(h[0x18..]);
    h[0x74] = (byte)((uint)stored.Length >> 24);
    h[0x79] = 0x00;
    h[0x7C] = 0x00;
    h[0x7D] = compressed ? BinaryIIConstants.DataFlagCompressed : (byte)0x00;
    h[0x7E] = 0x01;
    h[0x7F] = (byte)filesToFollow;

    if (stored.Length > 0)
      stored.AsSpan().CopyTo(output.AsSpan(BinaryIIConstants.HeaderSize));
    return output;
  }

  public static string NormalizePath(string path) {
    if (string.IsNullOrWhiteSpace(path))
      return "X";

    var rawParts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    var cooked = new List<string>(rawParts.Length);
    foreach (var raw in rawParts) {
      if (raw is "." or "..")
        continue;
      var upper = raw.ToUpperInvariant();
      var sb = new StringBuilder(upper.Length + 1);
      foreach (var ch in upper) {
        if (ch is >= 'A' and <= 'Z' || ch is >= '0' and <= '9' || ch == '.')
          sb.Append(ch);
        else
          sb.Append('.');
      }

      if (sb.Length == 0 || sb[0] is < 'A' or > 'Z')
        sb.Insert(0, 'X');
      if (sb.Length > 15)
        sb.Length = 15;
      cooked.Add(sb.ToString());
    }

    if (cooked.Count == 0)
      return "X";

    var joined = string.Join('/', cooked);
    if (joined.Length <= BinaryIIConstants.MaxNameLength)
      return joined;

    joined = joined[..BinaryIIConstants.MaxNameLength].TrimEnd('/');
    return joined.Length == 0 ? "X" : joined;
  }

  private static string MakeUnique(string normalized, HashSet<string> existing) {
    if (!existing.Contains(normalized))
      return normalized;

    var slash = normalized.LastIndexOf('/');
    var parent = slash >= 0 ? normalized[..(slash + 1)] : "";
    var leaf = slash >= 0 ? normalized[(slash + 1)..] : normalized;
    for (var n = 1; n < 10000; n++) {
      var suffix = "." + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
      var maxLeaf = Math.Min(15, BinaryIIConstants.MaxNameLength - parent.Length);
      var prefixLength = Math.Max(1, maxLeaf - suffix.Length);
      var candidateLeaf = leaf[..Math.Min(leaf.Length, prefixLength)] + suffix;
      var candidate = parent + candidateLeaf;
      if (!existing.Contains(candidate))
        return candidate;
    }

    throw new InvalidDataException($"Binary II could not disambiguate duplicate path '{normalized}'.");
  }

  private static bool ShouldCompress(string name, byte[] data, BinaryIICompressionMode mode) {
    if (mode == BinaryIICompressionMode.Stored || data.Length == 0)
      return false;
    if (mode == BinaryIICompressionMode.Squeeze)
      return true;

    using var src = new MemoryStream(data, writable: false);
    using var dst = new MemoryStream();
    SqueezeStream.Compress(src, dst, Path.GetFileName(name));
    return BinaryIIConstants.RoundUp128(checked((int)dst.Length)) < BinaryIIConstants.RoundUp128(data.Length);
  }

  private static byte StorageTypeForLength(int length)
    => length <= 512 ? BinaryIIConstants.ProDosStorageSeedling
      : length <= 128 * 1024 ? BinaryIIConstants.ProDosStorageSapling
      : BinaryIIConstants.ProDosStorageTree;

  private static void WriteUInt24LittleEndian(Span<byte> destination, int value) {
    var u = (uint)value;
    destination[0] = (byte)u;
    destination[1] = (byte)(u >> 8);
    destination[2] = (byte)(u >> 16);
  }
}

internal static class BinaryIIInPlaceModifier {
  private const int CopyBufferSize = 64 * 1024;

  public static void Add(Stream archive, IReadOnlyList<Compression.Registry.ArchiveInputInfo> inputs) {
    ValidateWritable(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var requested = BinaryIIWriter.PrepareRecords(inputs, BinaryIICompressionMode.Stored);
    foreach (var record in requested) {
      var reader = new BinaryIIReader(archive);
      var existing = reader.PhysicalRecords.FirstOrDefault(
        r => !r.IsPhantom && string.Equals(r.Name, record.Name, StringComparison.OrdinalIgnoreCase));

      if (existing is not null) {
        if (existing.IsDirectory && record.IsDirectory)
          continue;
        if (existing.IsDirectory != record.IsDirectory)
          throw new InvalidOperationException($"Binary II cannot replace '{record.Name}' with a different entry kind while descendants may exist.");
        ReplaceRecord(archive, existing, BinaryIIWriter.CreatePhysicalRecord(record, existing.FilesToFollow));
      } else {
        var physicalCount = reader.PhysicalRecords.Count;
        if (physicalCount >= BinaryIIConstants.MaxRecords)
          throw new InvalidDataException("Binary II archive already contains the maximum 256 records.");

        var logicalEnd = reader.PhysicalRecords.Count == 0
          ? 0L
          : reader.PhysicalRecords[^1].HeaderOffset + reader.PhysicalRecords[^1].PhysicalLength;
        if (archive.Length != logicalEnd)
          archive.SetLength(logicalEnd);
        archive.Position = logicalEnd;
        archive.Write(BinaryIIWriter.CreatePhysicalRecord(record, 0));
      }
    }

    PatchFilesToFollow(archive);
  }

  public static void Remove(Stream archive, string[] entryNames) {
    ValidateWritable(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Length == 0)
      return;

    var normalized = entryNames
      .Where(n => !string.IsNullOrWhiteSpace(n))
      .Select(BinaryIIWriter.NormalizePath)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    var reader = new BinaryIIReader(archive);
    var removals = reader.PhysicalRecords
      .Where(r => !r.IsPhantom && normalized.Any(n =>
        string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase)
        || r.Name.StartsWith(n + "/", StringComparison.OrdinalIgnoreCase)))
      .OrderByDescending(r => r.HeaderOffset)
      .ToList();

    foreach (var record in removals)
      RemoveRange(archive, record.HeaderOffset, record.PhysicalLength);

    PatchFilesToFollow(archive);
  }

  public static void Defragment(Stream archive) {
    ValidateWritable(archive);
    var reader = new BinaryIIReader(archive);
    if (reader.PhysicalRecords.Count == 0) {
      archive.SetLength(0);
      return;
    }

    foreach (var record in reader.PhysicalRecords) {
      var payloadEnd = record.DataOffset + record.StoredLength;
      var recordEnd = record.HeaderOffset + record.PhysicalLength;
      if (payloadEnd < recordEnd) {
        archive.Position = payloadEnd;
        WriteZeros(archive, checked((int)(recordEnd - payloadEnd)));
      }
    }

    var end = reader.PhysicalRecords[^1].HeaderOffset + reader.PhysicalRecords[^1].PhysicalLength;
    archive.SetLength(end);
    PatchFilesToFollow(archive);
  }

  private static void ReplaceRecord(Stream archive, BinaryIIRecord oldRecord, byte[] replacement) {
    var delta = replacement.LongLength - oldRecord.PhysicalLength;
    var tailStart = oldRecord.HeaderOffset + oldRecord.PhysicalLength;
    ShiftTail(archive, tailStart, delta);
    archive.Position = oldRecord.HeaderOffset;
    archive.Write(replacement);
  }

  private static void RemoveRange(Stream archive, long offset, long length)
    => ShiftTail(archive, offset + length, -length);

  private static void ShiftTail(Stream archive, long tailStart, long delta) {
    if (delta == 0)
      return;

    var oldLength = archive.Length;
    if (tailStart < 0 || tailStart > oldLength)
      throw new ArgumentOutOfRangeException(nameof(tailStart));

    var buffer = new byte[CopyBufferSize];
    if (delta > 0) {
      archive.SetLength(checked(oldLength + delta));
      var remaining = oldLength - tailStart;
      while (remaining > 0) {
        var chunk = (int)Math.Min(buffer.Length, remaining);
        var readPos = tailStart + remaining - chunk;
        archive.Position = readPos;
        archive.ReadExactly(buffer.AsSpan(0, chunk));
        archive.Position = readPos + delta;
        archive.Write(buffer, 0, chunk);
        remaining -= chunk;
      }
    } else {
      var shift = -delta;
      var readPos = tailStart;
      var writePos = tailStart - shift;
      while (readPos < oldLength) {
        var chunk = (int)Math.Min(buffer.Length, oldLength - readPos);
        archive.Position = readPos;
        archive.ReadExactly(buffer.AsSpan(0, chunk));
        archive.Position = writePos;
        archive.Write(buffer, 0, chunk);
        readPos += chunk;
        writePos += chunk;
      }
      archive.SetLength(checked(oldLength - shift));
    }
  }

  private static void PatchFilesToFollow(Stream archive) {
    var reader = new BinaryIIReader(archive);
    if (reader.PhysicalRecords.Count > BinaryIIConstants.MaxRecords)
      throw new InvalidDataException("Binary II archive contains more than 256 physical records.");

    for (var i = 0; i < reader.PhysicalRecords.Count; i++) {
      archive.Position = reader.PhysicalRecords[i].HeaderOffset + 0x7F;
      archive.WriteByte((byte)(reader.PhysicalRecords.Count - i - 1));
    }
    if (archive.CanSeek)
      archive.Position = 0;
  }

  private static void ValidateWritable(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("Binary II direct modification requires a readable, writable, seekable stream.", nameof(archive));
  }

  private static void WriteZeros(Stream output, int count) {
    Span<byte> zeros = stackalloc byte[128];
    zeros.Clear();
    while (count > 0) {
      var n = Math.Min(count, zeros.Length);
      output.Write(zeros[..n]);
      count -= n;
    }
  }
}
