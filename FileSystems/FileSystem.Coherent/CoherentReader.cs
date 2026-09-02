#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Coherent;

/// <summary>
/// Reader for Mark Williams Coherent OS file system (1983-1995). Coherent is a
/// commercial UNIX V7/System V clone with a near-identical s5fs-style layout
/// but a distinct 16-bit magic (0xFD18 at superblock+504) and 14-character
/// directory entries like Minix v1's 14-name variant.
///
/// Block size is 512 by default (sometimes 1024). Inode size is 64 bytes with
/// 13 block pointers (10 direct + 1/2/3 indirect) stored as 3-byte addresses.
/// Root inode = 2.
///
/// Spec source: Mark Williams Company "The Coherent Operating System Reference
/// Manual" (1992); Coherent kernel header /usr/include/sys/filsys.h.
/// </summary>
public sealed class CoherentReader : IDisposable {

  private readonly byte[] _data;
  private readonly List<CoherentEntry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<CoherentEntry> Entries => this._entries;
  /// <summary>True once a valid coh_super_block (s_fname/s_fpack volume strings) was found.</summary>
  public bool Valid { get; private set; }
  /// <summary>Volume name from the superblock s_fname field (e.g. "noname").</summary>
  public string VolumeName { get; private set; } = "";
    /// <summary>
  /// Gets or sets the block size.
  /// </summary>
public int BlockSize { get; private set; } = 512;

  // The coh_super_block sits at file offset 0 (the copy the Linux sysv driver
  // reads fields from); a second identical copy lives at offset 512 (the one
  // detect_coherent checks). Coherent carries no numeric magic — the volume is
  // recognised by the s_fname/s_fpack strings.
  private const int SuperblockOffset = 0;
  private const int InodeSize = 64;
  internal const int CohFnameOffset = 0x1E4;
  internal const int CohFpackOffset = 0x1EA;
  private const int RootInode = 2;

