#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ApplePascal;

/// <summary>
/// Writes Apple UCSD Pascal disk volumes (Apple II / Apple III / Lisa Pascal
/// era, late 1970s–early 1980s). UCSD Pascal is an extent-based filesystem:
/// every file occupies a contiguous block range. The volume directory at disk
/// block 2 (file offset 0x400) holds the 26-byte volume header followed by up
/// to 77 file entries.
///
/// <para><b>Flat by spec.</b> Apple Pascal volumes are flat — there are no
/// subdirectories. Files written with '/' or '\' in the input name have those
/// chars stripped to a single 15-char short name. The writer enforces the
/// 77-entry maximum and rejects names that don't fit.</para>
///
/// <para>Always 512-byte blocks (spec-mandated); the only sizing knob is the
/// total block count. Typical sizes: 280 blocks (140 KB single-sided 5.25"
/// floppy), 560 (280 KB double-sided), or larger for ProFile / Lisa hard disks.
/// Pascal convention: volume size in blocks is a multiple of 8 (one allocation
/// tile = 8 blocks = 4 KB).</para>
/// </summary>
public sealed class ApplePascalWriter {

  private readonly List<(string Name, byte[] Data, int Kind)> _files = [];

  /// <summary>Adds a file to the volume. <paramref name="kind"/> is the Pascal
  /// file-kind code (0=untyped, 2=code, 3=text, 4=info, 5=data, 6=graf, 7=foto).</summary>
  public void AddFile(string name, byte[] data, int kind = 0) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    var leaf = name.Replace('\\', '/');
    var slash = leaf.LastIndexOf('/');
    if (slash >= 0) leaf = leaf[(slash + 1)..];
    if (leaf.Length > 15) leaf = leaf[..15];
    _files.Add((leaf.ToUpperInvariant(), data, kind));
  }

  /// <summary>Blocks a 16-bit Pascal block number can address.</summary>
  private const int MaxVolumeBlocks = 65535;

  /// <summary>
  /// Builds the image. <paramref name="volumeBlocks"/> is the total block count
  /// (typical: 280 for 140 KB floppy, 560 for 280 KB DS floppy, 1024+ for HD).
  /// Must be ≥ 8 and rounded up to a multiple of 8 (Pascal allocates in 8-block
  /// tiles). <paramref name="volumeName"/> may be 1..7 ASCII chars.
  /// </summary>
  public byte[] Build(int volumeBlocks = 280, string volumeName = "PASCAL") {
    if (volumeBlocks < 8) throw new ArgumentException("volumeBlocks must be >= 8.", nameof(volumeBlocks));
    // Round up to 8-block tile alignment.
    volumeBlocks = ((volumeBlocks + 7) / 8) * 8;
    if (_files.Count > ApplePascalReader.MaxEntries)
      throw new InvalidOperationException(
        $"Apple Pascal supports at most {ApplePascalReader.MaxEntries} files; got {_files.Count}.");
    if (string.IsNullOrEmpty(volumeName)) volumeName = "PASCAL";
    if (volumeName.Length > 7) volumeName = volumeName[..7];

    // Pascal addresses blocks in 16 bits, so a volume larger than that cannot
    // be described at all. Multiplying the block count out first turned an
    // over-large request into an arithmetic overflow.
    if (volumeBlocks > MaxVolumeBlocks)
      throw new InvalidOperationException(
        $"Apple Pascal: a volume of {volumeBlocks:N0} blocks exceeds the {MaxVolumeBlocks:N0} " +
        $"blocks a 16-bit block number can address ({(long)MaxVolumeBlocks * ApplePascalReader.BlockSize:N0} bytes).");

    var img = new byte[(long)volumeBlocks * ApplePascalReader.BlockSize];

    // Volume header: type=0, first=0, next=6, name + 7 chars, total blocks,
    // file count, first-block-access (set to next), packed Pascal date (zero).
    var hdr = img.AsSpan(ApplePascalReader.DirectoryOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(0, 2), 0);  // first block
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(2, 2), 6);  // next block (== first file's start)
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(4, 2), 0);  // entry type = volume header
    hdr[6] = (byte)volumeName.Length;
    Encoding.ASCII.GetBytes(volumeName.ToUpperInvariant()).CopyTo(hdr[7..]);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(14, 2), (ushort)volumeBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(16, 2), (ushort)_files.Count);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(18, 2), 6); // first to access

    // Lay out files contiguously starting at block 6 (right after the 4-block
    // volume directory region — blocks 2..5 hold up to 4 × 512 = 2048 bytes
    // for the header + 78 × 26 = 2054 bytes of entries — but UCSD reserves 6
    // blocks total, starting file data at block 6.).
    var nextFreeBlock = 6;
    for (var i = 0; i < _files.Count; i++) {
      var (fname, data, kind) = _files[i];
      var blocksNeeded = (data.Length + ApplePascalReader.BlockSize - 1) / ApplePascalReader.BlockSize;
      if (blocksNeeded == 0) blocksNeeded = 1;
      var startBlock = nextFreeBlock;
      var endBlock = startBlock + blocksNeeded;
      if (endBlock > volumeBlocks)
        throw new InvalidOperationException(
          $"Apple Pascal: not enough room for '{fname}' — needs {blocksNeeded} blocks but only " +
          $"{volumeBlocks - nextFreeBlock} available. Increase volumeBlocks.");

      // File entry layout: 26 bytes.
      var entryOffset = ApplePascalReader.DirectoryOffset + (i + 1) * ApplePascalReader.EntrySize;
      var entry = img.AsSpan(entryOffset, ApplePascalReader.EntrySize);
      BinaryPrimitives.WriteUInt16LittleEndian(entry[..2], (ushort)startBlock);
      BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(2, 2), (ushort)endBlock);
      BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(4, 2), (ushort)kind);
      entry[6] = (byte)fname.Length;
      Encoding.ASCII.GetBytes(fname).CopyTo(entry[7..]);
      // bytes-in-last-block (offset 22 within the 26-byte entry).
      var bytesInLast = data.Length - (blocksNeeded - 1) * ApplePascalReader.BlockSize;
      if (bytesInLast <= 0) bytesInLast = ApplePascalReader.BlockSize;
      BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(22, 2), (ushort)bytesInLast);

      // Copy payload into the file's contiguous extent.
      data.CopyTo(img.AsSpan(startBlock * ApplePascalReader.BlockSize, data.Length));

      nextFreeBlock = endBlock;
    }
    return img;
  }
}
