using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Registry;

namespace Compression.Core.DiskImage;

/// <summary>
/// Read/write partition-table editor for raw disk images. Handles MBR
/// (4 primary entries plus an extended/logical chain) and GPT (up to 128
/// entries with primary + backup headers, configurable entry size). Operates
/// directly on the supplied <see cref="Stream"/> — the caller owns the stream
/// and is responsible for flushing/closing it.
/// </summary>
/// <remarks>
/// <para>
/// Construction auto-detects MBR vs GPT vs none. If neither is found the
/// editor enters MBR mode with no entries; the first call to
/// <see cref="AddPartition"/> writes a fresh MBR.
/// </para>
/// <para>
/// All write operations validate that new ranges do not overlap existing
/// partitions and stay within the bounds of the underlying stream. GPT
/// header CRC and entry-array CRC are recomputed on every write; the backup
/// GPT at the end of the disk is kept in sync.
/// </para>
/// <para>
/// MBR extended (0x05/0x0F/0x85) container and the logical-partition EBR
/// chain are fully editable through <see cref="AddLogicalPartition"/>,
/// <see cref="DeletePartition"/>, and <see cref="PurgePartition"/>. Listing
/// returns primary entries followed by logicals. GPT secondary header lives
/// at <c>stream.Length - sectorSize</c>; growing the underlying stream is the
/// caller's responsibility (virtual-disk container streams auto-grow as
/// needed).
/// </para>
/// </remarks>
public sealed class PartitionEditor {

  /// <summary>Standard sector size used by the editor.</summary>
  public const int SectorSize = 512;

  private const int MbrSignatureOffset = 510;
  private const ushort MbrBootSignature = 0xAA55;
  private const int MbrTableOffset = 0x1BE;
  private const int MbrEntrySize = 16;

  private const int GptHeaderLba = 1;
  private const int GptEntriesLba = 2;
  private const int GptHeaderSize = 92;
  private const int DefaultGptEntrySize = 128;
  private const int GptEntryCount = 128;
  private const int GptReservedSectors = 33; // 32 sectors of entries + 1 sector of header
  private static ReadOnlySpan<byte> GptSignature => "EFI PART"u8;

  private readonly Stream _stream;
  private PartitionScheme _scheme;
  private readonly List<PartitionEntry> _entries = [];

  // GPT layout parameters (read from on-disk primary header on Reload;
  // defaults applied when initialising a fresh GPT).
  private uint _gptEntrySize = DefaultGptEntrySize;
  private uint _gptEntryCount = GptEntryCount;

  /// <summary>Partition scheme currently in use on the disk image.</summary>
  public PartitionScheme Scheme => this._scheme;

  /// <summary>
  /// Opens an editor over the given disk-image stream. The stream must be
  /// readable, writable, and seekable. The scheme (MBR/GPT/None) is
  /// auto-detected from the on-disk signature.
  /// </summary>
  public PartitionEditor(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
    if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    this._stream = stream;
    this.Reload();
  }

  /// <summary>
  /// Convenience factory for an editor backed by an <see cref="IPartitionEditable"/>
  /// container (VHD/VHDX/VMDK/QCOW2/VDI). The caller is responsible for
  /// disposing the returned guest-disk view (the editor exposes no explicit
  /// close method — operations flush after every write).
  /// </summary>
  public static PartitionEditor OpenFromContainer(IPartitionEditable container, Stream containerImage) {
    ArgumentNullException.ThrowIfNull(container);
    var guest = container.OpenGuestDiskStream(containerImage);
    return new PartitionEditor(guest);
  }

  /// <summary>Returns a snapshot of the current partition table.</summary>
  /// <remarks>
  /// For MBR, the returned list contains both primary entries (Source == "MBR"
  /// or "MBR (Extended Container)") and logical entries from the EBR chain
  /// (Source == "EBR"). Primary entries appear first in disk-table order; logicals
  /// follow in chain order.
  /// </remarks>
  public IReadOnlyList<PartitionEntry> ListPartitions()
    => this._entries.AsReadOnly();

  /// <summary>
  /// Adds a new primary partition entry covering [<paramref name="startByteOffset"/>,
  /// <paramref name="startByteOffset"/> + <paramref name="lengthBytes"/>).
  /// </summary>
  /// <param name="startByteOffset">Start offset on disk, in bytes. Must be a
  /// multiple of <see cref="SectorSize"/>.</param>
  /// <param name="lengthBytes">Partition length in bytes. Must be a multiple
  /// of <see cref="SectorSize"/>.</param>
  /// <param name="type">Logical filesystem type — mapped to MBR byte or GPT
  /// GUID depending on scheme.</param>
  /// <param name="label">Partition name (GPT only; ignored for MBR).</param>
  /// <exception cref="InvalidOperationException">Range overlaps an existing
  /// partition or exceeds the disk length.</exception>
  public void AddPartition(long startByteOffset, long lengthBytes, PartitionType type, string? label) {
    ValidateRange(startByteOffset, lengthBytes);
    this.RequireFits(startByteOffset, lengthBytes);
    this.RequireNoOverlap(startByteOffset, lengthBytes);

    if (this._scheme == PartitionScheme.None) {
      // No table yet — promote to MBR.
      this._scheme = PartitionScheme.Mbr;
      this.InitializeEmptyMbr();
    }

    if (this._scheme == PartitionScheme.Mbr) {
      var primaryCount = this.CountPrimary();
      if (primaryCount >= 4)
        throw new InvalidOperationException("MBR supports at most 4 primary partitions; convert to GPT for more.");

      var mbrByte = PartitionTypeMapping.ToMbrByte(type);
      var isExtended = mbrByte is 0x05 or 0x0F or 0x85;
      if (isExtended && this.HasExtendedContainer())
        throw new InvalidOperationException("MBR allows only one extended/container partition per disk.");

      var entry = new PartitionEntry {
        Index = 0, // reassigned by ReindexAll
        StartOffset = startByteOffset,
        Size = lengthBytes,
        TypeName = PartitionTypeDatabase.GetMbrTypeName(mbrByte),
        TypeCode = $"0x{mbrByte:X2}",
        Source = isExtended ? "MBR (Extended Container)" : "MBR",
        Name = label ?? string.Empty
      };

      // Insert primary entries before any logical (EBR) entries.
      var insertAt = this.FirstLogicalIndex();
      this._entries.Insert(insertAt, entry);
      this.ReindexAll();
      this.RewriteMbr();
    } else {
      if (this._entries.Count >= this._gptEntryCount)
        throw new InvalidOperationException($"GPT supports at most {this._gptEntryCount} partitions.");
      var guid = PartitionTypeMapping.ToGptGuid(type);
      this._entries.Add(new PartitionEntry {
        Index = this._entries.Count,
        StartOffset = startByteOffset,
        Size = lengthBytes,
        TypeName = PartitionTypeDatabase.GetGptTypeName(guid),
        TypeCode = guid.ToString("D").ToUpperInvariant(),
        Source = "GPT",
        Name = label ?? string.Empty
      });
      this.RewriteGpt();
    }
  }

