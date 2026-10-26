using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace FileSystem.Vdfs;

/// <summary>
/// Edits a VDF volume in place: surviving payloads keep their bytes and, wherever the
/// catalog leaves them room, their offsets.
/// </summary>
/// <remarks>
/// <para>Every edit rewrites the catalog at offset 296, where every reference tool puts it,
/// laid out as <see cref="VdfsWriter" /> lays it out — a directory's entries contiguous and
/// its last entry flagged, which a tombstoned record in the middle of a block would break.</para>
///
/// <para>When the catalog grows into the first payloads, those payloads move to the end of
/// the volume and their entries follow them; nothing else moves. A new payload goes to the
/// end. A removed payload is zeroed. A replacement that fits its old extent is written over
/// it. In a zipped volume new payloads are ZippedStreams, except <c>.wav</c>/<c>.ogg</c>.</para>
///
/// <para>A volume whose catalog cannot be followed in full is refused rather than edited:
/// rewriting it would drop whatever the damaged part still names.</para>
/// </remarks>
public static class VdfsInPlaceModifier {

  /// <summary>
  /// Adds a file, replacing any file of the same path. Existing payloads keep their bytes.
  /// </summary>
  /// <exception cref="InvalidDataException">The volume is not a VDF or its catalog is damaged.</exception>
  public static void AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    RequireEditable(archive);

