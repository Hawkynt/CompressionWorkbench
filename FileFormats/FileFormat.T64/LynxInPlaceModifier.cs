#pragma warning disable CS1591

namespace FileFormat.Lynx;

internal static class LynxInPlaceModifier {
  private const int CopyBufferSize = 64 * 1024;

  public static void AddOrReplace(Stream archive, string name, byte[] data, char fileType) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(data);
    EnsureWritable(archive);

    var reader = Read(archive);
    var normalized = LynxWriter.NormalizeName(name).Name;
    var existing = reader.Entries.FirstOrDefault(entry =>
      string.Equals(entry.Name, normalized, StringComparison.OrdinalIgnoreCase));
    if (existing is not null) {
      Replace(archive, reader, existing, data);
      return;
    }

    var newSpec = LynxWriter.CreateSpec(normalized, data.Length, fileType);
    var specs = reader.Entries.Select(LynxWriter.FromEntry).Append(newSpec).ToArray();
    var directory = LynxWriter.BuildDirectory(
      specs,
      reader.DirectoryBlocks,
      reader.BasicHeader,
      reader.Signature);

    var oldDirectoryBytes = checked(reader.DirectoryBlocks * LynxReader.BlockSize);
    var directoryDelta = directory.Length - oldDirectoryBytes;
    if (directoryDelta < 0)
      throw new InvalidOperationException("Lynx in-place add never shrinks the directory allocation.");
    if (directoryDelta > 0)
      ShiftTail(archive, reader.DataStart, directoryDelta);

    var appendOffset = checked(reader.LogicalDataEnd + directoryDelta);
    var allocationBytes = checked((long)newSpec.ArchiveBlocks * LynxReader.BlockSize);
    if (allocationBytes > 0)
      ShiftTail(archive, appendOffset, allocationBytes);

    archive.Position = appendOffset;
    LynxWriter.WritePaddedPayload(archive, data, newSpec.ArchiveBlocks);
    WriteDirectory(archive, directory);
    archive.Position = 0;
  }

  public static void Remove(Stream archive, string name) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    EnsureWritable(archive);

    var reader = Read(archive);
    var normalized = LynxWriter.NormalizeName(name).Name;
    var index = reader.Entries.ToList().FindIndex(entry =>
      string.Equals(entry.Name, normalized, StringComparison.OrdinalIgnoreCase));
    if (index < 0)
      return;

    var entry = reader.Entries[index];
    var allocationBytes = checked((long)entry.ArchiveBlocks * LynxReader.BlockSize);
    if (allocationBytes > 0)
      ShiftTail(archive, entry.AllocationOffset + allocationBytes, -allocationBytes);

    var specs = reader.Entries
      .Where((_, entryIndex) => entryIndex != index)
      .Select(LynxWriter.FromEntry)
      .ToArray();
    var directory = LynxWriter.BuildDirectory(
      specs,
      reader.DirectoryBlocks,
      reader.BasicHeader,
      reader.Signature);
    if (directory.Length != reader.DirectoryBlocks * LynxReader.BlockSize)
      throw new InvalidOperationException("Lynx remove must preserve the current directory allocation.");
    WriteDirectory(archive, directory);
    archive.Position = 0;
  }

  private static void Replace(Stream archive, LynxReader reader, LynxEntry existing, byte[] data) {
    if (existing.FileType == 'R')
      throw new NotSupportedException(
        "Replacing a Lynx REL entry requires rebuilding its side-sector chain; REL is readable/removable but direct replacement is not claimed.");

    var replacement = LynxWriter.CreateSpec(existing.Name, data.Length, existing.FileType) with {
      Name = existing.Name,
      RawName = existing.RawName.ToArray(),
    };

    var specs = reader.Entries
      .Select(entry => ReferenceEquals(entry, existing) ? replacement : LynxWriter.FromEntry(entry))
      .ToArray();
    var directory = LynxWriter.BuildDirectory(
      specs,
      reader.DirectoryBlocks,
      reader.BasicHeader,
      reader.Signature);

    var oldDirectoryBytes = checked(reader.DirectoryBlocks * LynxReader.BlockSize);
    var directoryDelta = directory.Length - oldDirectoryBytes;
    if (directoryDelta < 0)
      throw new InvalidOperationException("Lynx in-place replace never shrinks the directory allocation.");
    if (directoryDelta > 0)
      ShiftTail(archive, reader.DataStart, directoryDelta);

    var allocationOffset = checked(existing.AllocationOffset + directoryDelta);
    var oldAllocationBytes = checked((long)existing.ArchiveBlocks * LynxReader.BlockSize);
    var newAllocationBytes = checked((long)replacement.ArchiveBlocks * LynxReader.BlockSize);
    var allocationDelta = newAllocationBytes - oldAllocationBytes;
    if (allocationDelta != 0)
      ShiftTail(archive, allocationOffset + oldAllocationBytes, allocationDelta);

    archive.Position = allocationOffset;
    LynxWriter.WritePaddedPayload(archive, data, replacement.ArchiveBlocks);
    WriteDirectory(archive, directory);
    archive.Position = 0;
  }

  private static LynxReader Read(Stream archive) {
    archive.Position = 0;
    return new LynxReader(archive);
  }

  private static void WriteDirectory(Stream archive, byte[] directory) {
    archive.Position = 0;
    archive.Write(directory);
    archive.Flush();
  }

  private static void ShiftTail(Stream stream, long start, long delta) {
    if (delta == 0) return;
    if (start < 0 || start > stream.Length)
      throw new ArgumentOutOfRangeException(nameof(start));
    if (delta < 0 && start + delta < 0)
      throw new ArgumentOutOfRangeException(nameof(delta));

    var oldLength = stream.Length;
    var newLength = checked(oldLength + delta);
    if (newLength < 0)
      throw new IOException("Lynx block shift would make the archive length negative.");

    var buffer = new byte[CopyBufferSize];
    if (delta > 0) {
      stream.SetLength(newLength);
      var remaining = oldLength - start;
      while (remaining > 0) {
        var chunk = (int)Math.Min(buffer.Length, remaining);
        var source = start + remaining - chunk;
        stream.Position = source;
        stream.ReadExactly(buffer.AsSpan(0, chunk));
        stream.Position = source + delta;
        stream.Write(buffer, 0, chunk);
        remaining -= chunk;
      }
      return;
    }

    var sourcePosition = start;
    var remainingForward = oldLength - start;
    while (remainingForward > 0) {
      var chunk = (int)Math.Min(buffer.Length, remainingForward);
      stream.Position = sourcePosition;
      stream.ReadExactly(buffer.AsSpan(0, chunk));
      stream.Position = sourcePosition + delta;
      stream.Write(buffer, 0, chunk);
      sourcePosition += chunk;
      remainingForward -= chunk;
    }
    stream.SetLength(newLength);
  }

  private static void EnsureWritable(Stream stream) {
    if (!stream.CanSeek || !stream.CanRead || !stream.CanWrite)
      throw new ArgumentException("Lynx in-place modification requires a readable, writable, seekable stream.", nameof(stream));
  }
}
