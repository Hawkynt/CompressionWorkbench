#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jffs2;

/// <summary>
/// True in-place R/W mutation of a JFFS2 image. JFFS2 is log-structured: a
/// "modify" operation appends a fresh node (inode or dirent) at the end of the
/// log with an incremented version number. Old nodes stay byte-identical at
/// their original offsets; the reader resolves an inode or (parent-inode, name)
/// pair to its highest-version node.
///
/// <para>This modifier preserves all existing bytes — only the tail of the
/// log grows. Garbage collection (compaction of obsolete nodes) is deliberately
/// out of scope here; that belongs to the defragmenter.</para>
///
/// <para>Honest scope: appended data nodes carry their bytes uncompressed
/// (<c>compr=0x00 JFFS2_COMPR_NONE</c>), matching what <see cref="Jffs2Writer"/>
/// emits on Create. Compressed-body emission (LZO / zlib) is out of scope.</para>
/// </summary>
public static class Jffs2InPlaceModifier {
  private const ushort Magic = 0x1985;
  private const ushort NodeTypeDirent = 0xE001;
  private const ushort NodeTypeInode = 0xE002;
  private const int InodeNodeHeaderSize = 68;
  private const int DirentNodeHeaderSize = 40;
  private const int CommonHeaderSize = 12;
  private const uint RootInode = 1;
  private const byte DtReg = 8;
  private const byte DtDir = 4;
  private const uint ModeRegular = 0x81A4; // S_IFREG | 0644
  private const uint ModeDirectory = 0x41ED; // S_IFDIR | 0755
  private const ushort NodeTypePadding = 0x2004;

