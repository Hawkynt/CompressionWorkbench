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
/// <para>A medium with a Type 1 Media Based Catalog — every NTBackup backup file has one —
/// gets its catalog written anew for every Data Set (<see cref="MtfCatalog"/>), so its
/// File/Directory Detail and Set Map describe the edited sets. In a medium with several Data
/// Sets a path names its set as the reader lists it, <c>set-NN/…</c>; the other sets keep
/// their blocks.</para>
/// <para>Refused, with the medium left as it was: a Type 2 catalog, a medium that spans to
/// another, and a Data Set with more than one volume.</para>
/// </remarks>
public static class BkfEditor {
  /// <summary>Adds or replaces the file at <paramref name="path"/>, creating its folders.</summary>
  public static void AddFile(Stream archive, string path, byte[] data, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(data);
    var target = BkfTree.Normalize(path);
    if (target.Length == 0 || target.EndsWith('/'))
      throw new ArgumentException("BKF: a file needs a name.", nameof(path));
    Edit(archive, target, (set, inSet, flb, now) => {
      var slash = inSet.LastIndexOf('/');
      if (inSet.Length == 0) throw new ArgumentException("BKF: a file needs a name.", nameof(path));
      RemovePath(set, inSet, filesOnly: true);
      var folder = slash < 0 ? "" : inSet[..slash];
      var at = EnsureFolder(set, folder, now, flb, gainsFile: true);
      set.Insert(at, MtfComposer.File(inSet[(slash + 1)..], data, modified, now, flb));
      return true;
    });
  }

  /// <summary>Adds a folder (and its parents) unless it is there already.</summary>
  public static void AddDirectory(Stream archive, string path) {
    var target = BkfTree.Normalize(path);
    if (target.Length == 0) return;
    Edit(archive, target, (set, inSet, flb, now) => {
      if (inSet.Length == 0) return false;
      var before = set.Count;
      EnsureFolder(set, inSet, now, flb, gainsFile: false);
      return set.Count != before;
    });
  }

  /// <summary>Removes the file, or the folder with everything in it, at <paramref name="path"/>; false when there is nothing there.</summary>
  public static bool Remove(Stream archive, string path) {
    var target = BkfTree.Normalize(path);
    var removed = false;
    Edit(archive, target, (set, inSet, _, _) => removed = inSet.Length > 0 && RemovePath(set, inSet, filesOnly: false));
    return removed;
  }

  // ── The edit ──────────────────────────────────────────────────────────────

  /// <summary>
  /// Reads the medium, lets <paramref name="change"/> edit the block list of the Data Set the
  /// path names (with the path inside that set), and writes the medium anew when it changed.
  /// </summary>
  private static void Edit(Stream archive, string path, Func<List<MtfBlock>, string, int, DateTime, bool> change) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanSeek || !archive.CanRead || !archive.CanWrite)
      throw new ArgumentException("BKF editing needs a readable, writable, seekable stream.", nameof(archive));
    var media = MtfMedia.Read(archive, keepData: true);
    if (media.Sets.Count == 0)
      throw new InvalidDataException("BKF: the medium holds no Data Set.");
    if (media.MediaBasedCatalogType is not (0 or 1))
      throw new NotSupportedException($"BKF: the medium carries a Type {media.MediaBasedCatalogType} Media Based Catalog, which is not written here; it is not edited.");
    if (media.HasEndOfTapeMarker || media.Sets.Any(s => s.Eset is null || s.ExtraEsets > 0
          || (s.Sset.Attributes & Mtf.BlockContinuationBit) != 0 || s.Blocks.Any(b => (b.Attributes & Mtf.BlockContinuationBit) != 0)))
      throw new NotSupportedException("BKF: the medium spans to another or a Data Set is incomplete; it is not edited.");

    var index = 0;
    var inSet = path;
    if (media.Sets.Count > 1) {
      var match = System.Text.RegularExpressions.Regex.Match(path, @"^set-(\d+)(?:/|$)");
      if (!match.Success || !int.TryParse(match.Groups[1].Value, out var number) || number < 1 || number > media.Sets.Count)
        throw new NotSupportedException($"BKF: the medium holds {media.Sets.Count} Data Sets; name one as the listing does (set-NN/…).");
      index = number - 1;
      inSet = path[match.Length..];
    }
    var set = media.Sets[index];
    if (set.Blocks.Count(b => b.Type == "VOLB") > 1)
      throw new NotSupportedException("BKF: the Data Set holds more than one volume; it is not edited.");

    var blocks = set.Blocks.ToList();
    if (!change(blocks, inSet, media.FormatLogicalBlockSize, DateTime.Now)) return;

    var catalog = media.MediaBasedCatalogType == 1;
    var sets = media.Sets.Select((s, i) => new MtfComposer.DataSet(s.Sset, i == index ? blocks : s.Blocks, Trim(s.Eset!), s.CatalogEset is { } c ? Trim(c) : null)).ToList();
    // Keep the Media Family ID the existing Set Map records (NTBackup writes zero there).
    uint? familyId = media.Sets[0].Eset!.Stream("TSMP") is { Data: { Length: >= 4 } map } ? BinaryPrimitives.ReadUInt32LittleEndian(map) : null;
    using var output = new MemoryStream();
    MtfComposer.Write(output, media.Tape, sets, media.FormatLogicalBlockSize, s => s.Data ?? [], catalog, familyId);
    archive.Position = 0;
    output.Position = 0;
    output.CopyTo(archive);
    archive.SetLength(output.Length);
    archive.Flush();
  }

  /// <summary>An ESET's own 85 bytes (§5.2.8), without the gap before its catalog streams.</summary>
  private static MtfBlock Trim(MtfBlock eset) => new() { Dblk = eset.Dblk[..Math.Min(eset.Dblk.Length, 85)], Offset = eset.Offset };

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
