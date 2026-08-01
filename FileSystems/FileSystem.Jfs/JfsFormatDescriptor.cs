#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Jfs;

/// <summary>
/// Descriptor for IBM JFS1 aggregate images. Reader walks the kernel-fixed AIT
/// (block 11), the indirect fileset AIM → IAG → FSIT path, and the inline
/// dtree root + xtree extents. Writer emits a complete WORM image with
/// FILESYSTEM_I → AIM → IAG → FSIT, dual superblocks, dmap+dmapctl with
/// canonical <c>ujfs_adjtree</c> buddy tree, both AIT/AIM copies, and an
/// inline-dtroot root directory with up to 8 user files. Validated clean
/// against real <c>fsck.jfs -n -f -v</c>.
/// <para>
/// State: <b>R/W (extended mutation past leaf-only)</b>.
/// <see cref="JfsMutator"/> implements:
/// <list type="bullet">
///   <item><b>arbitrary path depth</b> add/remove (descend by name through any
///   intermediate directory whose dtree is inline OR external/router);</item>
///   <item><b>long names via continuation slots</b> chained through the head
///   ldtentry's <c>next</c> byte (both insert and remove walk the chain);</item>
///   <item><b>external dtree leaf-page insert/delete</b> when the directory's
///   dtroot has been promoted to a router (in-place stbl shift + freelist
///   restore, with no split);</item>
///   <item><b>recursive subdirectory removal</b> — DFS the dtree, free every
///   child file's xtree extents + inode + dmap bits, free the dtree pages
///   themselves, then close out the entry in the parent;</item>
///   <item><b>multi-dmap allocation</b> — walks both dmap pages the writer
///   reserves before declaring the image full;</item>
///   <item><b>xtree extent allocate/free</b> with inline xad slots and dmap
///   binary-buddy <c>ujfs_adjtree</c> rerun for every modified dmap.</item>
/// </list>
/// Operations that genuinely need multi-week scope still throw
/// <see cref="NotSupportedException"/> with a SPECIFIC message naming what's
/// unsupported: inline dtroot leaf split, external dtree leaf split, xtree
/// root promotion to non-leaf, IAG full / FSIT extent growth.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://jfs.sourceforge.net/project/pub/jfslayout.pdf</c> — "JFS Layout", the official on-disk format document (superblock, dmap/dmapctl, dtree/xtree, IAG)</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/jfs</c> — mainline kernel implementation; jfsutils' <c>fsck.jfs</c> is the conformance gate</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/JFS_(file_system)</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class JfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
                                          IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable,
                                          IFormatOptionsSchema, ILayoutOptimizable , IFilesystemExtentMap, IWipeEmpty {
  /// <summary>
  /// JFS aggregate geometry (4 KiB blocks, single allocation group, fixed
  /// metadata layout) is not tunable, so the only honoured knob is the volume
  /// label stored in the superblock <c>s_label[16]</c> field (offset 152).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: "",
      Description: "JFS volume label stored in s_label (max 16 ASCII chars)."),
  ];

  // WORM write constraints.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 16L * 1024 * 1024;
  public string AcceptedInputsDescription =>
    "JFS1 filesystem image; single allocation group; long names supported via continuation slots.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    // Long names are supported via continuation-slot chains; no leaf length cap.
    reason = null;
    return true;
  }

  public string Id => "Jfs";
  public string DisplayName => "JFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".jfs";
  public IReadOnlyList<string> Extensions => [".jfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("JFS1"u8.ToArray(), Offset: 32768, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "IBM Journaled File System image (R/W: arbitrary-depth dtree mutation w/ long names + recursive subdir removal)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new JfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new JfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
    }
  }

  /// <summary>
  /// Opens a single filesystem entry as a bounded read-only stream. The
  /// reader produces the decoded file bytes by walking the entry's extent
  /// or block chain; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so cluster/extent slack past the entry's
  /// end is physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new JfsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new JfsWriter();
    if (options.HasOption("VolumeLabel")) w.SetVolumeLabel(options.GetOption("VolumeLabel", ""));
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the aggregate out; reading a large
      // input into a byte[] would cap it at what an array can hold.
      if (info.InMemoryContent is { } bytes)
        w.AddFile(info.ArchiveName, bytes);
      else
        w.AddStreamingFile(info.ArchiveName, new FileInfo(info.FullPath).Length,
                           () => File.OpenRead(info.FullPath));
    }
    if (output.CanSeek) w.BuildToStreaming(output);
    else w.WriteTo(output);
  }

  /// <summary>
  /// Two-pass streaming creation. JFS carries NO data checksum and stores file
  /// bodies in dedicated xtree extents (directories are stored inline in the
  /// dinode, so they never reach the streaming path), which makes the format
  /// fully streamable: pass 1 builds every metadata structure with the file
  /// extents left zero; pass 2 seeks to each file's extent and copies its bytes
  /// from <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// in 64 KiB chunks. The output is byte-identical to <see cref="Create"/> for
  /// the same inputs. Falls back to the buffered default on a non-seekable
  /// target.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new JfsWriter();
    if (options.HasOption("VolumeLabel")) w.SetVolumeLabel(options.GetOption("VolumeLabel", ""));
    if (!output.CanSeek) {
      // Non-seekable target: buffer each entry once and emit the classic image.
      foreach (var input in inputs) {
        if (input.IsDirectory) continue;
        using var src = input.OpenStream();
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        w.AddFile(input.Name, ms.ToArray());
      }
      w.WriteTo(output);
      return;
    }
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.BuildToStreaming(output);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware JFS1 defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start single-aggregate image with FILESYSTEM_I → AIM →
  /// IAG → FSIT, dual superblocks, dmap+dmapctl, and an inline-dtroot.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Buffering the rebuilt image would cap the aggregate at what a byte[] can
    // hold, so the packing modes stream: each entry is spilled to scratch and
    // the writer pulls it back while laying out the extents.
    // Every mode streams: end-pack and carve-hole order their entries from
    // scratch inside the rebuilder, so none of them has to fall back to the
    // buffered path that a volume past two gigabytes cannot use.
    {
      JfsWriter? writer = null;
      Stream? target = null;
      var spill = new List<string>();
      try {
        DefragRebuilder.RebuildStreaming(archive, options,
          readEntries: ReadEntries,
          beginWrite: s => { writer = new JfsWriter(); target = s; },
          writeEntry: (name, data) => {
            var path = Path.GetTempFileName();
            spill.Add(path);
            File.WriteAllBytes(path, data);
            writer!.AddStreamingFile(name, data.LongLength, () => File.OpenRead(path));
          },
          finishWrite: () => writer!.BuildToStreaming(target!));
      } finally {
        foreach (var path in spill)
          try { File.Delete(path); } catch { /* scratch file already gone */ }
      }
    }
  }

  // ── IArchiveModifiable (extended-scope in-place mutation) ──────────────
  // Routes Add/Remove through JfsMutator. The mutator now supports:
  //   • arbitrary path depth (descend by name through inline and external/router
  //     dtrees);
  //   • long names via continuation slots chained through the head ldtentry's
  //     `next` byte (both insert and remove);
  //   • external dtree leaf-page insert/delete when the dtroot has been
  //     promoted to a router (in-place stbl shift + freelist restore);
  //   • recursive subdirectory removal (DFS the dtree, free every child's
  //     xtree extents + inode + dmap bits, free the dtree pages themselves);
  //   • multi-dmap allocation across the two dmap pages the writer reserves;
  //   • xtree extent allocate/free + full ujfs_adjtree rerun.
  // Genuinely multi-week scope still falls back honestly: inline dtroot leaf
  // split, external dtree leaf split, xtree root promotion, IAG full, FSIT
  // extent growth — caller falls back to defragment-rebuild.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in — and where it can still edit, it has no room
    // to grow a full volume. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var image = ReadAll(archive);
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var data = input.ReadContent();
      // Pass the archive-relative path through unchanged — the mutator
      // descends into intermediate directories by name.
      JfsMutator.AddRootFile(image, input.ArchiveName, data);
    }
    WriteAll(archive, image);
  }

  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var image = ReadAll(archive);
    foreach (var name in entryNames) {
      // Pass the archive-relative path through unchanged so the mutator can
      // descend to nested directories before removing the leaf entry. Removing
      // a directory recursively frees its children + dtree pages.
      JfsMutator.RemoveRootEntry(image, name);
    }
    WriteAll(archive, image);
  }

  private static byte[] ReadAll(Stream s) {
    if (s.CanSeek) s.Position = 0;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static void WriteAll(Stream s, byte[] data) {
    if (s.CanSeek) {
      s.Position = 0;
      s.Write(data);
      s.SetLength(data.Length);
    } else {
      s.Write(data);
    }
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new JfsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => JfsExtentMap.Enumerate(image);

  /// <summary>
  /// Zero-fills every block the allocation map reports as free — which is where
  /// a removed file's bytes stay until something else claims them.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = JfsExtentMap.Enumerate(image).ToList();
    if (extents.Count == 0) return 0;
    // The map is per block and says nothing about where a file ends inside its
    // last one, so there are no cluster tips to trim from it.
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