  /// <summary>
  /// Adds a new logical partition inside the existing MBR extended container.
  /// The byte range must lie entirely within the extended container and not
  /// overlap any existing logical (each logical occupies its EBR sector plus
  /// the data range).
  /// </summary>
  /// <param name="startByteOffset">Start offset of the logical partition's
  /// data on disk, in bytes. Must be sector-aligned and leave at least one
  /// sector before it for the EBR.</param>
  /// <param name="lengthBytes">Logical partition length in bytes. Must be a
  /// multiple of <see cref="SectorSize"/>.</param>
  /// <param name="type">Logical filesystem type. Extended types (0x05/0x0F/0x85)
  /// are rejected — logicals cannot themselves be extended containers.</param>
  /// <param name="label">Optional label (recorded in memory only — MBR has no
  /// label field).</param>
  /// <exception cref="InvalidOperationException">No extended container exists,
  /// range outside container, or overlap detected.</exception>
  public void AddLogicalPartition(long startByteOffset, long lengthBytes, PartitionType type, string? label) {
    ValidateRange(startByteOffset, lengthBytes);
    if (this._scheme != PartitionScheme.Mbr)
      throw new InvalidOperationException("Logical partitions only exist in MBR layouts.");

    var container = this.FindExtendedContainer()
      ?? throw new InvalidOperationException(
        "No extended container partition exists. Add one with AddPartition(..., PartitionType.ExtendedLba, …) first.");

    var mbrByte = PartitionTypeMapping.ToMbrByte(type);
    if (mbrByte is 0x05 or 0x0F or 0x85)
      throw new InvalidOperationException("Logical partitions cannot be extended-type (0x05/0x0F/0x85).");

    // Bounds: logical data + its EBR must fit within the container.
    // The first logical sits one sector after the container start (its EBR
    // occupies LBA = container_start); subsequent logicals need a dedicated
    // EBR sector immediately preceding their data.
    var existingLogicals = this._entries.Count(e => e.Source == "EBR");
    if (existingLogicals == 0) {
      // First logical: container_start + 1 sector is the earliest legal data offset.
      if (startByteOffset < container.StartOffset + SectorSize)
        throw new InvalidOperationException(
          $"First logical must start at or after container_start + 1 sector ({container.StartOffset + SectorSize}); got {startByteOffset}.");
    } else {
      // Subsequent logical needs an EBR sector before it.
      var ebrLba = (startByteOffset / SectorSize) - 1;
      var ebrStart = ebrLba * SectorSize;
      if (ebrStart <= container.StartOffset)
        throw new InvalidOperationException(
          $"Logical start ({startByteOffset}) leaves no room for an EBR sector inside the extended container [{container.StartOffset}..{container.StartOffset + container.Size}).");
    }

    if (startByteOffset + lengthBytes > container.StartOffset + container.Size)
      throw new InvalidOperationException(
        $"Logical end ({startByteOffset + lengthBytes}) exceeds extended container end ({container.StartOffset + container.Size}).");

    // Overlap check: EBR + data for non-first; just data for first (EBR is at container start).
    var checkStart = existingLogicals == 0 ? startByteOffset : startByteOffset - SectorSize;
    var checkLength = existingLogicals == 0 ? lengthBytes : SectorSize + lengthBytes;
    this.RequireNoOverlap(checkStart, checkLength);

    var entry = new PartitionEntry {
      Index = 0,
      StartOffset = startByteOffset,
      Size = lengthBytes,
      TypeName = PartitionTypeDatabase.GetMbrTypeName(mbrByte),
      TypeCode = $"0x{mbrByte:X2}",
      Source = "EBR",
      Name = label ?? string.Empty
    };
    this._entries.Add(entry);
    this.ReindexAll();
    this.RewriteMbr();
    this.RewriteEbrChain();
  }

  /// <summary>
  /// Deletes the partition at the given index. The partition byte range is
  /// not modified — see <see cref="PurgePartition"/> to zero it.
  /// </summary>
  /// <remarks>
  /// For MBR logical entries the EBR chain is rebuilt automatically. Deleting
  /// the extended container (with logical entries inside) is allowed but also
  /// removes all contained logicals.
  /// </remarks>
  public void DeletePartition(int index) {
    this.RequireValidIndex(index);

    var removed = this._entries[index];
    this._entries.RemoveAt(index);

    // If we removed the extended container, drop all logicals too.
    if (removed.Source == "MBR (Extended Container)")
      this._entries.RemoveAll(e => e.Source == "EBR");

    this.ReindexAll();
    if (this._scheme == PartitionScheme.Mbr) {
      this.RewriteMbr();
      this.RewriteEbrChain();
    } else if (this._scheme == PartitionScheme.Gpt) {
      this.RewriteGpt();
    }
  }

  /// <summary>
  /// Deletes the partition <em>and</em> zero-fills its byte range on disk.
  /// </summary>
  public void PurgePartition(int index) {
    this.RequireValidIndex(index);
    var e = this._entries[index];
    this.ZeroRange(e.StartOffset, e.Size);

    // For logicals also zero the preceding EBR sector.
    if (e.Source == "EBR") {
      var ebrStart = e.StartOffset - SectorSize;
      if (ebrStart >= 0)
        this.ZeroRange(ebrStart, SectorSize);
    }

    this.DeletePartition(index);
  }

  /// <summary>
  /// Reads the current MBR partition table, drops it, then rewrites the
  /// equivalent GPT layout: protective MBR at LBA 0, primary GPT header at
  /// LBA 1, entries starting at LBA 2, backup GPT at the end of the disk.
  /// Translates MBR type bytes to GPT GUIDs via
  /// <see cref="PartitionTypeMapping"/>. The extended container (if present)
  /// is dropped — its logical children are promoted to top-level GPT entries.
  /// </summary>
  public void ConvertMbrToGpt() {
    if (this._scheme == PartitionScheme.Gpt)
      return;
    if (this._scheme != PartitionScheme.Mbr)
      throw new InvalidOperationException("No MBR partition table to convert.");

    // Translate entries: keep ranges, swap type metadata, drop extended containers.
    var translated = new List<PartitionEntry>(this._entries.Count);
    foreach (var e in this._entries) {
      if (e.Source == "MBR (Extended Container)")
        continue; // GPT has no extended-partition concept.
      var mbrByte = ParseMbrTypeCode(e.TypeCode);
      var logical = PartitionTypeMapping.FromMbrByte(mbrByte);
      var guid = PartitionTypeMapping.ToGptGuid(logical);
      translated.Add(new PartitionEntry {
        Index = translated.Count,
        StartOffset = e.StartOffset, Size = e.Size,
        TypeName = PartitionTypeDatabase.GetGptTypeName(guid),
        TypeCode = guid.ToString("D").ToUpperInvariant(),
        Source = "GPT",
        Name = e.Name,
        IsActive = e.IsActive
      });
    }

    this._entries.Clear();
    this._entries.AddRange(translated);
    this._scheme = PartitionScheme.Gpt;
    this._gptEntrySize = DefaultGptEntrySize;
    this._gptEntryCount = GptEntryCount;
    this.RewriteGpt(); // Also rewrites the protective MBR.
  }

