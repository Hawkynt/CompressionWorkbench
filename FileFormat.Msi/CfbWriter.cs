#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Msi;

/// <summary>
/// Writes a flat OLE Compound File Binary (MS-CFB) container holding a list of
/// named streams under a single root storage. Used to give "WORM" creation to
/// formats that wrap CFB (DOC/XLS/PPT/MSG/Thumbs.db) -- the produced files
/// roundtrip through <see cref="CfbReader"/> and <see cref="MsiReader"/>.
///
/// Simplifications taken to keep this small (~200 LoC vs the ~2000-LoC full
/// MS-CFB writer):
/// <list type="bullet">
///   <item>Always v3 (512-byte sectors). Up to ~6.8 MB total file size (109 FAT sectors, no DIFAT chain).</item>
///   <item>Mini-stream cutoff is set to 0 -- every stream uses regular sectors regardless of size, so no mini FAT or mini stream bookkeeping is needed.</item>
///   <item>Single root storage, no nested sub-storages. All streams are direct children.</item>
///   <item>Directory tree is a degenerate right-leaning chain. Permissive readers (ours, libgsf, Apache POI) accept it; strict readers (Word/Excel) won't open the file as a document but its CFB envelope is structurally valid.</item>
/// </list>
/// </summary>
public sealed class CfbWriter {
  private const int SectorSize = 512;
  private const int DirEntrySize = 128;
  private const int DirEntriesPerSector = SectorSize / DirEntrySize; // 4
  private const int FatEntriesPerSector = SectorSize / 4; // 128
  private const int MaxHeaderDifat = 109;

  private const uint NoStream  = 0xFFFFFFFFu;
  private const uint EndOfChain = 0xFFFFFFFEu;
  private const uint FatSect    = 0xFFFFFFFDu;
  private const uint FreeSect   = 0xFFFFFFFFu;

  private readonly List<(string name, byte[] data)> _streams = [];