    /// <summary>
  /// Initializes a new instance of <see cref="CoherentReader"/>.
  /// </summary>
public CoherentReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < 1024 + InodeSize)
      throw new InvalidDataException("Coherent: image too small for superblock.");
    var sb = this._data.AsSpan(SuperblockOffset);
    // Recognise the volume by the coh_super_block s_fname/s_fpack strings; the
    // filesystem has no numeric magic. Accept the canonical "noname"/"nopack"
    // (and the "xxxxx " / "xxxxx\n" mkfs placeholders the kernel also allows).
    var fname = Encoding.ASCII.GetString(sb.Slice(CohFnameOffset, 6));
    var fpack = Encoding.ASCII.GetString(sb.Slice(CohFpackOffset, 6));
    var fnameOk = fname is "noname" or "xxxxx ";
    var fpackOk = fpack is "nopack" or "xxxxx\n";
    if (!fnameOk || !fpackOk)
      throw new InvalidDataException(
        $"Coherent: not a coh_super_block (s_fname='{fname}', s_fpack='{fpack}').");
    this.Valid = true;
    this.VolumeName = fname.TrimEnd();
    this.ReadDirectory(RootInode, "");
  }

  internal long BlockOffset(uint block) => (long)block * this.BlockSize;
  internal long InodeTableOffset() => 2L * this.BlockSize;

  private byte[]? ReadInode(uint inum) {
    if (inum == 0) return null;
    var offset = this.InodeTableOffset() + (long)(inum - 1) * InodeSize;
    if (offset < 0 || offset + InodeSize > this._data.Length) return null;
    return this._data.AsSpan((int)offset, InodeSize).ToArray();
  }

  // PDP-11 3-byte zone address: disk [d0,d1,d2] → block d1 | d2<<8 | d0<<16
  // (the inverse of CoherentWriter.Write24 / the kernel's read3byte for PDP).
  private static uint Read24(ReadOnlySpan<byte> s) =>
    s[1] | ((uint)s[2] << 8) | ((uint)s[0] << 16);

  // PDP-11 middle-endian 32-bit: the two 16-bit halves are stored high-half
  // first, each little-endian.
  private static uint ReadPdp32(ReadOnlySpan<byte> s) =>
    s[2] | ((uint)s[3] << 8) | ((uint)s[0] << 16) | ((uint)s[1] << 24);

  private static (ushort mode, uint size, uint[] zones) ParseInode(byte[] inode) {
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(inode);
    var size = ReadPdp32(inode.AsSpan(8));
    var zones = new uint[13];
    for (var i = 0; i < 13; i++) zones[i] = Read24(inode.AsSpan(12 + i * 3));
    return (mode, size, zones);
  }

  private static bool IsDirectory(ushort mode) => (mode & 0xF000) == 0x4000;

  private byte[] ReadInodeData(uint inum) {
    var inode = this.ReadInode(inum);
    if (inode == null) return [];
    var (_, size, zones) = ParseInode(inode);
    return this.ReadZones(zones, size);
  }


  /// <summary>
  /// Emits the zeros a hole stands for, and counts them against what is left.
  /// </summary>
  /// <remarks>
  /// A zero pointer in one of these block maps does not mean the file ends there.
  /// It means the file holds nothing in that block, and every reader of the format
  /// hands back zeros for it and carries on. Stopping instead cut a file off at
  /// its first hole -- which is how a volume any of the reference tools left
  /// sparse would have been read here, not merely one this project wrote.
  /// </remarks>
  private static void AppendHole(Stream ms, ref long remaining, long bytes) {
    var toWrite = Math.Min(remaining, bytes);
    if (toWrite <= 0) return;

    var zeros = new byte[Math.Min(toWrite, 64 * 1024)];
    while (toWrite > 0) {
      var chunk = (int)Math.Min(zeros.Length, toWrite);
      ms.Write(zeros, 0, chunk);
      toWrite -= chunk;
      remaining -= chunk;
    }
  }

  private byte[] ReadZones(uint[] zones, uint size) {
    if (size == 0) return [];
    using var ms = new MemoryStream();
    long remaining = size;
    for (var i = 0; i < 10 && remaining > 0; i++) {
      // A zero zone is a hole the width of one block, not the end of the file.
      if (zones[i] == 0) { AppendHole(ms, ref remaining, this.BlockSize); continue; }
      this.AppendBlock(ms, zones[i], ref remaining);
    }
    if (remaining > 0) {
      if (zones[10] != 0) this.ReadIndirect(ms, zones[10], ref remaining, 1);
      else AppendHole(ms, ref remaining, (long)(this.BlockSize / 3) * this.BlockSize);
    }
    if (remaining > 0) {
      if (zones[11] != 0) this.ReadIndirect(ms, zones[11], ref remaining, 2);
      else AppendHole(ms, ref remaining, (long)(this.BlockSize / 3) * this.BlockSize * (this.BlockSize / 3));
    }
    if (remaining > 0) {
      if (zones[12] != 0) this.ReadIndirect(ms, zones[12], ref remaining, 3);
      else AppendHole(ms, ref remaining, (long)(this.BlockSize / 3) * this.BlockSize * (this.BlockSize / 3) * (this.BlockSize / 3));
    }
    return ms.ToArray();
  }

  private void AppendBlock(MemoryStream ms, uint block, ref long remaining) {
    var offset = this.BlockOffset(block);
    if (offset < 0 || offset + this.BlockSize > this._data.Length) return;
    var toRead = (int)Math.Min(remaining, this.BlockSize);
    ms.Write(this._data, (int)offset, toRead);
    remaining -= toRead;
  }

  private void ReadIndirect(MemoryStream ms, uint indirectBlock, ref long remaining, int level) {
    if (indirectBlock == 0 || remaining <= 0) return;
    var offset = this.BlockOffset(indirectBlock);
    if (offset + this.BlockSize > this._data.Length) return;
    var ptrsPerBlock = this.BlockSize / 3;
    for (var i = 0; i < ptrsPerBlock && remaining > 0; i++) {
      var ptr = Read24(this._data.AsSpan((int)offset + i * 3));
      if (ptr == 0) {
        // Everything this pointer would have addressed is hole.
        var span = (long)this.BlockSize;
        for (var l = 1; l < level; ++l) span *= ptrsPerBlock;
        AppendHole(ms, ref remaining, span);
        continue;
      }
      if (level == 1) this.AppendBlock(ms, ptr, ref remaining);
      else this.ReadIndirect(ms, ptr, ref remaining, level - 1);
    }
  }

  // Coherent directory entries are 16 bytes: u16 inode + 14-char name (NUL-padded).
  private void ReadDirectory(uint inum, string path) {
    var dirData = this.ReadInodeData(inum);
    if (dirData.Length == 0) return;
    var seen = new HashSet<uint>();
    const int entrySize = 16;
    for (var off = 0; off + entrySize <= dirData.Length; off += entrySize) {
      var ino = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(off));
      if (ino == 0) continue;
      var name = ReadNullTermString(dirData, off + 2, 14);
      if (name is "." or "..") continue;
      this.ProcessDirEntry(ino, name, path, seen);
    }
  }

  private void ProcessDirEntry(uint ino, string name, string path, HashSet<uint> seen) {
    var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
    var inode = this.ReadInode(ino);
    if (inode == null) return;
    var (mode, size, _) = ParseInode(inode);
    var isDir = IsDirectory(mode);
    this._entries.Add(new CoherentEntry {
      Name = fullPath,
      Size = isDir ? 0 : size,
      InodeNumber = (int)ino,
      IsDirectory = isDir,
    });
    if (isDir && seen.Add(ino)) this.ReadDirectory(ino, fullPath);
  }

  private static string ReadNullTermString(byte[] data, int offset, int maxLen) {
    var end = offset;
    var limit = Math.Min(offset + maxLen, data.Length);
    while (end < limit && data[end] != 0) end++;
    return Encoding.ASCII.GetString(data, offset, end - offset);
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(CoherentEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var inode = this.ReadInode((uint)entry.InodeNumber);
    if (inode == null) return [];
    var (_, size, _) = ParseInode(inode);
    var data = this.ReadInodeData((uint)entry.InodeNumber);
    if (data.Length > size) return data.AsSpan(0, (int)size).ToArray();
    return data;
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
