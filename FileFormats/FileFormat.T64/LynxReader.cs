#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Lynx;

internal sealed record LynxEntry(
  string Name,
  byte[] RawName,
  char FileType,
  int RecordSize,
  int ArchiveBlocks,
  int DataBlocks,
  int LastBlockCount,
  long AllocationOffset,
  long DataOffset,
  int Length
);

internal sealed class LynxReader {
  internal const int BlockSize = 254;
  private const int MaxBasicHeader = 1024;
  private readonly byte[] _data;
  private readonly List<LynxEntry> _entries = [];

  public IReadOnlyList<LynxEntry> Entries => this._entries;
  public int DirectoryBlocks { get; private set; }
  public int FileCount { get; private set; }
  public string Signature { get; private set; } = string.Empty;
  public byte[] BasicHeader { get; private set; } = [];
  public long DataStart => (long)this.DirectoryBlocks * BlockSize;
  public long LogicalDataEnd { get; private set; }

  public LynxReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    this._data = memory.ToArray();
    this.Parse();
  }

  public byte[] Extract(LynxEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Length == 0) return [];
    if (entry.DataOffset < 0 || entry.DataOffset > this._data.LongLength - entry.Length)
      throw new InvalidDataException($"Lynx entry '{entry.Name}' points outside the archive.");
    return this._data.AsSpan(checked((int)entry.DataOffset), entry.Length).ToArray();
  }

  private void Parse() {
    if (this._data.Length < BlockSize)
      throw new InvalidDataException("Lynx archive is shorter than one 254-byte block.");

    var cursor = FindBasicHeaderEnd(this._data);
    this.BasicHeader = this._data.AsSpan(0, cursor).ToArray();

    this.DirectoryBlocks = ReadUnsigned(this._data, ref cursor, this._data.Length, requireCarriageReturn: false);
    if (this.DirectoryBlocks <= 0)
      throw new InvalidDataException("Lynx directory block count must be positive.");

    SkipSpaces(this._data, ref cursor, this._data.Length);
    if (cursor + 24 >= this._data.Length)
      throw new InvalidDataException("Lynx archive is truncated in its 24-byte signature.");
    this.Signature = Encoding.Latin1.GetString(this._data, cursor, 24);
    cursor += 24;
    if (!this.Signature.Contains("LYNX", StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("Lynx archive signature does not contain 'LYNX'.");
    ExpectCarriageReturn(this._data, ref cursor, this._data.Length);

    this.FileCount = ReadUnsigned(this._data, ref cursor, this._data.Length, requireCarriageReturn: true);
    if (this.FileCount < 0 || this.FileCount > 100_000)
      throw new InvalidDataException($"Lynx file count {this.FileCount} is out of range.");

    var directoryEnd = checked((long)this.DirectoryBlocks * BlockSize);
    if (directoryEnd > this._data.LongLength)
      throw new InvalidDataException("Lynx directory extends beyond the archive.");
    var directoryLimit = checked((int)directoryEnd);
    if (cursor > directoryLimit)
      throw new InvalidDataException("Lynx directory header exceeds its declared block count.");

    var allocationOffset = directoryEnd;
    var power64LengthConvention = this.Signature.Contains("POWER64", StringComparison.OrdinalIgnoreCase);

    for (var index = 0; index < this.FileCount; ++index) {
      var rawName = ReadRawName(this._data, ref cursor, directoryLimit);
      var name = DecodeName(rawName);
      var archiveBlocks = ReadUnsigned(this._data, ref cursor, directoryLimit, requireCarriageReturn: true);
      if (archiveBlocks < 0)
        throw new InvalidDataException($"Lynx entry '{name}' has a negative block count.");

      if (cursor >= directoryLimit)
        throw new InvalidDataException("Lynx directory is truncated before a file type.");
      var fileType = char.ToUpperInvariant((char)this._data[cursor++]);
      ExpectCarriageReturn(this._data, ref cursor, directoryLimit);
      if (fileType is not ('D' or 'S' or 'P' or 'U' or 'R'))
        throw new InvalidDataException($"Lynx entry '{name}' has unknown file type '{fileType}'.");

      var recordSize = 0;
      int lastBlockCount;
      if (fileType == 'R') {
        recordSize = ReadUnsigned(this._data, ref cursor, directoryLimit, requireCarriageReturn: true);
        if (!TryReadUnsigned(this._data, ref cursor, directoryLimit, requireCarriageReturn: true, out lastBlockCount)) {
          if (index != this.FileCount - 1)
            throw new InvalidDataException($"Lynx REL entry '{name}' is missing its last-block byte count.");
          lastBlockCount = archiveBlocks == 0 ? 0 : 255;
        }
      } else if (!TryReadUnsigned(this._data, ref cursor, directoryLimit, requireCarriageReturn: true, out lastBlockCount)) {
        if (index != this.FileCount - 1)
          throw new InvalidDataException($"Lynx entry '{name}' is missing its last-block byte count.");
        lastBlockCount = archiveBlocks == 0 ? 0 : 255;
      }

      var sideSectorBlocks = 0;
      var dataBlocks = archiveBlocks;
      if (fileType == 'R' && archiveBlocks > 0) {
        sideSectorBlocks = (archiveBlocks + 119) / 121;
        if (sideSectorBlocks <= 0 || archiveBlocks < 121 * sideSectorBlocks - 119 || archiveBlocks > 121 * sideSectorBlocks)
          throw new InvalidDataException($"Lynx REL entry '{name}' has an invalid side-sector/block relationship.");
        dataBlocks -= sideSectorBlocks;
      }

      var length = DecodeLength(dataBlocks, lastBlockCount, power64LengthConvention, name);
      var dataOffset = checked(allocationOffset + (long)sideSectorBlocks * BlockSize);
      if (dataOffset < 0 || dataOffset > this._data.LongLength - length)
        throw new InvalidDataException($"Lynx entry '{name}' data extends beyond the archive.");

      this._entries.Add(new LynxEntry(
        name,
        rawName,
        fileType,
        recordSize,
        archiveBlocks,
        dataBlocks,
        lastBlockCount,
        allocationOffset,
        dataOffset,
        length));

      allocationOffset = checked(allocationOffset + (long)archiveBlocks * BlockSize);
      if (allocationOffset > this._data.LongLength)
        throw new InvalidDataException($"Lynx entry '{name}' allocation extends beyond the archive.");
    }

    this.LogicalDataEnd = allocationOffset;
  }

  private static int DecodeLength(int blocks, int lastBlockCount, bool directCount, string name) {
    if (blocks == 0) {
      if (lastBlockCount != 0)
        throw new InvalidDataException($"Lynx entry '{name}' has zero blocks but a non-zero last-block count.");
      return 0;
    }

    int lastBytes;
    if (directCount) {
      if (lastBlockCount is < 1 or > BlockSize)
        throw new InvalidDataException($"Lynx entry '{name}' has invalid Power64 last-block count {lastBlockCount}.");
      lastBytes = lastBlockCount;
    } else {
      // Classic Lynx preserves the 1541 terminal-sector count byte: 255 means all 254
      // payload bytes are used, otherwise the stored value is payload-byte-count + 1.
      if (lastBlockCount is < 2 or > 255)
        throw new InvalidDataException($"Lynx entry '{name}' has invalid last-block count {lastBlockCount}.");
      lastBytes = lastBlockCount == 255 ? BlockSize : lastBlockCount - 1;
    }

    var length = checked((long)(blocks - 1) * BlockSize + lastBytes);
    if (length > int.MaxValue)
      throw new NotSupportedException($"Lynx entry '{name}' exceeds the managed-array limit.");
    return (int)length;
  }

  private static int FindBasicHeaderEnd(byte[] data) {
    var limit = Math.Min(data.Length, MaxBasicHeader);
    for (var i = 4; i <= limit; ++i) {
      if (data[i - 4] == 0 && data[i - 3] == 0 && data[i - 2] == 0 && data[i - 1] == 13)
        return i;
    }
    throw new InvalidDataException("Lynx BASIC preamble terminator was not found in the first 1024 bytes.");
  }

  private static byte[] ReadRawName(byte[] data, ref int cursor, int limit) {
    var raw = Enumerable.Repeat((byte)0xA0, 16).ToArray();
    var count = 0;
    while (cursor < limit) {
      var value = data[cursor++];
      if (value == 13)
        return raw;
      if (count >= 16)
        throw new InvalidDataException("Lynx directory contains a file name longer than 16 bytes.");
      raw[count++] = value;
    }
    throw new InvalidDataException("Lynx directory is truncated in a file name.");
  }

  private static string DecodeName(byte[] raw) {
    var length = raw.Length;
    while (length > 0 && raw[length - 1] is 0xA0 or 0x20 or 0x00)
      --length;
    return Encoding.Latin1.GetString(raw, 0, length);
  }

  private static int ReadUnsigned(byte[] data, ref int cursor, int limit, bool requireCarriageReturn) {
    if (!TryReadUnsigned(data, ref cursor, limit, requireCarriageReturn, out var value))
      throw new InvalidDataException("Lynx directory expected an ASCII decimal number.");
    return value;
  }

  private static bool TryReadUnsigned(byte[] data, ref int cursor, int limit, bool requireCarriageReturn, out int value) {
    SkipSpaces(data, ref cursor, limit);
    var start = cursor;
    long result = 0;
    while (cursor < limit && data[cursor] is >= (byte)'0' and <= (byte)'9') {
      result = checked(result * 10 + data[cursor] - (byte)'0');
      if (result > int.MaxValue)
        throw new InvalidDataException("Lynx ASCII numeric field is too large.");
      ++cursor;
    }
    if (cursor == start) {
      value = 0;
      return false;
    }

    SkipSpaces(data, ref cursor, limit);
    if (requireCarriageReturn)
      ExpectCarriageReturn(data, ref cursor, limit);
    value = (int)result;
    return true;
  }

  private static void SkipSpaces(byte[] data, ref int cursor, int limit) {
    while (cursor < limit && data[cursor] == 0x20)
      ++cursor;
  }

  private static void ExpectCarriageReturn(byte[] data, ref int cursor, int limit) {
    if (cursor >= limit || data[cursor] != 13)
      throw new InvalidDataException("Lynx directory expected a carriage-return delimiter.");
    ++cursor;
  }
}