  /// <summary>Adds a stream entry to the root storage.</summary>
  /// <param name="name">UTF-16 stream name. CFB limits names to 31 characters (2 bytes each + null = 64-byte field).</param>
  /// <param name="data">Stream payload. May be empty.</param>
  public void AddStream(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Length == 0)
      throw new ArgumentException("Stream name must be non-empty.", nameof(name));
    if (name.Length > 31)
      throw new ArgumentException($"CFB stream names are limited to 31 chars; '{name}' is {name.Length}.", nameof(name));
    _streams.Add((name, data));
  }

  /// <summary>Serialises the CFB container to <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var n = _streams.Count;
    var dirSectors = ((1 + n) * DirEntrySize + SectorSize - 1) / SectorSize;

    // Stream sector requirements.
    var streamSectorCount = new int[n];
    for (var i = 0; i < n; i++)
      streamSectorCount[i] = _streams[i].data.Length == 0 ? 0
        : (_streams[i].data.Length + SectorSize - 1) / SectorSize;
    var totalStreamSectors = 0;
    for (var i = 0; i < n; i++) totalStreamSectors += streamSectorCount[i];

    var nonFatSectors = dirSectors + totalStreamSectors;
    // Iterate to a fixed point for FAT sector count -- each FAT sector is itself
    // covered by the FAT, so adding a FAT sector grows the total.
    var fatSectors = (nonFatSectors + FatEntriesPerSector - 1) / FatEntriesPerSector;
    while (true) {
      var total = nonFatSectors + fatSectors;
      var need = (total + FatEntriesPerSector - 1) / FatEntriesPerSector;
      if (need == fatSectors) break;
      fatSectors = need;
    }
    if (fatSectors > MaxHeaderDifat)
      throw new InvalidOperationException(
        $"CfbWriter: file would need {fatSectors} FAT sectors but DIFAT chain is not implemented (max {MaxHeaderDifat}). " +
        "Reduce total stream payload below ~6.8 MB.");

    // Allocate sector IDs sequentially: FAT, Directory, Streams.
    uint nextSid = 0;
    var fatSectorIds = new uint[fatSectors];
    for (var i = 0; i < fatSectors; i++) fatSectorIds[i] = nextSid++;
    var dirSectorIds = new uint[dirSectors];
    for (var i = 0; i < dirSectors; i++) dirSectorIds[i] = nextSid++;
    var streamStarts = new uint[n];
    for (var i = 0; i < n; i++) {
      if (streamSectorCount[i] == 0) {
        streamStarts[i] = EndOfChain;
      } else {
        streamStarts[i] = nextSid;
        nextSid += (uint)streamSectorCount[i];
      }
    }

    // Build FAT in memory.
    var fat = new uint[fatSectors * FatEntriesPerSector];
    Array.Fill(fat, FreeSect);
    foreach (var sid in fatSectorIds) fat[sid] = FatSect;
    for (var i = 0; i < dirSectors; i++)
      fat[dirSectorIds[i]] = i == dirSectors - 1 ? EndOfChain : dirSectorIds[i + 1];
    for (var i = 0; i < n; i++) {
      if (streamSectorCount[i] == 0) continue;
      var start = streamStarts[i];
      for (var j = 0; j < streamSectorCount[i]; j++)
        fat[start + j] = j == streamSectorCount[i] - 1 ? EndOfChain : (uint)(start + j + 1);
    }

    // ---- Write header (sector before sector 0) ----
    WriteHeader(output, fatSectors, dirSectorIds[0], fatSectorIds);

    // ---- Write FAT sectors ----
    Span<byte> u32 = stackalloc byte[4];
    foreach (var sid in fatSectorIds) {
      var start = (int)sid * FatEntriesPerSector;
      for (var i = 0; i < FatEntriesPerSector; i++) {
        BinaryPrimitives.WriteUInt32LittleEndian(u32, fat[start + i]);
        output.Write(u32);
      }
    }

    // ---- Write directory sectors ----
    // Root entry first, then one per stream, padded with empty entries to fill
    // the last directory sector.
    var dirEntries = new List<byte[]>(dirSectors * DirEntriesPerSector);
    dirEntries.Add(BuildRootEntry(n > 0 ? 1u : NoStream));
    for (var i = 0; i < n; i++) {
      // Degenerate right-chain: entry i+1's right sibling = entry i+2 (or NoStream).
      var right = i == n - 1 ? NoStream : (uint)(i + 2);
      dirEntries.Add(BuildStreamEntry(_streams[i].name, streamStarts[i],
        _streams[i].data.Length, NoStream, right, NoStream));
    }
    while (dirEntries.Count < dirSectors * DirEntriesPerSector)
      dirEntries.Add(BuildEmptyEntry());
    foreach (var entry in dirEntries)
      output.Write(entry);

    // ---- Write stream data sectors ----
    Span<byte> padBuf = stackalloc byte[SectorSize];
    padBuf.Clear();
    for (var i = 0; i < n; i++) {
      var data = _streams[i].data;
      if (data.Length == 0) continue;
      output.Write(data);
      var pad = SectorSize - data.Length % SectorSize;
      if (pad < SectorSize)
        output.Write(padBuf[..pad]);
    }
  }

  // ── Header ─────────────────────────────────────────────────────────────────

  private static void WriteHeader(Stream output, int fatSectors, uint firstDirSector, uint[] fatSectorIds) {
    Span<byte> hdr = stackalloc byte[SectorSize];
    hdr.Clear();

    // Magic
    ReadOnlySpan<byte> magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    magic.CopyTo(hdr);
    // CLSID (16 bytes of zero) at 0x08 -- already zero
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[0x18..], 0x003E);          // minor version
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[0x1A..], 0x0003);          // major version (3)
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[0x1C..], 0xFFFE);          // byte order = little-endian
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[0x1E..], 9);               // sector shift: 2^9 = 512
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[0x20..], 6);               // mini sector shift: 2^6 = 64
    // Reserved bytes 0x22..0x27 = 0
    BinaryPrimitives.WriteInt32LittleEndian(hdr[0x28..], 0);                // num dir sectors (0 for v3)
    BinaryPrimitives.WriteInt32LittleEndian(hdr[0x2C..], fatSectors);       // num FAT sectors
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[0x30..], firstDirSector);  // first dir sector
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[0x34..], 0);               // transaction sig
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[0x38..], 0);               // mini stream cutoff = 0 -> no mini FAT
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[0x3C..], EndOfChain);      // first mini FAT sector
    BinaryPrimitives.WriteInt32LittleEndian(hdr[0x40..], 0);                // num mini FAT sectors
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[0x44..], EndOfChain);      // first DIFAT sector
    BinaryPrimitives.WriteInt32LittleEndian(hdr[0x48..], 0);                // num DIFAT sectors

    // DIFAT array: 109 entries at 0x4C
    for (var i = 0; i < MaxHeaderDifat; i++) {
      var sid = i < fatSectorIds.Length ? fatSectorIds[i] : FreeSect;
      BinaryPrimitives.WriteUInt32LittleEndian(hdr[(0x4C + i * 4)..], sid);
    }

    output.Write(hdr);
  }

  // ── Directory entries ───────────────────────────────────────────────────────

  private static byte[] BuildRootEntry(uint childDid) {
    var e = new byte[DirEntrySize];
    WriteEntryName(e, "Root Entry");
    e[0x42] = 5; // RootStorage
    e[0x43] = 1; // color: black
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x44), NoStream); // left sibling
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x48), NoStream); // right sibling
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x4C), childDid); // child
    // CLSID (0x50, 16 bytes) = 0
    // State flags (0x60), creation/modified time (0x64..0x73) = 0
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x74), EndOfChain); // start sector (no mini stream)
    BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(0x78), 0);           // mini stream size = 0
    return e;
  }

  private static byte[] BuildStreamEntry(string name, uint startSector, long size,
      uint leftDid, uint rightDid, uint childDid) {
    var e = new byte[DirEntrySize];
    WriteEntryName(e, name);
    e[0x42] = 2; // Stream
    e[0x43] = 1; // color: black
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x44), leftDid);
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x48), rightDid);
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x4C), childDid);
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x74), size == 0 ? EndOfChain : startSector);
    BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(0x78), size);
    return e;
  }

  private static byte[] BuildEmptyEntry() {
    var e = new byte[DirEntrySize];
    // Object type = 0 (Unknown / unallocated). Sibling pointers = NoStream.
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x44), NoStream);
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x48), NoStream);
    BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(0x4C), NoStream);
    return e;
  }

  private static void WriteEntryName(byte[] entry, string name) {
    var bytes = Encoding.Unicode.GetBytes(name);
    Buffer.BlockCopy(bytes, 0, entry, 0, bytes.Length);
    // null terminator (UTF-16 LE = 2 zero bytes) follows; entry already zero-filled
    var nameLenIncludingNull = (ushort)(bytes.Length + 2);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(0x40), nameLenIncludingNull);
  }
}
