#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Bkf;

/// <summary>
/// Adds and removes files and folders in a Microsoft NTBackup (.bkf) medium written in
/// Microsoft Tape Format 1.00a.
/// </summary>
/// <remarks>
/// <para>MTF is a linear tape format: every DBLK records its Format Logical Address and
/// Control Block ID, and FILE DBLKs their directory and file IDs, so a block cannot be taken
/// out or slipped in where it stands. An edit therefore writes the Data Set anew into the same
/// stream: every DBLK that stays keeps its own bytes — dates, attributes, OS-specific data,
/// names — and every data stream (security, alternate data, checksums) byte for byte; only the
/// positional fields and checksums are written from the new layout. A new file goes after
/// the files of its folder, a new folder's DIRB at the end of the set.</para>
/// <para>Refused, with the medium left as it was: more than one Data Set, a Media Based
/// Catalog (its set map and file/directory detail record the old positions), a medium that
/// spans to another, and a Data Set with more than one volume.</para>
/// </remarks>
public static class BkfEditor {
  /// <summary>Adds or replaces the file at <paramref name="path"/>, creating its folders.</summary>
  public static void AddFile(Stream archive, string path, byte[] data, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(data);
    var target = BkfTree.Normalize(path);
    var slash = target.LastIndexOf('/');
    if (target.Length == 0 || slash == target.Length - 1)
      throw new ArgumentException("BKF: a file needs a name.", nameof(path));
    Edit(archive, (set, flb, now) => {
      RemovePath(set, target, filesOnly: true);
      var folder = slash < 0 ? "" : target[..slash];
      var at = EnsureFolder(set, folder, now, flb, gainsFile: true);
      set.Insert(at, MtfComposer.File(target[(slash + 1)..], data, modified, now, flb));
      return true;
    });
  }

  /// <summary>Adds a folder (and its parents) unless it is there already.</summary>
  public static void AddDirectory(Stream archive, string path) {
    var target = BkfTree.Normalize(path);
    if (target.Length == 0) return;
    Edit(archive, (set, flb, now) => {
      var before = set.Count;
      EnsureFolder(set, target, now, flb, gainsFile: false);
      return set.Count != before;
    });
  }

  /// <summary>Removes the file, or the folder with everything in it, at <paramref name="path"/>; false when there is nothing there.</summary>
  public static bool Remove(Stream archive, string path) {
    var target = BkfTree.Normalize(path);
    var removed = false;
    Edit(archive, (set, _, _) => removed = RemovePath(set, target, filesOnly: false));
    return removed;
  }

  // ── The edit ──────────────────────────────────────────────────────────────

