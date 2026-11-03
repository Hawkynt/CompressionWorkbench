#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.PkwareDcl;

namespace FileFormat.Mpq;

/// <summary>How the <see cref="MpqWriter"/> stores one file.</summary>
/// <param name="Compression">An MPQ compression mask (<see cref="MpqCompression"/>), 0 to store.</param>
/// <param name="Implode">Use the PKWARE "imploded file" flag (Diablo style) instead of a compression mask.</param>
/// <param name="Encrypt">Encrypt the file with the key derived from its name.</param>
/// <param name="AdjustedKey">Adjust the key by the file's offset and size (<c>MPQ_FILE_KEY_V2</c>).</param>
public sealed record MpqFileOptions(byte Compression = 0, bool Implode = false, bool Encrypt = false, bool AdjustedKey = false);

/// <summary>
/// Writes Blizzard MPQ archives that Storm and StormLib open: format version 1, or version 2
/// when a file uses LZMA. Files are split into sectors, each compressed with the chosen mask (or
/// imploded) and kept stored where compression does not help, optionally encrypted. A
/// "(listfile)" naming every file is added so names survive.
/// </summary>
public sealed class MpqWriter {
  private const string ListfileName = "(listfile)";
  private const uint HeaderMagic = 0x1A51504D;     // "MPQ\x1A"
  private const uint FlagFileExists = 0x80000000;
  private const uint FlagImplode = 0x00000100;
  private const uint FlagCompress = 0x00000200;
  private const uint FlagEncrypted = 0x00010000;
  private const uint FlagEmptyHashSlot = 0xFFFFFFFF;
  private const ushort SectorSizeShift = 3; // 4096-byte sectors, Storm's default

  private static readonly byte[] Version2Masks = [
    MpqCompression.Zlib, MpqCompression.Pkware, MpqCompression.Bzip2, MpqCompression.Lzma, MpqCompression.Sparse,
    MpqCompression.Sparse | MpqCompression.Zlib, MpqCompression.Sparse | MpqCompression.Bzip2,
    MpqCompression.AdpcmMono | MpqCompression.Huffman, MpqCompression.AdpcmStereo | MpqCompression.Huffman,
  ];

  private readonly List<(string Name, byte[] Data, MpqFileOptions Options)> _files = [];
  private readonly MpqFileOptions _defaults;

  /// <summary>Creates a writer whose files are stored (no compression, no encryption).</summary>
  public MpqWriter() : this(new MpqFileOptions()) { }

  /// <summary>Creates a writer that applies <paramref name="defaults"/> to every file added without options.</summary>
  public MpqWriter(MpqFileOptions defaults) {
    ArgumentNullException.ThrowIfNull(defaults);
    this._defaults = defaults;
  }

  /// <summary>
  /// Performs the add file operation.
  /// </summary>
  public void AddFile(string name, byte[] data) => this.AddFile(name, data, this._defaults);