  /// <summary>
  /// Reads the current GPT partition table, takes the first 4 entries (MBR
  /// hard limit), zero-fills both GPT header areas, and writes a fresh MBR
  /// with the translated entries.
  /// </summary>
  public void ConvertGptToMbr() {
    if (this._scheme == PartitionScheme.Mbr)
      return;
    if (this._scheme != PartitionScheme.Gpt)
      throw new InvalidOperationException("No GPT partition table to convert.");

    if (this._entries.Count > 4)
      throw new InvalidOperationException(
        $"GPT has {this._entries.Count} partitions; MBR supports only 4 primaries.");

    // Translate entries.
    var translated = new List<PartitionEntry>(this._entries.Count);
    for (var i = 0; i < this._entries.Count; ++i) {
      var e = this._entries[i];
      var guid = Guid.TryParse(e.TypeCode, out var g) ? g : Guid.Empty;
      var logical = PartitionTypeMapping.FromGptGuid(guid);
      var mbrByte = PartitionTypeMapping.ToMbrByte(logical);
      translated.Add(new PartitionEntry {
        Index = i, StartOffset = e.StartOffset, Size = e.Size,
        TypeName = PartitionTypeDatabase.GetMbrTypeName(mbrByte),
        TypeCode = $"0x{mbrByte:X2}",
        Source = "MBR",
        Name = string.Empty,
        IsActive = e.IsActive
      });
    }

    // Zero out primary GPT (header + entry array, LBAs 1..33) and backup
    // GPT (last 33 sectors) before writing the MBR.
    this.ZeroRange(GptHeaderLba * SectorSize, (long)GptReservedSectors * SectorSize);
    var backupStart = this._stream.Length - (long)GptReservedSectors * SectorSize;
    if (backupStart > 0)
      this.ZeroRange(backupStart, (long)GptReservedSectors * SectorSize);

    this._entries.Clear();
    this._entries.AddRange(translated);
    this._scheme = PartitionScheme.Mbr;
    this.RewriteMbr();
  }

  /// <summary>
  /// Pluggable in-place filesystem-variant converter. Set by the higher-level
  /// <c>Compression.Lib</c> layer at startup (it can reference the per-family
  /// FileSystem.* projects, which Core cannot). When non-null,
  /// <see cref="ConvertFilesystem"/> attempts a metadata-only conversion via
  /// this delegate before falling back to <c>false</c>.
  ///
  /// <para>Contract: takes (partitionStream, sourceFsId, targetFsId), returns
  /// <c>true</c> if the conversion succeeded entirely via metadata edits,
  /// <c>false</c> if no in-place path exists for the pair (the caller should
  /// then do a full migration: extract files → FormatPartition → re-import).</para>
  /// </summary>
  public static Func<Stream, string, string, bool>? InPlaceFilesystemConverter { get; set; }

  /// <summary>
  /// Attempts an in-place filesystem-variant conversion of the partition at
  /// <paramref name="partitionIndex"/>. Reads the partition's current
  /// contents as a substream, dispatches to <see cref="InPlaceFilesystemConverter"/>
  /// (set by the Compression.Lib bootstrap), and returns the converter's
  /// answer.
  ///
  /// <para>Returns <c>false</c> when:</para>
  /// <list type="bullet">
  ///   <item>No converter has been registered (Core-only build).</item>
  ///   <item>The source/target pair has no metadata-only conversion path
  ///   (e.g. FAT → ext, NTFS → exFAT — any cross-family conversion).</item>
  ///   <item>The image geometry rules out the requested target (e.g. a tiny
  ///   FAT12 floppy can't fit FAT32's 32-reserved-sector overhead).</item>
  /// </list>
  /// In any of these cases the caller is expected to fall back to a full
  /// extract → reformat → re-import migration.
  /// </summary>
  /// <param name="partitionIndex">Zero-based index into <see cref="ListPartitions"/>.</param>
  /// <param name="newFsId">Target filesystem id (e.g. "Fat32", "Ext4").</param>
  /// <returns><c>true</c> when the conversion completed in place;
  /// <c>false</c> when the caller should do a full migration instead.</returns>
  public bool ConvertFilesystem(int partitionIndex, string newFsId) {
    this.RequireValidIndex(partitionIndex);
    ArgumentException.ThrowIfNullOrEmpty(newFsId);

    var converter = InPlaceFilesystemConverter;
    if (converter is null) return false;

    var e = this._entries[partitionIndex];
    // Detect the current filesystem id so the converter can pick the right
    // family + variant. We read the partition's leading 4 KiB through a
    // SubStream and let the registry's detector inspect the magic bytes.
    var sourceFsId = DetectFsId(this._stream, e.StartOffset, e.Size);
    if (string.IsNullOrEmpty(sourceFsId)) return false;
    if (string.Equals(sourceFsId, newFsId, StringComparison.OrdinalIgnoreCase)) return true;

    using var partitionView = new PartitionSubStream(this._stream, e.StartOffset, e.Size);
    return converter(partitionView, sourceFsId, newFsId);
  }

  /// <summary>
  /// Returns the registered filesystem id for the partition spanning
  /// [<paramref name="startOffset"/>, <paramref name="startOffset"/> +
  /// <paramref name="size"/>) on <paramref name="disk"/>, or null if the
  /// contents don't match a known descriptor. Uses the FormatRegistry's
  /// magic-byte scanner; only inspects the first ~4 KiB of the partition.
  /// </summary>
  private static string? DetectFsId(Stream disk, long startOffset, long size) {
    var probeLen = (int)Math.Min(size, 4096);
    if (probeLen <= 0) return null;
    var probe = new byte[probeLen];
    var saved = disk.Position;
    try {
      disk.Position = startOffset;
      disk.ReadExactly(probe, 0, probeLen);
    } finally {
      disk.Position = saved;
    }

    // Iterate registered descriptors; first match wins. We intentionally
    // don't pull in any specific FileSystem.* type — registry lookup keeps
    // Core decoupled.
    foreach (var desc in FormatRegistry.All) {
      var sigs = desc.MagicSignatures;
      if (sigs.Count == 0) continue;
      foreach (var sig in sigs) {
        if (MatchesAtOffset(probe, sig)) return desc.Id;
      }
    }
    return null;
  }

  private static bool MatchesAtOffset(byte[] probe, MagicSignature sig) {
    var bytes = sig.Bytes;
    if (sig.Offset < 0 || sig.Offset + bytes.Length > probe.Length) return false;
    for (var i = 0; i < bytes.Length; i++)
      if (probe[sig.Offset + i] != bytes[i]) return false;
    return true;
  }

