#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.SmartFs;

/// <summary>
/// Builds a SmartFS volume: a format sector, a root directory, and a sector
/// chain per file.
/// </summary>
/// <remarks>
/// <para>The volume this emits is what a freshly formatted flash looks like
/// before wear levelling has moved anything: logical sector N sits in physical
/// sector N, every sector's sequence number is zero, and the free sectors past
/// the last file are erased. That is the state <c>mksmartfs</c> leaves behind
/// plus the files, so NuttX reads it as an ordinary volume.</para>
///
/// <para>Names are limited to <see cref="SmartFsLayout.MaxNameLength" />
/// characters, which is the directory entry's fixed name field — the format has
/// nowhere to put a longer one.</para>
/// </remarks>
public sealed class SmartFsWriter {

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Bytes per sector. One of 256, 512, 1024, 2048 or 4096.</summary>
  public int SectorSize { get; init; } = 1024;

  /// <summary>Adds a file to the volume.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>
  /// Lays the volume out. <paramref name="totalSectors" /> of zero sizes the
  /// image to what the files need; a larger count leaves the surplus erased,
  /// which is where a running system would put new files.
  /// </summary>
  public byte[] Build(int totalSectors = 0) {
    var sectorSize = this.SectorSize;
    var sizeCode = SmartFsLayout.SizeCode(sectorSize);
    var payloadPerSector = sectorSize - SmartFsLayout.SectorHeaderSize - SmartFsLayout.ChainHeaderSize;
    if (payloadPerSector <= SmartFsLayout.EntrySize)
      throw new InvalidOperationException(
        $"SmartFS: a {sectorSize}-byte sector leaves {payloadPerSector} bytes for content, " +
        "which is not enough for one directory entry.");

    foreach (var (name, _) in this._files)
      if (Encoding.ASCII.GetByteCount(name) > SmartFsLayout.MaxNameLength)
        throw new InvalidOperationException(
          $"SmartFS: '{name}' is longer than the {SmartFsLayout.MaxNameLength} characters a " +
          "directory entry's name field holds.");

    // Lay the chains out first so the sector count is known before anything is
    // written: a file needs one sector per payloadPerSector bytes (at least
    // one, so an empty file still has a sector to point at), and the root needs
    // one per entriesPerSector entries.
    var entriesPerSector = payloadPerSector / SmartFsLayout.EntrySize;
    var rootSectors = Math.Max(1, (this._files.Count + entriesPerSector - 1) / entriesPerSector);

    var nextFree = (ushort)(SmartFsLayout.FirstDataSector + rootSectors - 1);
    var fileChains = new List<List<ushort>>(this._files.Count);
    foreach (var (_, data) in this._files) {
      var needed = Math.Max(1, (data.Length + payloadPerSector - 1) / payloadPerSector);
      var chain = new List<ushort>(needed);
      for (var i = 0; i < needed; ++i) chain.Add(++nextFree);
      fileChains.Add(chain);
    }

    var used = nextFree + 1;
    var sectors = Math.Max(totalSectors, used);
    var image = new byte[(long)sectors * sectorSize];

    // Erased flash reads as 0xFF, and SmartFS relies on that: a sector whose
    // logical number is still all-ones is one that has never been written.
    image.AsSpan().Fill(0xFF);

    WriteFormatSector(image, sectorSize, sizeCode, (ushort)rootSectors);

    // Root directory: the entries, split across as many sectors as they need.
    var rootChain = new List<ushort>(rootSectors);
    rootChain.Add(SmartFsLayout.RootDirSector);
    for (var i = 1; i < rootSectors; ++i)
      rootChain.Add((ushort)(SmartFsLayout.FirstDataSector + i - 1));

    var entryIndex = 0;
    for (var s = 0; s < rootChain.Count; ++s) {
      var count = Math.Min(entriesPerSector, this._files.Count - entryIndex);
      var next = s + 1 < rootChain.Count ? rootChain[s + 1] : SmartFsLayout.EndOfChain;
      var payload = new byte[count * SmartFsLayout.EntrySize];
      for (var e = 0; e < count; ++e) {
        var (name, data) = this._files[entryIndex + e];
        WriteEntry(payload.AsSpan(e * SmartFsLayout.EntrySize), name, fileChains[entryIndex + e][0]);
      }
      WriteSector(image, sectorSize, rootChain[s], next, SmartFsLayout.ChainTypeDirectory, payload);
      entryIndex += count;
    }

    // File chains.
    for (var f = 0; f < this._files.Count; ++f) {
      var data = this._files[f].Data;
      var chain = fileChains[f];
      for (var i = 0; i < chain.Count; ++i) {
        var offset = i * payloadPerSector;
        var take = Math.Min(payloadPerSector, Math.Max(0, data.Length - offset));
        var next = i + 1 < chain.Count ? chain[i + 1] : SmartFsLayout.EndOfChain;
        WriteSector(image, sectorSize, chain[i], next, SmartFsLayout.ChainTypeFile,
          data.AsSpan(offset, take));
      }
    }

    return image;
  }

  /// <summary>
  /// Sector 0 carries the signature a driver looks for, the sector size it must
  /// read the volume with, and how many sectors the root directory spans.
  /// </summary>
  private static void WriteFormatSector(byte[] image, int sectorSize, byte sizeCode, ushort rootSectors) {
    var sector = image.AsSpan(0, sectorSize);
    sector.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(sector, 0);          // logical sector 0
    sector[2] = 0;                                                 // sequence
    sector[3] = 0;                                                 // CRC, unused in this configuration
    sector[4] = SmartFsLayout.StatusCommitted;

    SmartFsLayout.Signature.CopyTo(sector[SmartFsLayout.SignatureOffset..]);
    sector[SmartFsLayout.SignatureOffset + 4] = SmartFsLayout.FormatVersion;
    sector[SmartFsLayout.SignatureOffset + 5] = sizeCode;
    BinaryPrimitives.WriteUInt16LittleEndian(sector[(SmartFsLayout.SignatureOffset + 6)..], rootSectors);
  }

  /// <summary>Writes one sector: its header, its chain header, and its payload.</summary>
  private static void WriteSector(byte[] image, int sectorSize, ushort logical, ushort next,
      byte chainType, ReadOnlySpan<byte> payload) {
    var at = (long)logical * sectorSize;
    if (at + sectorSize > image.Length)
      throw new InvalidOperationException(
        $"SmartFS: sector {logical} does not fit in a {image.Length:N0}-byte volume.");

    var sector = image.AsSpan((int)at, sectorSize);
    sector.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(sector, logical);
    sector[2] = 0;
    sector[3] = 0;
    sector[4] = SmartFsLayout.StatusCommitted;

    var chain = sector[SmartFsLayout.SectorHeaderSize..];
    BinaryPrimitives.WriteUInt16LittleEndian(chain, next);
    BinaryPrimitives.WriteUInt16LittleEndian(chain[2..], (ushort)payload.Length);
    chain[4] = chainType;

    payload.CopyTo(chain[SmartFsLayout.ChainHeaderSize..]);
  }

  /// <summary>Writes one directory entry: flags, where the file starts, and its name.</summary>
  private static void WriteEntry(Span<byte> entry, string name, ushort firstSector) {
    entry.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)(SmartFsLayout.EntryActive | 0x01A4));
    BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], firstSector);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 0);       // timestamp
    var bytes = Encoding.ASCII.GetBytes(name);
    bytes.AsSpan(0, Math.Min(bytes.Length, SmartFsLayout.MaxNameLength))
      .CopyTo(entry[SmartFsLayout.EntryHeaderSize..]);
  }
}