  /// <summary>
  /// Appends new files to the log. If an input name matches an existing file's
  /// path, a new inode node is appended with the same <c>ino</c> and
  /// <c>version = oldVersion + 1</c>, transparently replacing the prior
  /// content. Otherwise a fresh inode (plus any missing parent-directory inode
  /// chain) and a dirent are appended at the tail. Existing bytes are never
  /// rewritten.
  /// </summary>
  public static void Add(Stream image, IReadOnlyList<(string Name, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    if (inputs.Count == 0) return;

    image.Position = 0;
    var state = LogState.Scan(image);

    var nowSec = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var appendNodes = new List<byte[]>();

    foreach (var (rawName, data) in inputs) {
      if (string.IsNullOrEmpty(rawName)) continue;
      var segments = SplitPath(rawName);
      if (segments.Length == 0) continue;

      // Walk / create parent-directory chain.
      var parentInode = RootInode;
      var pathSoFar = string.Empty;
      for (var i = 0; i < segments.Length - 1; ++i) {
        pathSoFar = pathSoFar.Length == 0 ? segments[i] : pathSoFar + "/" + segments[i];
        if (!state.DirectoryInodes.TryGetValue(pathSoFar, out var dirInode)) {
          dirInode = state.AllocateInode();
          state.DirectoryInodes[pathSoFar] = dirInode;

          appendNodes.Add(BuildInodeNode(
            inode: dirInode,
            version: 1,
            mode: ModeDirectory,
            size: 0,
            offsetInFile: 0,
            data: [],
            mtime: nowSec));
          appendNodes.Add(BuildDirentNode(
            parentInode: parentInode,
            inode: dirInode,
            name: segments[i],
            type: DtDir,
            version: 1,
            mtime: nowSec));

          // Track for subsequent inputs in this same Add call.
          state.NoteInodeVersion(dirInode, 1);
          state.NoteDirentVersion(parentInode, segments[i], 1);
        }

        parentInode = dirInode;
      }

      var leafName = segments[^1];
      var fullPath = string.Join('/', segments);

      if (state.FileInodeByPath.TryGetValue(fullPath, out var existingIno)) {
        // Replace: append fresh inode node with bumped version, same ino.
        var oldVersion = state.InodeVersionMax.GetValueOrDefault(existingIno, 0u);
        var newVersion = oldVersion + 1;
        appendNodes.Add(BuildInodeNode(
          inode: existingIno,
          version: newVersion,
          mode: ModeRegular,
          size: (uint)data.Length,
          offsetInFile: 0,
          data: data,
          mtime: nowSec));
        state.NoteInodeVersion(existingIno, newVersion);
      } else {
        // Fresh add: allocate new ino + emit inode + dirent. If a prior
        // (parent, leaf) dirent exists — including an unlink from a previous
        // Remove — the new dirent must carry a version strictly greater than
        // that prior one, otherwise the highest-version-wins resolution would
        // surface the stale dirent instead of the fresh one.
        var newIno = state.AllocateInode();
        var direntKey = (parentInode, leafName);
        var priorDirentVersion = state.LatestDirentByKey.TryGetValue(direntKey, out var prior) ? prior.Version : 0u;
        var direntVersion = priorDirentVersion + 1;
        appendNodes.Add(BuildInodeNode(
          inode: newIno,
          version: 1,
          mode: ModeRegular,
          size: (uint)data.Length,
          offsetInFile: 0,
          data: data,
          mtime: nowSec));
        appendNodes.Add(BuildDirentNode(
          parentInode: parentInode,
          inode: newIno,
          name: leafName,
          type: DtReg,
          version: direntVersion,
          mtime: nowSec));
        state.NoteInodeVersion(newIno, 1);
        state.LatestDirentByKey[direntKey] = new LogState.DirentRecord(parentInode, newIno, leafName, direntVersion);
        state.FileInodeByPath[fullPath] = newIno;
      }
    }

    AppendNodes(image, state.EndOfLogOffset, state.EraseBlockSize, appendNodes);
  }

  /// <summary>
  /// Replaces the content of <paramref name="name"/> with <paramref name="newData"/>.
  /// Resolves the existing inode and appends a new inode node carrying the same
  /// <c>ino</c>, <c>version = oldVersion + 1</c>, and the fresh data bytes.
  /// Existing nodes stay byte-identical at their original offsets. Throws if
  /// the name does not resolve to an existing file.
  /// </summary>
  public static void Replace(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);

    image.Position = 0;
    var state = LogState.Scan(image);
    var normalized = string.Join('/', SplitPath(name));
    if (!state.FileInodeByPath.TryGetValue(normalized, out var ino))
      throw new FileNotFoundException($"JFFS2 image has no file '{name}'.");

    var oldVersion = state.InodeVersionMax.GetValueOrDefault(ino, 0u);
    var newVersion = oldVersion + 1;

    var node = BuildInodeNode(
      inode: ino,
      version: newVersion,
      mode: ModeRegular,
      size: (uint)newData.Length,
      offsetInFile: 0,
      data: newData,
      mtime: (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    AppendNodes(image, state.EndOfLogOffset, state.EraseBlockSize, [node]);
  }

  /// <summary>
  /// Removes <paramref name="name"/> by appending an unlink dirent (the JFFS2
  /// idiom: <c>ino=0</c>) for the same <c>(pino, name)</c> pair with
  /// <c>version = oldVersion + 1</c>. The original nodes stay byte-identical;
  /// the reader's highest-version-wins resolution sees the unlink and treats
  /// the file as gone. Throws if the name does not resolve to a live dirent.
  /// </summary>
  public static void Remove(Stream image, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    image.Position = 0;
    var state = LogState.Scan(image);
    var normalized = string.Join('/', SplitPath(name));
    if (!state.LiveDirentByPath.TryGetValue(normalized, out var live))
      throw new FileNotFoundException($"JFFS2 image has no live dirent for '{name}'.");

    var newVersion = live.Version + 1;
    var node = BuildDirentNode(
      parentInode: live.ParentInode,
      inode: 0u, // 0 = unlink marker
      name: live.LeafName,
      type: DtReg,
      version: newVersion,
      mtime: (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    AppendNodes(image, state.EndOfLogOffset, state.EraseBlockSize, [node]);
  }

  // ── Append mechanics ─────────────────────────────────────────────────

  /// <summary>
  /// Writes <paramref name="nodes"/> sequentially starting at
  /// <paramref name="endOfLogOffset"/>, padding each to a 4-byte boundary, and
  /// grows the stream to a multiple of <paramref name="eraseBlockSize"/>
  /// (filling the new tail with 0xFF — JFFS2's erased-flash state).
  /// </summary>
  private static void AppendNodes(Stream image, long endOfLogOffset, int eraseBlockSize, IReadOnlyList<byte[]> nodes) {
    if (nodes.Count == 0) return;

    long totalAppend = 0;
    foreach (var n in nodes) totalAppend += Align4(n.Length);

    var requiredLength = endOfLogOffset + totalAppend;
    var newImageSize = ((requiredLength + eraseBlockSize - 1) / eraseBlockSize) * eraseBlockSize;
    if (newImageSize < eraseBlockSize) newImageSize = eraseBlockSize;

    var oldLength = image.Length;
    if (newImageSize > oldLength) {
      image.SetLength(newImageSize);
      // Fill the freshly-grown region with 0xFF (erased flash).
      image.Position = oldLength;
      WriteFill(image, 0xFF, newImageSize - oldLength);
    }

    image.Position = endOfLogOffset;
    foreach (var n in nodes) {
      image.Write(n, 0, n.Length);
      var pad = Align4(n.Length) - n.Length;
      if (pad > 0) WriteFill(image, 0xFF, pad);
    }

    // Fill whatever tail remains inside the current (possibly pre-existing)
    // erase block with 0xFF so the log boundary is unambiguous.
    var cursor = endOfLogOffset + totalAppend;
    if (cursor < image.Length) {
      image.Position = cursor;
      WriteFill(image, 0xFF, image.Length - cursor);
    }
  }

  private static void WriteFill(Stream image, byte value, long count) {
    Span<byte> chunk = stackalloc byte[1024];
    chunk.Fill(value);
    while (count > 0) {
      var n = (int)Math.Min(count, chunk.Length);
      image.Write(chunk[..n]);
      count -= n;
    }
  }

  // ── Log scanner / mutation state ─────────────────────────────────────

  /// <summary>
  /// Snapshot of the existing log that the modifier needs to append correctly:
  /// where the live nodes end (so appends extend the log), the highest version
  /// per inode and per (pino, name) pair, the inode currently bound to each
  /// full path (so Replace and Remove resolve names), and the next free inode
  /// number to hand out for fresh adds.
  /// </summary>
  private sealed class LogState {
    public long EndOfLogOffset { get; private set; }
    public int EraseBlockSize { get; private set; }
    public Dictionary<uint, uint> InodeVersionMax { get; } = new();
    public Dictionary<(uint Pino, string Name), DirentRecord> LatestDirentByKey { get; } = new();
    public Dictionary<string, uint> FileInodeByPath { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, uint> DirectoryInodes { get; } = new(StringComparer.Ordinal) {
      [string.Empty] = RootInode,
    };
    public Dictionary<string, DirentRecord> LiveDirentByPath { get; } = new(StringComparer.Ordinal);
    public uint NextInode { get; private set; } = 2;

    public uint AllocateInode() {
      var id = this.NextInode++;
      return id;
    }

    public void NoteInodeVersion(uint ino, uint version) {
      if (!this.InodeVersionMax.TryGetValue(ino, out var existing) || version > existing)
        this.InodeVersionMax[ino] = version;
      if (ino >= this.NextInode) this.NextInode = ino + 1;
    }

    public void NoteDirentVersion(uint pino, string leafName, uint version) {
      var key = (pino, leafName);
      if (!this.LatestDirentByKey.TryGetValue(key, out var existing) || version >= existing.Version) {
        // No-op here: in-place call paths set ino separately via FileInodeByPath.
        // This is only used to bump versions for synthetic directory creation
        // within an Add batch.
        this.LatestDirentByKey[key] = new DirentRecord(pino, 0, leafName, version);
      }
    }

    public sealed record DirentRecord(uint ParentInode, uint Inode, string LeafName, uint Version);

    public static LogState Scan(Stream image) {
      var state = new LogState();
      using var ms = new MemoryStream();
      image.Position = 0;
      image.CopyTo(ms);
      var data = ms.ToArray();
      var span = data.AsSpan();

      state.EraseBlockSize = DetectEraseBlockSize(data);

      // ── Pass 1: collect raw nodes ──────────────────────────────────
      var inodeNodes = new List<(uint Ino, uint Version, uint Mode)>();
      var direntNodes = new List<(uint Pino, uint Ino, string Name, uint Version)>();
      var lastNodeEnd = 0L;

      var off = 0;
      while (off + CommonHeaderSize <= span.Length) {
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(off, 2));
        if (magic != Magic) {
          // Detect end of log: a run of 0xFF that goes to (or near) end.
          if (data[off] == 0xFF) break;
          off += 4;
          continue;
        }
        var nodeType = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(off + 2, 2));
        var totLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 4, 4));
        if (totLen < CommonHeaderSize || totLen > span.Length || off + (int)totLen > span.Length) {
          off += 4;
          continue;
        }
        var aligned = ((int)totLen + 3) & ~3;

        switch (nodeType) {
          case NodeTypeInode when off + InodeNodeHeaderSize <= span.Length: {
            var ino = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 12, 4));
            var version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 16, 4));
            var mode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 20, 4));
            inodeNodes.Add((ino, version, mode));
            if (!state.InodeVersionMax.TryGetValue(ino, out var v) || version > v)
              state.InodeVersionMax[ino] = version;
            if (ino >= state.NextInode) state.NextInode = ino + 1;
            break;
          }
          case NodeTypeDirent when off + DirentNodeHeaderSize <= span.Length: {
            var pino = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 12, 4));
            var version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 16, 4));
            var ino = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 20, 4));
            var nsize = span[off + 28];
            if (nsize > 0 && nsize <= 128 && off + DirentNodeHeaderSize + nsize <= span.Length) {
              var name = Encoding.UTF8.GetString(span.Slice(off + DirentNodeHeaderSize, nsize));
              direntNodes.Add((pino, ino, name, version));
              if (ino >= state.NextInode && ino != 0) state.NextInode = ino + 1;
            }
            break;
          }
        }

        lastNodeEnd = off + aligned;
        off += aligned;
      }

      state.EndOfLogOffset = lastNodeEnd;

      // ── Pass 2: resolve highest-version dirent per (pino, name) ────
      foreach (var (pino, ino, name, version) in direntNodes) {
        var key = (pino, name);
        if (!state.LatestDirentByKey.TryGetValue(key, out var existing) || version > existing.Version) {
          state.LatestDirentByKey[key] = new DirentRecord(pino, ino, name, version);
        }
      }

      // ── Pass 3: build path → inode map from LIVE dirents ───────────
      var dirInodeToPath = new Dictionary<uint, string> { [RootInode] = string.Empty };
      var inodeMode = new Dictionary<uint, uint>();
      foreach (var (ino, version, mode) in inodeNodes) {
        if (!state.InodeVersionMax.TryGetValue(ino, out var maxv)) continue;
        if (version == maxv) inodeMode[ino] = mode;
      }

      // BFS by parent inode so directories are resolved before their children.
      var resolvedDirs = new Queue<uint>();
      resolvedDirs.Enqueue(RootInode);
      var processedDirs = new HashSet<uint> { RootInode };
      while (resolvedDirs.Count > 0) {
        var parent = resolvedDirs.Dequeue();
        var parentPath = dirInodeToPath[parent];
        foreach (var kv in state.LatestDirentByKey) {
          if (kv.Key.Pino != parent) continue;
          var rec = kv.Value;
          if (rec.Inode == 0) continue; // unlink
          var fullPath = parentPath.Length == 0 ? rec.LeafName : parentPath + "/" + rec.LeafName;
          state.LiveDirentByPath[fullPath] = rec;

          var isDir = inodeMode.TryGetValue(rec.Inode, out var m) && (m & 0xF000) == 0x4000;
          if (isDir && processedDirs.Add(rec.Inode)) {
            dirInodeToPath[rec.Inode] = fullPath;
            state.DirectoryInodes[fullPath] = rec.Inode;
            resolvedDirs.Enqueue(rec.Inode);
          } else if (!isDir) {
            state.FileInodeByPath[fullPath] = rec.Inode;
          }
        }
      }

      if (state.EraseBlockSize == 0) state.EraseBlockSize = Jffs2Writer.DefaultEraseBlockSize;
      return state;
    }

    private static int DetectEraseBlockSize(byte[] data) {
      // Mirror the scanner's heuristic so a freshly-grown image keeps the same
      // erase-block alignment the writer chose at create time.
      int[] candidates = [0x1000, 0x4000, 0x10000, 0x20000, 0x40000, 0x100000, 0x400000];
      foreach (var candidate in candidates) {
        if (candidate > data.Length) break;
        if (data.Length % candidate != 0) continue;
        var hits = 0;
        var count = data.Length / candidate;
        for (var i = 0; i < count; ++i) {
          var off = i * candidate;
          if (off + 2 > data.Length) break;
          if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off, 2)) == Magic) ++hits;
        }
        if (count > 0 && hits * 2 >= count) return candidate;
      }
      return 0;
    }
  }

  // ── Node builders (CRC layout copied from Jffs2Writer to keep parity) ─

  private static byte[] BuildInodeNode(uint inode, uint version, uint mode, uint size, uint offsetInFile, byte[] data, uint mtime) {
    var totLen = (uint)(InodeNodeHeaderSize + data.Length);
    var node = new byte[totLen];

    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(0, 2), Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2, 2), NodeTypeInode);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4, 4), totLen);

    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(12, 4), inode);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(16, 4), version);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(20, 4), mode);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(24, 2), 0); // uid
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(26, 2), 0); // gid
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(28, 4), size); // isize
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(32, 4), mtime); // atime
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(36, 4), mtime); // mtime
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(40, 4), mtime); // ctime
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(44, 4), offsetInFile);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(48, 4), (uint)data.Length); // csize
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(52, 4), (uint)data.Length); // dsize
    node[56] = 0x00; // compr = JFFS2_COMPR_NONE
    node[57] = 0x00; // usercompr
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(58, 2), 0); // flags

    if (data.Length > 0)
      data.CopyTo(node, InodeNodeHeaderSize);

    var dataCrc = Jffs2Crc.Compute(data);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(60, 4), dataCrc);

    // hdr_crc before node_crc: node_crc runs across bytes 0..59 and so covers it.
    var hdrCrc = Jffs2Crc.Compute(node.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8, 4), hdrCrc);

    var nodeCrc = Jffs2Crc.Compute(node.AsSpan(0, InodeNodeHeaderSize - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(64, 4), nodeCrc);

    return node;
  }

  private static byte[] BuildDirentNode(uint parentInode, uint inode, string name, byte type, uint version, uint mtime) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var totLen = (uint)(DirentNodeHeaderSize + nameBytes.Length);
    var node = new byte[totLen];

    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(0, 2), Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2, 2), NodeTypeDirent);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4, 4), totLen);

    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(12, 4), parentInode);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(16, 4), version);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(20, 4), inode);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(24, 4), mtime);
    node[28] = (byte)nameBytes.Length;
    node[29] = type;

    nameBytes.CopyTo(node, DirentNodeHeaderSize);

    var nameCrc = Jffs2Crc.Compute(nameBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(36, 4), nameCrc);

    // hdr_crc before node_crc: node_crc runs across bytes 0..31 and so covers it.
    var hdrCrc = Jffs2Crc.Compute(node.AsSpan(0, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8, 4), hdrCrc);

    var nodeCrc = Jffs2Crc.Compute(node.AsSpan(0, DirentNodeHeaderSize - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(32, 4), nodeCrc);

    return node;
  }

  private static int Align4(int value) => (value + 3) & ~3;
  private static long Align4(long value) => (value + 3) & ~3;

  private static string[] SplitPath(string name)
    => name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