  /// <summary>
  /// Pluggable in-place filesystem-resizer. Set by the higher-level
  /// <c>Compression.Lib</c> layer at startup (Core cannot reference
  /// FileSystem.* projects, so the actual resizer lives there). The
  /// contract is:
  /// <para>(partitionStream, fsId, newSizeBytes, isShrink) → bool</para>
  /// Returns <c>true</c> when the resize succeeded entirely via in-place
  /// edits, <c>false</c> when no in-place path exists for the fsId (the
  /// caller should fall back to a full extract → reformat → re-import).
  /// </summary>
  public static Func<Stream, string, long, bool, bool>? InPlaceFilesystemResizer { get; set; }

  /// <summary>
  /// Resizes the partition at <paramref name="index"/> to
  /// <paramref name="newSizeBytes"/>. Shrinks or grows the inner filesystem
  /// in place (via <see cref="InPlaceFilesystemResizer"/>) and updates the
  /// partition table entry to reflect the new size.
  ///
  /// <para>For <b>shrink</b>: the FS is shrunk first (so its trailing data
  /// has been migrated down before we touch the partition table), then the
  /// partition entry is updated. A crash between the FS shrink and the
  /// table update leaves the partition at its old (larger) size with a
  /// smaller FS inside — readable, no data loss.</para>
  ///
  /// <para>For <b>grow</b>: the partition entry is updated first (so the
  /// substream view exposes the new range), then the FS is grown. A crash
  /// between the two leaves the partition at its new (larger) size with
  /// the old FS inside, which is also readable (the new tail bytes are
  /// just unused).</para>
  ///
  /// <para>The new size must be sector-aligned and must not cause overlap
  /// with the next partition (for grow) or fall below a minimum FS-defined
  /// floor (for shrink). All validation happens before any write.</para>
  /// </summary>
  /// <param name="index">Zero-based partition index from <see cref="ListPartitions"/>.</param>
  /// <param name="newSizeBytes">New partition size in bytes (sector-aligned).</param>
  /// <exception cref="InvalidOperationException">No in-place resizer registered,
  /// new size invalid, would overlap, or FS does not support resize.</exception>
  public void ResizePartition(int index, long newSizeBytes) {
    this.RequireValidIndex(index);
    if (newSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(newSizeBytes));
    if (newSizeBytes % SectorSize != 0)
      throw new ArgumentException($"New size must be sector-aligned ({SectorSize} bytes).", nameof(newSizeBytes));

    var resizer = InPlaceFilesystemResizer
      ?? throw new InvalidOperationException(
        "No in-place filesystem resizer registered. Compression.Lib must be loaded for partition resize.");

    var e = this._entries[index];
    if (newSizeBytes == e.Size) return; // No-op.

    var isShrink = newSizeBytes < e.Size;

    // Validate up-front: new size must stay within disk bounds and not
    // overlap any neighbouring partition. For grow we check overlap; for
    // shrink we trivially do not introduce overlap.
    var newEnd = e.StartOffset + newSizeBytes;
    if (newEnd > this._stream.Length)
      throw new InvalidOperationException(
        $"New partition end ({newEnd}) exceeds disk length ({this._stream.Length}).");
    if (!isShrink) {
      foreach (var other in this._entries) {
        if (other.Index == e.Index) continue;
        // Skip extended-container parents that legitimately span over
        // their logical children.
        if (other.Source == "MBR (Extended Container)" &&
            e.StartOffset >= other.StartOffset && newEnd <= other.StartOffset + other.Size) continue;
        var oStart = other.StartOffset;
        var oEnd = other.StartOffset + other.Size;
        if (e.StartOffset < oEnd && newEnd > oStart)
          throw new InvalidOperationException(
            $"Grown partition [{e.StartOffset}..{newEnd}) would overlap partition #{other.Index} [{oStart}..{oEnd}).");
      }
    }

    // Detect FS id at the partition head (reuses the same registry-driven
    // scanner that ConvertFilesystem uses).
    var fsId = DetectFsId(this._stream, e.StartOffset, e.Size)
      ?? throw new InvalidOperationException(
        $"Cannot determine filesystem at partition #{index} — no magic match in registry.");

    if (isShrink) {
      // Order: FS first, then table.
      using (var partitionView = new PartitionSubStream(this._stream, e.StartOffset, e.Size)) {
        if (!resizer(partitionView, fsId, newSizeBytes, true))
          throw new InvalidOperationException(
            $"Filesystem '{fsId}' does not support in-place shrink.");
      }
      this._stream.Flush();
      this.UpdateEntrySize(index, newSizeBytes);
    } else {
      // Order: table first (so the substream view exposes the new range),
      // then FS grow.
      this.UpdateEntrySize(index, newSizeBytes);
      using (var partitionView = new PartitionSubStream(this._stream, e.StartOffset, newSizeBytes)) {
        if (!resizer(partitionView, fsId, newSizeBytes, false))
          throw new InvalidOperationException(
            $"Filesystem '{fsId}' does not support in-place grow.");
      }
      this._stream.Flush();
    }
  }

  /// <summary>
  /// Replaces the Size field of the partition entry at <paramref name="index"/>
  /// and rewrites the MBR/GPT table sector. Used internally by
  /// <see cref="ResizePartition"/>.
  /// </summary>
  private void UpdateEntrySize(int index, long newSizeBytes) {
    var old = this._entries[index];
    this._entries[index] = new PartitionEntry {
      Index = old.Index,
      StartOffset = old.StartOffset,
      Size = newSizeBytes,
      TypeName = old.TypeName,
      IsActive = old.IsActive,
      TypeCode = old.TypeCode,
      Name = old.Name,
      Source = old.Source,
    };
    if (this._scheme == PartitionScheme.Mbr) {
      this.RewriteMbr();
      // Logical-partition size lives in its EBR's first entry, so any logical
      // resize requires rewriting the EBR chain too.
      if (old.Source == "EBR") this.RewriteEbrChain();
    } else if (this._scheme == PartitionScheme.Gpt) {
      this.RewriteGpt();
    }
  }

  /// <summary>
  /// Writes a fresh filesystem image of the given <paramref name="formatId"/>
  /// into the partition's byte range. The format must be registered with
  /// <see cref="FormatRegistry"/> and support <see cref="IArchiveCreatable"/>.
  /// The resulting bytes must fit within the partition (an exception is
  /// thrown otherwise).
  /// </summary>
  public void FormatPartition(int index, string formatId, FormatCreateOptions options) {
    this.RequireValidIndex(index);
    ArgumentException.ThrowIfNullOrEmpty(formatId);
    ArgumentNullException.ThrowIfNull(options);

    var ops = FormatRegistry.GetArchiveOps(formatId)
      ?? throw new InvalidOperationException($"Format '{formatId}' is not registered.");
    if (ops is not IArchiveCreatable creatable)
      throw new InvalidOperationException($"Format '{formatId}' does not support creation.");

    var e = this._entries[index];
    using var fsImage = new MemoryStream();
    creatable.Create(fsImage, [], options);
    var bytes = fsImage.ToArray();
    if (bytes.Length > e.Size)
      throw new InvalidOperationException(
        $"Generated {formatId} image ({bytes.Length} bytes) exceeds partition size ({e.Size} bytes).");

    // First wipe the whole partition so leftover bytes don't confuse FS readers.
    this.ZeroRange(e.StartOffset, e.Size);

    this._stream.Position = e.StartOffset;
    this._stream.Write(bytes, 0, bytes.Length);
    this._stream.Flush();
  }