  /// <summary>Adds a file stored as <paramref name="options"/> describe.</summary>
  public void AddFile(string name, byte[] data, MpqFileOptions options) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(name))
      throw new ArgumentException("Name must be non-empty.", nameof(name));
    if (string.Equals(name, ListfileName, StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("'(listfile)' is reserved -- it is generated automatically.", nameof(name));
    if (options.Implode && options.Compression != 0)
      throw new ArgumentException("A file is either imploded or compressed with a mask, not both.", nameof(options));
    this._files.Add((name.Replace('/', '\\'), data, options));
  }

  /// <summary>
  /// Writes the to to the supplied output.
  /// </summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var allNames = this._files.Select(f => f.Name).Append(ListfileName).ToList();
    var listfile = Encoding.UTF8.GetBytes(string.Join("\r\n", allNames));
    var listfileOptions = this._defaults with { Encrypt = false, AdjustedKey = false };
    var allFiles = new List<(string Name, byte[] Data, MpqFileOptions Options)>(this._files) { (ListfileName, listfile, listfileOptions) };

    var version2 = allFiles.Any(f => f.Options.Compression == MpqCompression.Lzma);
    foreach (var (name, _, options) in allFiles)
      if (version2 && options.Compression != 0 && Array.IndexOf(Version2Masks, options.Compression) < 0)
        throw new NotSupportedException($"Compression mask 0x{options.Compression:X2} of '{name}' cannot be combined with LZMA: version 2 archives allow only a fixed set of masks.");

    var headerSize = version2 ? 0x2C : 0x20;
    var sectorSize = 512 << SectorSizeShift;
    var hashTableEntries = NextPowerOfTwo(Math.Max(16, allFiles.Count * 2));

    var payload = new MemoryStream();
    var blocks = new (uint Offset, uint CompressedSize, uint OriginalSize, uint Flags)[allFiles.Count];
    for (var i = 0; i < allFiles.Count; ++i) {
      var (name, data, options) = allFiles[i];
      var offset = checked((uint)(headerSize + payload.Length));
      var (stored, flags) = EncodeFile(Encoding.UTF8.GetBytes(name), data, options, offset, sectorSize);
      payload.Write(stored);
      blocks[i] = (offset, (uint)stored.Length, (uint)data.Length, flags);
    }

    var hashTableOffset = checked((uint)(headerSize + payload.Length));
    var blockTableOffset = checked(hashTableOffset + (uint)hashTableEntries * 16u);
    var archiveSize = checked(blockTableOffset + (uint)allFiles.Count * 16u);

    var hashTable = new byte[hashTableEntries * 16];
    for (var i = 0; i < hashTable.Length; i += 16) {
      BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(i + 0), 0xFFFFFFFFu);
      BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(i + 4), 0xFFFFFFFFu);
      BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(i + 12), FlagEmptyHashSlot);
    }
    for (var i = 0; i < allFiles.Count; i++) {
      var name = Encoding.UTF8.GetBytes(allFiles[i].Name);
      var hashA = MpqCrypto.HashString(name, MpqCrypto.HashTypeNameA);
      var hashB = MpqCrypto.HashString(name, MpqCrypto.HashTypeNameB);
      var slot = MpqCrypto.HashString(name, MpqCrypto.HashTypeOffset) % (uint)hashTableEntries;
      while (BinaryPrimitives.ReadUInt32LittleEndian(hashTable.AsSpan((int)slot * 16 + 12)) != FlagEmptyHashSlot)
        slot = (slot + 1) % (uint)hashTableEntries;
      var off = (int)slot * 16;
      BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(off + 0), hashA);
      BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(off + 4), hashB);
      BinaryPrimitives.WriteUInt16LittleEndian(hashTable.AsSpan(off + 8), 0);
      BinaryPrimitives.WriteUInt16LittleEndian(hashTable.AsSpan(off + 10), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(hashTable.AsSpan(off + 12), (uint)i);
    }
    MpqCrypto.EncryptBlock(hashTable, MpqCrypto.HashTableKey);

    var blockTable = new byte[allFiles.Count * 16];
    for (var i = 0; i < allFiles.Count; i++) {
      var off = i * 16;
      BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(off + 0), blocks[i].Offset);
      BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(off + 4), blocks[i].CompressedSize);
      BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(off + 8), blocks[i].OriginalSize);
      BinaryPrimitives.WriteUInt32LittleEndian(blockTable.AsSpan(off + 12), blocks[i].Flags);
    }
    MpqCrypto.EncryptBlock(blockTable, MpqCrypto.BlockTableKey);

    var header = new byte[headerSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), HeaderMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)headerSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), archiveSize);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)(version2 ? 1 : 0));
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), SectorSizeShift);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), hashTableOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), blockTableOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)hashTableEntries);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)allFiles.Count);
    // Version 2: hi-block table offset (none) and the high words of both table offsets stay zero.

    output.Write(header);
    payload.Position = 0;
    payload.CopyTo(output);
    output.Write(hashTable);
    output.Write(blockTable);
  }

  /// <summary>Encodes one file's stored bytes (sector table plus sectors) and its block flags.</summary>
  private static (byte[] Stored, uint Flags) EncodeFile(byte[] name, byte[] data, MpqFileOptions options, uint blockOffset, int sectorSize) {
    var flags = FlagFileExists;
    if (data.Length == 0)
      return ([], flags);

    var compressed = options.Implode || options.Compression != 0;
    if (options.Implode) flags |= FlagImplode;
    else if (options.Compression != 0) flags |= FlagCompress;
    if (options.Encrypt) flags |= FlagEncrypted;
    if (options.Encrypt && options.AdjustedKey) flags |= MpqCrypto.FlagKeyV2;
    var key = options.Encrypt ? MpqCrypto.FileKey(name, blockOffset, (uint)data.Length, flags) : 0;

    var sectorCount = (data.Length + sectorSize - 1) / sectorSize;
    var sectors = new byte[sectorCount][];
    for (var i = 0; i < sectorCount; ++i) {
      var plain = data.AsSpan(i * sectorSize, Math.Min(sectorSize, data.Length - i * sectorSize));
      var sector = !compressed ? plain.ToArray()
        : options.Implode ? Implode(plain)
        : MpqCompression.Compress(plain, options.Compression);
      if (options.Encrypt)
        MpqCrypto.EncryptBlock(sector, unchecked(key + (uint)i));
      sectors[i] = sector;
    }

    using var stored = new MemoryStream();
    if (compressed) {
      var table = new byte[(sectorCount + 1) * 4];
      var position = (uint)table.Length;
      for (var i = 0; i < sectorCount; ++i) {
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 4), position);
        position += (uint)sectors[i].Length;
      }
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(sectorCount * 4), position);
      if (options.Encrypt)
        MpqCrypto.EncryptBlock(table, unchecked(key - 1));
      stored.Write(table);
    }
    foreach (var sector in sectors)
      stored.Write(sector);
    return (stored.ToArray(), flags);
  }

  /// <summary>PKWARE implode of one sector; kept stored when imploding does not make it shorter.</summary>
  private static byte[] Implode(ReadOnlySpan<byte> sector) {
    var imploded = PkwareDclEncoder.Compress(sector, PkwareDclMode.Binary, sector.Length < 0x600 ? 1024 : sector.Length < 0xC00 ? 2048 : 4096);
    return imploded.Length < sector.Length ? imploded : sector.ToArray();
  }

  private static int NextPowerOfTwo(int v) {
    if (v <= 1) return 1;
    var p = 1;
    while (p < v) p <<= 1;
    return p;
  }
}