    var volume = Volume.Load(archive);
    volume.RemoveAndWipe(archive, name, wipeData: true);
    volume.Add(name.ToUpperInvariant(), data);
    volume.Commit(archive);
  }

  /// <summary>
  /// Removes a file — or a directory and everything under it — and zeroes the payloads.
  /// Returns whether anything was removed.
  /// </summary>
  public static bool RemoveFile(Stream archive, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    RequireEditable(archive);

    Volume volume;
    try {
      volume = Volume.Load(archive);
    } catch (InvalidDataException) {
      return false;
    }

    if (!volume.RemoveAndWipe(archive, name, wipeData)) return false;
    volume.Commit(archive);
    return true;
  }

  /// <summary>
  /// Replaces a file's contents, over its old extent when the new payload fits there and
  /// at the end of the volume otherwise. A missing file is added.
  /// </summary>
  public static bool ReplaceFile(Stream archive, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);
    RequireEditable(archive);

    var volume = Volume.Load(archive);
    var node = volume.Catalog.Find(name);
    if (node is not { IsDirectory: false }) {
      AddFile(archive, name, newData);
      return true;
    }

    var encoded = volume.Encode(node.Name, newData);
    var stored = (long)node.Tag!;
    if (encoded.Length > stored || node.Size == 0) {
      AddFile(archive, name, newData);
      return true;
    }

    archive.Position = node.Offset;
    archive.Write(encoded);
    ExtentCopy.Zero(archive, node.Offset + encoded.Length, stored - encoded.Length);
    node.Size = newData.Length;
    node.Tag = (long)encoded.Length;
    volume.Commit(archive);
    return true;
  }

  private static void RequireEditable(Stream archive) {
    if (!archive.CanSeek || !archive.CanRead || !archive.CanWrite)
      throw new ArgumentException("Archive stream must be readable, writable, and seekable.", nameof(archive));
  }

  /// <summary>A volume's header facts and catalog, with each file's stored length in its tag.</summary>
  private sealed class Volume {
    public VdfsCatalog Catalog { get; } = new();
    private readonly Dictionary<VdfsCatalog.Node, byte[]> _pending = [];
    private bool _zipped;
    private long _oldCatalogOffset;
    private long _oldCatalogLength;

    public static Volume Load(Stream archive) {
      using var reader = new VdfsReader(archive, leaveOpen: true);
      if (reader.IsPartial)
        throw new InvalidDataException("VDFS: the catalog cannot be followed in full; refusing to rewrite it.");

      var volume = new Volume {
        _zipped = reader.IsZippedVolume,
        _oldCatalogOffset = reader.EntryTableOffset,
        _oldCatalogLength = reader.EntryTableLength,
      };
      foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
        var node = volume.Catalog.GetOrAddFile(entry.Name);
        node.Offset = entry.DataOffset;
        node.Size = entry.Size;
        node.Attributes = entry.Attributes;
        node.Tag = entry.StoredSize;
      }
      return volume;
    }

    /// <summary>The payload as this volume stores it.</summary>
    public byte[] Encode(string name, byte[] data)
      => this._zipped && data.Length > 0 && !VdfsLayout.IsStoredRawWhenZipped(name)
        ? VdfsZippedStream.Encode(data)
        : data;

    public bool RemoveAndWipe(Stream archive, string name, bool wipeData) {
      var removed = this.Catalog.Remove(name);
      if (wipeData)
        foreach (var file in removed)
          if (!this._pending.Remove(file))
            ExtentCopy.Zero(archive, file.Offset, Math.Min((long)file.Tag!, Math.Max(0, archive.Length - file.Offset)));
      return removed.Count > 0;
    }

    public void Add(string name, byte[] data) {
      if ((ulong)data.LongLength > uint.MaxValue)
        throw new ArgumentException($"VDFS: '{name}' is larger than the 32-bit size field.");
      var node = this.Catalog.GetOrAddFile(name);
      var encoded = this.Encode(node.Name, data);
      node.Size = data.Length;
      node.Offset = -1;
      node.Tag = (long)encoded.Length;
      this._pending[node] = encoded;
    }

    /// <summary>
    /// Writes the catalog at 296, moving out of its way any payload it now covers, places
    /// pending payloads at the end, and patches the header.
    /// </summary>
    public void Commit(Stream archive) {
      var files = this.Catalog.Files().Select(f => f.File).ToList();
      var catalogEnd = VdfsLayout.HeaderSize + (long)this.Catalog.EntryCount * VdfsLayout.EntrySize;
      var placed = files.Where(f => f.Offset >= 0 && (long)f.Tag! > 0).ToList();
      var tail = Math.Max(catalogEnd, placed.Count == 0 ? 0 : placed.Max(f => f.Offset + (long)f.Tag!));
      tail = Math.Max(tail, this._oldCatalogOffset + this._oldCatalogLength);

      foreach (var file in placed.Where(f => f.Offset < catalogEnd).OrderBy(f => f.Offset)) {
        var stored = (long)file.Tag!;
        ExtentCopy.Move(archive, file.Offset, tail, stored);
        // What the catalog does not overwrite would stay behind as a stale copy.
        var leftover = file.Offset + stored - catalogEnd;
        if (leftover > 0) ExtentCopy.Zero(archive, catalogEnd, leftover);
        file.Offset = tail;
        tail += stored;
      }

      foreach (var file in files) {
        if (this._pending.TryGetValue(file, out var bytes)) {
          if (archive.Length < tail + bytes.Length) archive.SetLength(tail + bytes.Length);
          archive.Position = tail;
          archive.Write(bytes);
          file.Offset = tail;
          tail += bytes.Length;
        }
        if (file.Offset > uint.MaxValue)
          throw new InvalidOperationException("VDFS: payload offsets are 32-bit; the volume has grown past 4 GiB.");
      }
      this._pending.Clear();

      // Empty files point at the end of the volume, as VdfsWriter places them.
      var end = Math.Max(Math.Max(tail, catalogEnd), archive.Length);
      foreach (var file in files.Where(f => (long)f.Tag! == 0))
        file.Offset = Math.Min(end, uint.MaxValue);

      // The old catalog's bytes past the new one are dead now. Nothing live sits in them:
      // they held the catalog, and any payload under the new catalog has just moved away.
      var deadStart = Math.Max(this._oldCatalogOffset, catalogEnd);
      var deadEnd = this._oldCatalogOffset + this._oldCatalogLength;
      if (deadEnd > deadStart && !files.Any(f => f.Offset < deadEnd && f.Offset + (long)f.Tag! > deadStart))
        ExtentCopy.Zero(archive, deadStart, deadEnd - deadStart);

      var catalog = this.Catalog.Serialize();
      if (archive.Length < catalogEnd) archive.SetLength(catalogEnd);
      archive.Position = VdfsLayout.HeaderSize;
      archive.Write(catalog);

      var header = new byte[VdfsLayout.HeaderSize];
      archive.Position = 0;
      archive.ReadExactly(header);
      var dosTime = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(VdfsLayout.TimestampOffset));
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(VdfsLayout.VolumeFlagsOffset));
      VdfsWriter.WriteHeaderFields(header, this.Catalog, dosTime, files.Sum(f => f.Size), flags);
      archive.Position = 0;
      archive.Write(header);
      archive.Flush();

      this._oldCatalogOffset = VdfsLayout.HeaderSize;
      this._oldCatalogLength = catalog.Length;
    }
  }
}