  /// <summary>
  /// Verifies on-disk integrity of the current partition table. Reports
  /// header signature/CRC mismatches, GPT entry-array CRC mismatches,
  /// primary/backup divergence, and out-of-range partition extents.
  /// </summary>
  public PartitionTableVerification Verify() {
    var issues = new List<string>();

    if (this._scheme == PartitionScheme.None) {
      issues.Add("No partition table present.");
      return new PartitionTableVerification(this._scheme, false, issues.AsReadOnly());
    }

    if (this._scheme == PartitionScheme.Mbr) {
      this.VerifyMbr(issues);
      return new PartitionTableVerification(this._scheme, issues.Count == 0, issues.AsReadOnly());
    }

    this.VerifyGpt(issues);
    return new PartitionTableVerification(this._scheme, issues.Count == 0, issues.AsReadOnly());
  }

  /// <summary>
  /// Reloads the in-memory partition table from disk. Useful after
  /// out-of-band edits to the stream.
  /// </summary>
  public void Reload() {
    this._entries.Clear();
    if (this._stream.Length < SectorSize * 2L) {
      this._scheme = PartitionScheme.None;
      return;
    }

    var head = new byte[Math.Min(4096, (int)this._stream.Length)];
    this._stream.Position = 0;
    var read = this._stream.Read(head, 0, head.Length);
    if (read < SectorSize) {
      this._scheme = PartitionScheme.None;
      return;
    }

    if (GptParser.IsGpt(head)) {
      try {
        var parsed = GptParser.Parse(this._stream);
        // Read entry size from the on-disk header for round-trip preservation.
        this._stream.Position = (long)GptHeaderLba * SectorSize;
        Span<byte> hdr = stackalloc byte[GptHeaderSize];
        this._stream.ReadExactly(hdr);
        this._gptEntryCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr[80..]);
        this._gptEntrySize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[84..]);
        if (this._gptEntrySize < DefaultGptEntrySize) this._gptEntrySize = DefaultGptEntrySize;
        if (this._gptEntryCount == 0) this._gptEntryCount = GptEntryCount;

        this._scheme = PartitionScheme.Gpt;
        this._entries.AddRange(parsed);
        this.ReindexAll();
        return;
      } catch {
        // Fall through to MBR.
      }
    }

    if (MbrParser.IsMbr(head)) {
      this._scheme = PartitionScheme.Mbr;
      // Use the full parser so logical entries are included as well.
      List<PartitionEntry> parsed;
      try {
        parsed = MbrParser.Parse(this._stream);
      } catch {
        parsed = [.. MbrParser.ParsePrimary(head)];
      }

      // MbrParser.Parse returns primaries (non-extended) plus EBR-discovered
      // logicals, but skips the extended container itself. Re-read sector 0
      // separately to surface the extended container so callers can see it.
      this._stream.Position = 0;
      var sector0 = new byte[SectorSize];
      this._stream.ReadExactly(sector0);
      var primariesFromTable = MbrParser.ParsePrimary(sector0);
      var entries = new List<PartitionEntry>();
      foreach (var p in primariesFromTable)
        entries.Add(p);
      // Append EBR-derived logicals from `parsed` (those have Source == "EBR").
      foreach (var p in parsed)
        if (p.Source == "EBR")
          entries.Add(p);
      this._entries.AddRange(entries);
      this.ReindexAll();
      return;
    }

    this._scheme = PartitionScheme.None;
  }

  // ── Overlap / validation helpers ───────────────────────────────────

  private static void ValidateRange(long startByteOffset, long lengthBytes) {
    if (startByteOffset < 0) throw new ArgumentOutOfRangeException(nameof(startByteOffset));
    if (lengthBytes <= 0) throw new ArgumentOutOfRangeException(nameof(lengthBytes));
    if (startByteOffset % SectorSize != 0)
      throw new ArgumentException($"Start offset must be sector-aligned ({SectorSize} bytes).", nameof(startByteOffset));
    if (lengthBytes % SectorSize != 0)
      throw new ArgumentException($"Length must be a multiple of sector size ({SectorSize} bytes).", nameof(lengthBytes));
  }

  private void RequireFits(long startByteOffset, long lengthBytes) {
    if (startByteOffset + lengthBytes > this._stream.Length)
      throw new InvalidOperationException(
        $"Partition end ({startByteOffset + lengthBytes}) exceeds disk length ({this._stream.Length}).");
  }

  private void RequireNoOverlap(long startByteOffset, long lengthBytes) {
    foreach (var existing in this._entries) {
      // Extended container is allowed to encompass its logicals — skip it
      // for primaries being added inside.
      var overlapStart = Math.Max(existing.StartOffset, startByteOffset);
      var overlapEnd = Math.Min(existing.StartOffset + existing.Size, startByteOffset + lengthBytes);
      if (overlapStart >= overlapEnd) continue;

      // A logical inside the extended container does not "overlap" the
      // container itself for our purposes.
      if (existing.Source == "MBR (Extended Container)"
          && startByteOffset >= existing.StartOffset
          && startByteOffset + lengthBytes <= existing.StartOffset + existing.Size)
        continue;

      throw new InvalidOperationException(
        $"New partition [{startByteOffset}..{startByteOffset + lengthBytes}) overlaps existing partition #{existing.Index} [{existing.StartOffset}..{existing.StartOffset + existing.Size}).");
    }
  }

  private int CountPrimary()
    => this._entries.Count(e => e.Source is "MBR" or "MBR (Extended Container)");

  private bool HasExtendedContainer()
    => this._entries.Any(e => e.Source == "MBR (Extended Container)");

  private PartitionEntry? FindExtendedContainer()
    => this._entries.FirstOrDefault(e => e.Source == "MBR (Extended Container)");

  private int FirstLogicalIndex() {
    for (var i = 0; i < this._entries.Count; ++i)
      if (this._entries[i].Source == "EBR")
        return i;
    return this._entries.Count;
  }

  private void ReindexAll() {
    for (var i = 0; i < this._entries.Count; ++i) {
      var e = this._entries[i];
      this._entries[i] = new PartitionEntry {
        Index = i,
        StartOffset = e.StartOffset,
        Size = e.Size,
        TypeName = e.TypeName,
        IsActive = e.IsActive,
        TypeCode = e.TypeCode,
        Name = e.Name,
        Source = e.Source
      };
    }
  }

  // ── MBR writers ────────────────────────────────────────────────────

  private void InitializeEmptyMbr() {
    var sector = new byte[SectorSize];
    BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(MbrSignatureOffset), MbrBootSignature);
    this._stream.Position = 0;
    this._stream.Write(sector);
  }

  private void RewriteMbr() {
    // Read sector 0 first so we preserve boot code in the first 446 bytes.
    var sector = new byte[SectorSize];
    if (this._stream.Length >= SectorSize) {
      this._stream.Position = 0;
      this._stream.ReadExactly(sector);
    }

    // Clear the 4 entries.
    Array.Clear(sector, MbrTableOffset, 4 * MbrEntrySize);

    var primarySlot = 0;
    foreach (var e in this._entries) {
      if (e.Source == "EBR") continue;
      if (primarySlot >= 4) break;

      var entry = sector.AsSpan(MbrTableOffset + primarySlot * MbrEntrySize, MbrEntrySize);
      var mbrByte = ParseMbrTypeCode(e.TypeCode);
      entry[0] = e.IsActive ? (byte)0x80 : (byte)0x00;
      // CHS "use LBA" sentinel — 0xFE/0xFF/0xFF.
      entry[1] = 0xFE; entry[2] = 0xFF; entry[3] = 0xFF;
      entry[4] = mbrByte;
      entry[5] = 0xFE; entry[6] = 0xFF; entry[7] = 0xFF;
      BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)(e.StartOffset / SectorSize));
      BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)(e.Size / SectorSize));
      primarySlot++;
    }

    BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(MbrSignatureOffset), MbrBootSignature);
    this._stream.Position = 0;
    this._stream.Write(sector);
    this._stream.Flush();
  }

  /// <summary>
  /// Rewrites the EBR (Extended Boot Record) chain for the logical
  /// partitions inside the extended container. Convention:
  /// the first EBR sits at the start of the extended container; each
  /// subsequent EBR sits one sector before its logical's data. Each EBR
  /// holds two entries: (1) the logical owned by this EBR, with its LBA
  /// relative to this EBR sector; (2) a pointer to the next EBR with its
  /// LBA relative to the extended container start (zeroed for the last EBR).
  /// </summary>
  private void RewriteEbrChain() {
    var container = this.FindExtendedContainer();
    if (container is null) return;

    var logicals = this._entries.Where(e => e.Source == "EBR")
      .OrderBy(e => e.StartOffset)
      .ToList();

    var containerStartLba = (uint)(container.StartOffset / SectorSize);

    // Always zero the first sector of the container first — when the chain
    // shrinks (logicals removed), we don't want a stale EBR lingering. The
    // active first EBR will be rewritten below if logicals.Count > 0.
    this.ZeroRange(container.StartOffset, SectorSize);

    if (logicals.Count == 0) {
      this._stream.Flush();
      return;
    }

    for (var i = 0; i < logicals.Count; ++i) {
      var logical = logicals[i];
      var dataLba = (uint)(logical.StartOffset / SectorSize);

      // EBR location: first logical -> container start; subsequent -> data-1.
      var ebrLba = i == 0 ? containerStartLba : dataLba - 1;
      var ebrOffset = (long)ebrLba * SectorSize;

      var sector = new byte[SectorSize];
      if (ebrOffset + SectorSize <= this._stream.Length) {
        this._stream.Position = ebrOffset;
        this._stream.ReadExactly(sector);
        Array.Clear(sector, MbrTableOffset, 4 * MbrEntrySize);
      }

      // Entry 0: this logical (LBA relative to this EBR).
      {
        var entry = sector.AsSpan(MbrTableOffset, MbrEntrySize);
        var mbrByte = ParseMbrTypeCode(logical.TypeCode);
        entry[0] = logical.IsActive ? (byte)0x80 : (byte)0x00;
        entry[1] = 0xFE; entry[2] = 0xFF; entry[3] = 0xFF;
        entry[4] = mbrByte;
        entry[5] = 0xFE; entry[6] = 0xFF; entry[7] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], dataLba - ebrLba);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)(logical.Size / SectorSize));
      }

      // Entry 1: pointer to next EBR (relative to extended container start),
      // or all-zero on the last EBR.
      if (i + 1 < logicals.Count) {
        var nextLogical = logicals[i + 1];
        var nextDataLba = (uint)(nextLogical.StartOffset / SectorSize);
        var nextEbrLba = nextDataLba - 1;

        var entry = sector.AsSpan(MbrTableOffset + MbrEntrySize, MbrEntrySize);
        entry[0] = 0x00;
        entry[1] = 0xFE; entry[2] = 0xFF; entry[3] = 0xFF;
        entry[4] = 0x05;
        entry[5] = 0xFE; entry[6] = 0xFF; entry[7] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], nextEbrLba - containerStartLba);
        var nextLastLba = nextDataLba + (uint)(nextLogical.Size / SectorSize) - 1;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], nextLastLba - nextEbrLba + 1);
      }

      BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(MbrSignatureOffset), MbrBootSignature);
      this._stream.Position = ebrOffset;
      this._stream.Write(sector);
    }
    this._stream.Flush();
  }

  // ── GPT writers ────────────────────────────────────────────────────

  private void RewriteGpt() {
    var totalSectors = this._stream.Length / SectorSize;
    if (totalSectors < 34 + GptReservedSectors)
      throw new InvalidOperationException(
        $"Stream too small for GPT layout ({this._stream.Length} bytes); need at least {(34 + GptReservedSectors) * SectorSize}.");

    var entrySize = (int)this._gptEntrySize;
    var entryCount = (int)this._gptEntryCount;
    var entryArrayBytes = entrySize * entryCount;

    // Build entry array.
    var entriesBuf = new byte[entryArrayBytes];
    for (var i = 0; i < this._entries.Count && i < entryCount; ++i) {
      var e = this._entries[i];
      var slot = entriesBuf.AsSpan(i * entrySize, entrySize);
      var typeGuid = Guid.TryParse(e.TypeCode, out var g) ? g : Guid.Empty;
      WriteMixedEndianGuid(slot, typeGuid);
      WriteMixedEndianGuid(slot[16..], DeriveUniqueGuid(i, e.StartOffset));
      var firstLba = (ulong)(e.StartOffset / SectorSize);
      var lastLba = (ulong)((e.StartOffset + e.Size) / SectorSize - 1);
      BinaryPrimitives.WriteUInt64LittleEndian(slot[32..], firstLba);
      BinaryPrimitives.WriteUInt64LittleEndian(slot[40..], lastLba);
      BinaryPrimitives.WriteUInt64LittleEndian(slot[48..], e.IsActive ? 0x04UL : 0UL);
      var name = e.Name ?? string.Empty;
      var nameBytes = Encoding.Unicode.GetBytes(name);
      var nameMax = Math.Min(72, entrySize - 56);
      if (nameMax > 0) {
        var copyLen = Math.Min(nameMax, nameBytes.Length);
        nameBytes.AsSpan(0, copyLen).CopyTo(slot[56..]);
      }
    }

    var entriesCrc = Crc32.Compute(entriesBuf);

    // Number of sectors needed for the entry array.
    var entrySectors = (entryArrayBytes + SectorSize - 1) / SectorSize;
    var reservedSectors = Math.Max(GptReservedSectors, entrySectors + 1);

    // Primary header at LBA 1.
    var primaryHeader = BuildGptHeader(
      myLba: 1,
      altLba: (ulong)(totalSectors - 1),
      firstUsableLba: (ulong)(2 + entrySectors),
      lastUsableLba: (ulong)(totalSectors - reservedSectors),
      diskGuid: this.DeriveDiskGuid(),
      entriesLba: 2,
      entriesCrc: entriesCrc,
      entryCount: entryCount,
      entrySize: entrySize);

    var backupEntriesLba = (ulong)(totalSectors - reservedSectors);
    var backupHeader = BuildGptHeader(
      myLba: (ulong)(totalSectors - 1),
      altLba: 1,
      firstUsableLba: (ulong)(2 + entrySectors),
      lastUsableLba: (ulong)(totalSectors - reservedSectors),
      diskGuid: this.DeriveDiskGuid(),
      entriesLba: backupEntriesLba,
      entriesCrc: entriesCrc,
      entryCount: entryCount,
      entrySize: entrySize);

    // Protective MBR at LBA 0.
    this.WriteProtectiveMbr(totalSectors);

    var headerSector = new byte[SectorSize];
    primaryHeader.CopyTo(headerSector, 0);
    this._stream.Position = SectorSize;
    this._stream.Write(headerSector);

    this._stream.Position = 2L * SectorSize;
    this._stream.Write(entriesBuf);

    this._stream.Position = (long)backupEntriesLba * SectorSize;
    this._stream.Write(entriesBuf);

    Array.Clear(headerSector);
    backupHeader.CopyTo(headerSector, 0);
    this._stream.Position = (totalSectors - 1) * SectorSize;
    this._stream.Write(headerSector);

    this._stream.Flush();
  }

  private void WriteProtectiveMbr(long totalSectors) {
    var mbr = new byte[SectorSize];
    if (this._stream.Length >= SectorSize) {
      this._stream.Position = 0;
      this._stream.ReadExactly(mbr);
    }
    Array.Clear(mbr, MbrTableOffset, 4 * MbrEntrySize);

    var entry = mbr.AsSpan(MbrTableOffset, MbrEntrySize);
    entry[0] = 0x00;
    entry[1] = 0x00; entry[2] = 0x02; entry[3] = 0x00;
    entry[4] = 0xEE;
    entry[5] = 0xFF; entry[6] = 0xFF; entry[7] = 0xFF;
    BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 1u);
    var sectorsForProtective = totalSectors - 1 > uint.MaxValue ? uint.MaxValue : (uint)(totalSectors - 1);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], sectorsForProtective);

    BinaryPrimitives.WriteUInt16LittleEndian(mbr.AsSpan(MbrSignatureOffset), MbrBootSignature);
    this._stream.Position = 0;
    this._stream.Write(mbr);
  }

  private static byte[] BuildGptHeader(
    ulong myLba, ulong altLba, ulong firstUsableLba, ulong lastUsableLba,
    Guid diskGuid, ulong entriesLba, uint entriesCrc, int entryCount, int entrySize) {
    var hdr = new byte[GptHeaderSize];
    GptSignature.CopyTo(hdr);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), 0x00010000u);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12), GptHeaderSize);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(20), 0u);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(24), myLba);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(32), altLba);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(40), firstUsableLba);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(48), lastUsableLba);
    WriteMixedEndianGuid(hdr.AsSpan(56, 16), diskGuid);
    BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(72), entriesLba);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(80), (uint)entryCount);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(84), (uint)entrySize);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(88), entriesCrc);

    var headerCrc = Crc32.Compute(hdr);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16), headerCrc);
    return hdr;
  }

  private Guid DeriveDiskGuid() {
    Span<byte> seed = stackalloc byte[16];
    BinaryPrimitives.WriteInt64LittleEndian(seed, this._stream.Length);
    BinaryPrimitives.WriteInt64LittleEndian(seed[8..], 0x4544495459474C44L);
    return new Guid(seed);
  }

  private static Guid DeriveUniqueGuid(int index, long startOffset) {
    Span<byte> seed = stackalloc byte[16];
    BinaryPrimitives.WriteInt32LittleEndian(seed, index);
    BinaryPrimitives.WriteInt64LittleEndian(seed[4..], startOffset);
    BinaryPrimitives.WriteInt32LittleEndian(seed[12..], 0x50415254);
    return new Guid(seed);
  }

  private static void WriteMixedEndianGuid(Span<byte> dest, Guid guid) {
    var bytes = guid.ToByteArray();
    bytes.CopyTo(dest);
  }

  // ── Verification ───────────────────────────────────────────────────

  private void VerifyMbr(List<string> issues) {
    if (this._stream.Length < SectorSize) {
      issues.Add("Stream shorter than one sector.");
      return;
    }
    this._stream.Position = 0;
    var sector = new byte[SectorSize];
    this._stream.ReadExactly(sector);
    var sig = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(MbrSignatureOffset));
    if (sig != MbrBootSignature)
      issues.Add($"MBR boot signature missing at offset 510 (expected 0xAA55, got 0x{sig:X4}).");

    foreach (var e in this._entries) {
      if (e.StartOffset < 0 || e.StartOffset + e.Size > this._stream.Length)
        issues.Add($"Partition #{e.Index} extends beyond disk: [{e.StartOffset}..{e.StartOffset + e.Size}), stream length {this._stream.Length}.");
    }

    // EBR-chain consistency check: logicals live inside the container with
    // an EBR sector either at the container start (first logical) or one
    // sector before the data (subsequent logicals).
    var container = this.FindExtendedContainer();
    if (container is not null) {
      foreach (var logical in this._entries.Where(p => p.Source == "EBR")) {
        if (logical.StartOffset < container.StartOffset + SectorSize)
          issues.Add($"Logical #{logical.Index} starts before container_start + 1 sector.");
        if (logical.StartOffset + logical.Size > container.StartOffset + container.Size)
          issues.Add($"Logical #{logical.Index} data extends past extended container end.");
      }
    }
  }

  private void VerifyGpt(List<string> issues) {
    if (this._stream.Length < (34 + GptReservedSectors) * SectorSize) {
      issues.Add("Stream too small to hold primary + backup GPT.");
      return;
    }

    // Primary header.
    this._stream.Position = (long)GptHeaderLba * SectorSize;
    var headerSector = new byte[SectorSize];
    this._stream.ReadExactly(headerSector);
    var primaryOk = VerifyGptHeader(headerSector, isPrimary: true, expectedMyLba: 1, issues, out var entriesLba, out var entryCount, out var entrySize, out var entriesCrc);

    if (primaryOk) {
      var entryBytes = (long)entryCount * entrySize;
      if (entryBytes <= 0 || entryBytes > int.MaxValue) {
        issues.Add($"GPT entry array size {entryBytes} out of range.");
      } else {
        var buf = new byte[entryBytes];
        this._stream.Position = (long)entriesLba * SectorSize;
        this._stream.ReadExactly(buf);
        var crc = Crc32.Compute(buf);
        if (crc != entriesCrc)
          issues.Add($"Primary GPT entry-array CRC mismatch: header expects 0x{entriesCrc:X8}, computed 0x{crc:X8}.");
      }
    }

    // Backup header.
    var totalSectors = this._stream.Length / SectorSize;
    this._stream.Position = (totalSectors - 1) * SectorSize;
    var backupSector = new byte[SectorSize];
    this._stream.ReadExactly(backupSector);
    var backupOk = VerifyGptHeader(backupSector, isPrimary: false, expectedMyLba: (ulong)(totalSectors - 1), issues,
      out var backupEntriesLba, out var backupEntryCount, out var backupEntrySize, out var backupEntriesCrc);

    if (backupOk) {
      var entryBytes = (long)backupEntryCount * backupEntrySize;
      if (entryBytes > 0 && entryBytes <= int.MaxValue) {
        var buf = new byte[entryBytes];
        this._stream.Position = (long)backupEntriesLba * SectorSize;
        this._stream.ReadExactly(buf);
        var crc = Crc32.Compute(buf);
        if (crc != backupEntriesCrc)
          issues.Add($"Backup GPT entry-array CRC mismatch: header expects 0x{backupEntriesCrc:X8}, computed 0x{crc:X8}.");
      }
    }

    if (primaryOk && backupOk) {
      if (entryCount != backupEntryCount)
        issues.Add($"Primary/backup entry-count divergence ({entryCount} vs {backupEntryCount}).");
      if (entrySize != backupEntrySize)
        issues.Add($"Primary/backup entry-size divergence ({entrySize} vs {backupEntrySize}).");
      if (entriesCrc != backupEntriesCrc)
        issues.Add($"Primary/backup entry-array CRC divergence (0x{entriesCrc:X8} vs 0x{backupEntriesCrc:X8}).");
    }
  }

  private static bool VerifyGptHeader(byte[] sector, bool isPrimary, ulong expectedMyLba, List<string> issues,
    out ulong entriesLba, out uint entryCount, out uint entrySize, out uint entriesCrc) {
    entriesLba = 0; entryCount = 0; entrySize = 0; entriesCrc = 0;
    var label = isPrimary ? "Primary" : "Backup";

    if (!sector.AsSpan(0, 8).SequenceEqual(GptSignature)) {
      issues.Add($"{label} GPT signature missing (expected 'EFI PART').");
      return false;
    }
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(12));
    if (headerSize < GptHeaderSize || headerSize > SectorSize) {
      issues.Add($"{label} GPT header size {headerSize} out of range.");
      return false;
    }
    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(16));
    // Re-compute header CRC with crc field zeroed.
    var hdrCopy = new byte[headerSize];
    sector.AsSpan(0, (int)headerSize).CopyTo(hdrCopy);
    Array.Clear(hdrCopy, 16, 4);
    var actualCrc = Crc32.Compute(hdrCopy);
    if (actualCrc != storedCrc)
      issues.Add($"{label} GPT header CRC mismatch: stored 0x{storedCrc:X8}, computed 0x{actualCrc:X8}.");

    var myLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(24));
    if (myLba != expectedMyLba)
      issues.Add($"{label} GPT MyLBA mismatch: expected {expectedMyLba}, got {myLba}.");

    entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(72));
    entryCount = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(80));
    entrySize = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(84));
    entriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(88));
    return true;
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private void RequireValidIndex(int index) {
    if (index < 0 || index >= this._entries.Count)
      throw new ArgumentOutOfRangeException(nameof(index),
        $"Partition index {index} out of range [0..{this._entries.Count}).");
  }

  private void ZeroRange(long start, long length) {
    if (length <= 0) return;
    var zeros = new byte[Math.Min(length, 64 * 1024)];
    this._stream.Position = start;
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(zeros.Length, remaining);
      this._stream.Write(zeros, 0, chunk);
      remaining -= chunk;
    }
    this._stream.Flush();
  }

  private static byte ParseMbrTypeCode(string code) {
    if (string.IsNullOrEmpty(code)) return 0x00;
    var trimmed = code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? code[2..] : code;
    return byte.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
      System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : (byte)0x00;
  }
}