  private static void Edit(Stream archive, Func<List<MtfBlock>, int, DateTime, bool> change) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanSeek || !archive.CanRead || !archive.CanWrite)
      throw new ArgumentException("BKF editing needs a readable, writable, seekable stream.", nameof(archive));
    var media = MtfMedia.Read(archive, keepData: true);
    if (media.Sets.Count != 1)
      throw new NotSupportedException($"BKF: the medium holds {media.Sets.Count} Data Sets; only a single-set backup is edited.");
    var set = media.Sets[0];
    if (media.MediaBasedCatalogType != 0 || set.Eset is null || set.Eset.Streams.Count > 0 || set.ExtraEsets > 0)
      throw new NotSupportedException("BKF: the medium carries a Media Based Catalog, which records the old block positions; it is not edited.");
    if (media.HasEndOfTapeMarker || set.Blocks.Any(b => (b.Attributes & Mtf.BlockContinuationBit) != 0)
        || (set.Sset.Attributes & Mtf.BlockContinuationBit) != 0)
      throw new NotSupportedException("BKF: the Data Set spans media; it is not edited.");
    if (set.Blocks.Count(b => b.Type == "VOLB") > 1)
      throw new NotSupportedException("BKF: the Data Set holds more than one volume; it is not edited.");

    var blocks = set.Blocks.ToList();
    if (!change(blocks, media.FormatLogicalBlockSize, DateTime.Now)) return;

    using var output = new MemoryStream();
    MtfComposer.Write(output, media.Tape, [new MtfComposer.DataSet(set.Sset, blocks, set.Eset)],
      media.FormatLogicalBlockSize, s => s.Data ?? []);
    archive.Position = 0;
    output.Position = 0;
    output.CopyTo(archive);
    archive.SetLength(output.Length);
    archive.Flush();
  }

  /// <summary>The folder each block belongs to: a DIRB names it, a FILE takes the last DIRB's.</summary>
  private static List<(MtfBlock Block, string Folder, string? File)> Paths(List<MtfBlock> blocks) {
    var result = new List<(MtfBlock, string, string?)>(blocks.Count);
    var folder = "";
    foreach (var b in blocks) {
      switch (b.Type) {
        case "VOLB":
          folder = "";
          result.Add((b, "", null));
          break;
        case "DIRB":
          folder = FolderOf(b);
          result.Add((b, folder, null));
          break;
        case "FILE":
          result.Add((b, folder, NameOf(b)));
          break;
        default:
          result.Add((b, folder, null));
          break;
      }
    }
    return result;
  }

  private static string FolderOf(MtfBlock dirb) {
    var name = (dirb.U32(52) & Mtf.DirbPathInStreamBit) != 0
      ? Mtf.DecodeString(dirb.Stream("PNAM")?.Data ?? [], dirb.StringType)
      : dirb.String(80);
    return string.Join('/', name.Split('\0', StringSplitOptions.RemoveEmptyEntries));
  }

  private static string NameOf(MtfBlock file) {
    var name = (file.U32(52) & Mtf.FileNameInStreamBit) != 0
      ? Mtf.DecodeString(file.Stream("FNAM")?.Data ?? [], file.StringType)
      : file.String(84);
    return name.TrimEnd('\0');
  }

  private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

  /// <summary>Removes the file at the path or, unless <paramref name="filesOnly"/>, the folder there and everything in it.</summary>
  private static bool RemovePath(List<MtfBlock> blocks, string path, bool filesOnly) {
    var paths = Paths(blocks);
    var drop = new HashSet<MtfBlock>();
    for (var i = 0; i < paths.Count; ++i) {
      var (block, folder, file) = paths[i];
      var inFolder = !filesOnly && path.Length > 0 && (folder == path || folder.StartsWith(path + "/", StringComparison.Ordinal));
      var isFile = file != null && string.Equals(Join(folder, file), path, StringComparison.Ordinal);
      if (block.Type == "VOLB") continue;
      if (inFolder || isFile) {
        drop.Add(block);
        // A CFIL describes the FILE before it and goes with it.
        if (isFile && i + 1 < paths.Count && paths[i + 1].Block.Type == "CFIL") drop.Add(paths[i + 1].Block);
      }
    }
    if (filesOnly && paths.Any(p => p.Block.Type == "DIRB" && p.Folder == path && path.Length > 0))
      throw new IOException($"BKF: '{path}' is a folder.");
    blocks.RemoveAll(drop.Contains);
    return drop.Count > 0;
  }

  /// <summary>
  /// Makes sure a DIRB names the folder and each of its parents; returns the index right after
  /// the folder's last block, where a new FILE of it goes.
  /// </summary>
  private static int EnsureFolder(List<MtfBlock> blocks, string folder, DateTime now, int flb, bool gainsFile) {
    var parts = folder.Length == 0 ? [] : folder.Split('/');
    for (var level = 0; level <= parts.Length; ++level) {
      var path = string.Join('/', parts[..level]);
      var paths = Paths(blocks);
      if (paths.Any(p => p.Block.Type == "DIRB" && p.Folder == path)) continue;
      var dirb = MtfComposer.Dirb(path, now, now, empty: level == parts.Length && !gainsFile, flb);
      if (path.Length == 0) {
        // The root comes right after the volume, before any other folder.
        var volume = blocks.FindIndex(b => b.Type == "VOLB");
        blocks.Insert(volume + 1, dirb);
      } else {
        blocks.Add(dirb);
      }
    }

    // A folder that gains a file or subfolder is no longer empty.
    var all = Paths(blocks);
    var index = all.FindIndex(p => p.Block.Type == "DIRB" && p.Folder == folder);
    bool Gains(string f) => (f == folder && gainsFile)
      || (f != folder && (f.Length == 0 || folder.StartsWith(f + "/", StringComparison.Ordinal)));
    foreach (var p in all.Where(p => p.Block.Type == "DIRB" && Gains(p.Folder)))
      BinaryPrimitives.WriteUInt32LittleEndian(p.Block.Dblk.AsSpan(52), p.Block.U32(52) & ~Mtf.DirbEmptyBit);
    var end = index + 1;
    while (end < blocks.Count && blocks[end].Type is not ("DIRB" or "VOLB")) ++end;
    return end;
  }
}