/// <summary>
/// Non-owning sliding-window view over a partition's byte range within a
/// larger disk image. Reads/writes are clamped to the [start, start+size)
/// range; the underlying disk stream is never closed by <see cref="Dispose"/>.
/// Used by <see cref="PartitionEditor.ConvertFilesystem"/> to hand the
/// in-place converter a view that looks like a standalone filesystem image.
/// </summary>
internal sealed class PartitionSubStream(Stream inner, long startOffset, long length) : Stream {
  private readonly Stream _inner = inner;
  private readonly long _start = startOffset;
  private readonly long _length = length;
  private long _position;

  public override bool CanRead => this._inner.CanRead;
  public override bool CanSeek => this._inner.CanSeek;
  public override bool CanWrite => this._inner.CanWrite;
  public override long Length => this._length;
  public override long Position { get => this._position; set => this._position = value; }
  public override void Flush() => this._inner.Flush();

  public override int Read(byte[] buffer, int offset, int count) {
    if (this._position >= this._length) return 0;
    var remaining = this._length - this._position;
    var toRead = (int)Math.Min(count, remaining);
    if (toRead <= 0) return 0;
    this._inner.Position = this._start + this._position;
    var read = this._inner.Read(buffer, offset, toRead);
    this._position += read;
    return read;
  }

  public override long Seek(long offset, SeekOrigin origin) {
    this._position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._length + offset,
      _ => this._position,
    };
    return this._position;
  }

  public override void SetLength(long value) {
    // Substream length is fixed at the partition boundary; treat SetLength
    // as a no-op for shrinks (caller is asking us to truncate inside the
    // partition, which is fine — the bytes past `value` simply become
    // unused inside the partition).
    if (value > this._length)
      throw new IOException($"Cannot extend partition view beyond {this._length} bytes.");
  }

  public override void Write(byte[] buffer, int offset, int count) {
    if (this._position + count > this._length)
      throw new IOException($"Write at position {this._position} for {count} bytes exceeds partition view length {this._length}.");
    this._inner.Position = this._start + this._position;
    this._inner.Write(buffer, offset, count);
    this._position += count;
  }

  protected override void Dispose(bool disposing) {
    // Do NOT dispose the underlying disk stream.
  }
}

/// <summary>Partition scheme detected on a disk image.</summary>
public enum PartitionScheme {
  /// <summary>No recognised partition table (raw / superfloppy / blank disk).</summary>
  None,
  /// <summary>MBR / DOS partition table at LBA 0.</summary>
  Mbr,
  /// <summary>GPT (UEFI) partition table with protective MBR at LBA 0.</summary>
  Gpt
}

/// <summary>
/// Result of <see cref="PartitionEditor.Verify"/> — a snapshot of integrity
/// checks against the on-disk partition table.
/// </summary>
/// <param name="Scheme">Detected partition scheme.</param>
/// <param name="IsValid"><c>true</c> when no issues were detected.</param>
/// <param name="Issues">Human-readable diagnostics for each detected problem.</param>
public sealed record PartitionTableVerification(
  PartitionScheme Scheme,
  bool IsValid,
  IReadOnlyList<string> Issues);
