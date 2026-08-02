#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Fat;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Msa;

/// <summary>
/// Descriptor for Atari ST MSA (Magic Shadow Archiver) disk images — an
/// RLE-compressed track-image container wrapping a FAT12 floppy filesystem.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/hatari/hatari</c> — Hatari emulator; its MSA disk-image support is the de-facto reference implementation</description></item>
///   <item><description>Magic Shadow Archiver original documentation (Atari ST, Seimet) — no stable online spec</description></item>
/// </list>
/// </summary>
public sealed class MsaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty {
  public string Id => "Msa";
  public string DisplayName => "MSA (Magic Shadow Archiver)";
  public FormatCategory Category => FormatCategory.Archive;
  // WORM, not R/W: Add/Remove rebuild the whole image (read-all -> re-create),
  // so the verb works via rebuild but nothing is modified in place. CanModify
  // must not be advertised. See Compression.Registry/FormatCapabilities.cs.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest;
  public string DefaultExtension => ".msa";
  public IReadOnlyList<string> Extensions => [".msa"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x0E, 0x0F], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rle", "RLE")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Atari ST Magic Shadow Archiver disk image with RLE compression";

  /// <summary>
  /// Lists the files on the floppy the image holds. An MSA file wraps a GEMDOS
  /// volume, and this descriptor's Add and Remove already work on that volume's
  /// files, so this reads them too — a disc whose filesystem cannot be walked
  /// falls back to the raw image as a single entry.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MsaReader(stream);
    var disk = DecodedDisk(r);
    if (disk != null)
      try {
        using var inner = new FileSystem.Gemdos.GemdosReader(new MemoryStream(disk, writable: false));
        var entries = inner.Entries.Where(e => !e.IsDirectory).ToList();
        if (entries.Count > 0)
          return entries.Select((e, i) => new ArchiveEntryInfo(
            i, e.Name, e.Size, -1, "RLE", false, false, null)).ToList();
      } catch {
        // fall through to the raw image
      }

    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "RLE", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MsaReader(stream);
    var disk = DecodedDisk(r);
    if (disk != null)
      try {
        using var inner = new FileSystem.Gemdos.GemdosReader(new MemoryStream(disk, writable: false));
        var wrote = false;
        foreach (var e in inner.Entries) {
          if (e.IsDirectory) continue;
          if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
          WriteFile(outputDir, e.Name, inner.Extract(e));
          wrote = true;
        }
        if (wrote) return;
      } catch {
        // fall through to the raw image
      }

    foreach (var e in r.Entries)
      WriteFile(outputDir, e.Name, r.Extract(e));
  }

  /// <summary>The decoded floppy, or null when the image holds no track data.</summary>
  private static byte[]? DecodedDisk(MsaReader reader) {
    var entry = reader.Entries.FirstOrDefault();
    if (entry == null) return null;
    var disk = reader.Extract(entry);
    return disk.Length >= 512 ? disk : null;
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
    var r = new MsaReader(archive);
    foreach (var e in r.Entries) {
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

  /// <summary>
  /// Builds an MSA image holding the inputs. An MSA file is a compressed Atari
  /// ST floppy, so the files go into a GEMDOS volume first and that volume is
  /// what gets encoded.
  /// <para>
  /// A single input that already is a floppy image — the .st → .msa conversion —
  /// is encoded as it stands. Anything else used to be treated the same way,
  /// which meant the first input's bytes were read as a disk image and every
  /// other input was dropped without a word: three files in, one nonsensical
  /// "floppy" out.
  /// </para>
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();
    if (fileList.Count == 0) return;

    if (fileList.Count == 1 && LooksLikeFloppyImage(fileList[0].Data)) {
      MsaWriter.Write(output, fileList[0].Data);
      return;
    }

    // A floppy is all an MSA file can describe. Handing more than fits to the
    // GEMDOS writer produced an image whose directory ran off the end of the
    // volume — it read back as garbage rather than saying no.
    var payload = fileList.Sum(f => (long)f.Data.Length);
    if (payload > UsableFloppyBytes)
      throw new InvalidOperationException(
        $"MSA: combined input size {payload:N0} bytes exceeds the 720 KB Atari ST floppy " +
        $"this format describes ({UsableFloppyBytes:N0} bytes usable).");

    // 720 KB double-sided, the ST's common floppy: 80 tracks x 9 sectors x 2
    // sides. MsaWriter's defaults describe the same geometry.
    var writer = new FileSystem.Gemdos.GemdosWriter();
    foreach (var (name, data) in fileList)
      writer.AddFile(name, data);
    var floppy = writer.Build(totalSectors: 1440, bytesPerSector: 512,
      sectorsPerCluster: 2, rootEntries: 112);
    MsaWriter.Write(output, floppy);
  }

  /// <summary>
  /// What a 720 KB volume holds once its boot sector, both FATs and the root
  /// directory are subtracted.
  /// </summary>
  private const long UsableFloppyBytes = 1440L * 512 - 24L * 512;

  /// <summary>
  /// Whether these bytes are already an ST floppy image: one of the standard
  /// capacities, with a BPB whose sector size and media descriptor read as a
  /// GEMDOS/FAT boot sector.
  /// </summary>
  private static bool LooksLikeFloppyImage(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    var standardSizes = new[] { 360 * 1024, 400 * 1024, 720 * 1024, 800 * 1024, 1440 * 1024 };
    if (!standardSizes.Contains(data.Length)) return false;
    if (data.Length < 32) return false;
    var bytesPerSector = data[11] | (data[12] << 8);
    if (bytesPerSector is not (256 or 512 or 1024)) return false;
    var sectorsPerCluster = data[13];
    return sectorsPerCluster is 1 or 2 or 4;
  }

  /// <summary>
  /// Adds files to the FAT12 filesystem inside an existing MSA image. Each call
  /// performs decode → modify FAT → re-encode (see <see cref="MsaModifier"/>);
  /// per-track RLE compression makes anything cheaper architecturally impossible.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    ModifyInner(archive,
      volume => new FileSystem.Gemdos.GemdosFormatDescriptor().Add(volume, inputs),
      () => {
        foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
          MsaModifier.AddFile(archive, name, data);
      });
  }

  /// <summary>
  /// Removes files from the FAT12 filesystem inside an existing MSA image.
  /// Inner-layer wipe is delegated to <see cref="FileSystem.Fat.FatRemover"/>
  /// (zeros cluster bytes + cluster-tip slack + dirent + FAT entries), then the
  /// modified flat image is re-encoded to MSA tracks.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    ModifyInner(archive,
      volume => new FileSystem.Gemdos.GemdosFormatDescriptor().Remove(volume, entryNames),
      () => {
        foreach (var name in entryNames)
          MsaModifier.RemoveFile(archive, name);
      });
  }

  /// <summary>
  /// Applies <paramref name="onGemdos" /> to the volume inside the image and
  /// re-encodes it. A volume the GEMDOS layer does not recognise — a
  /// PC-formatted floppy in an MSA wrapper — falls to <paramref name="onFat" />,
  /// which edits the same bytes through the FAT reader instead.
  /// </summary>
  private static void ModifyInner(Stream archive, Action<Stream> onGemdos, Action onFat) {
    archive.Position = 0;
    var reader = new MsaReader(archive);
    if (reader.Entries.Count == 0) { onFat(); return; }

    var flat = reader.Extract(reader.Entries[0]);
    var geometry = (reader.SectorsPerTrack, reader.Sides);
    using var volume = new MemoryStream();
    volume.Write(flat);
    volume.Position = 0;
    if (!FileSystem.Gemdos.GemdosExtentMap.Enumerate(volume).Any()) { onFat(); return; }

    volume.Position = 0;
    onGemdos(volume);

    using var encoded = new MemoryStream();
    MsaWriter.Write(encoded, volume.ToArray(), geometry.SectorsPerTrack, geometry.Sides);
    var rebuilt = encoded.ToArray();
    archive.Position = 0;
    archive.Write(rebuilt, 0, rebuilt.Length);
    archive.SetLength(rebuilt.Length);
  }

  // ── IArchiveDefragmentable ───────────────────────────────────────────

  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Defragments the inner FAT12 filesystem inside an MSA image. The image is
  /// decoded to a flat disk, the FAT layer is defragmented via rebuild (read all
  /// files, rebuild with FatWriter which always start-packs), and the result is
  /// re-encoded to MSA tracks preserving the original geometry.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    archive.Position = 0;
    var reader = new MsaReader(archive);
    if (reader.Entries.Count == 0) return;
    var flat = reader.Extract(reader.Entries[0]);
    var geom = (reader.SectorsPerTrack, reader.Sides, reader.StartTrack, reader.EndTrack);

    // Read all files from the inner FAT image. An MSA is a wrapper: what is
    // inside is normally an Atari FAT volume, but nothing makes it so, and a
    // payload that is not one has no layout this can rearrange. Saying so
    // beats letting the FAT reader's complaint out as if the wrapper itself
    // were corrupt.
    using var fatStream = new MemoryStream(flat, writable: false);
    FatReader fatReader;
    try {
      fatReader = new FatReader(fatStream);
    } catch (InvalidDataException ex) {
      throw new NotSupportedException(
        "MSA: the wrapped image is not a FAT volume, so there is no layout to lay out again — " +
        ex.Message.Split('\n')[0], ex);
    }

    var files = fatReader.Entries
      .Where(e => !e.IsDirectory)
      .Select(e => (e.Name, fatReader.Extract(e)))
      .ToList();

    // Rebuild the FAT image (FatWriter always start-packs = defragmented).
    IReadOnlyList<(string Name, byte[] Data)> ordered = options.Mode switch {
      DefragMode.ConsolidateAtEnd => files.OrderByDescending(f => f.Item2.Length).ToList(),
      _ => files,
    };

    var fw = new FatWriter();
    foreach (var (name, data) in ordered) fw.AddFile(name, data);
    var totalSectors = flat.Length / 512;
    var rebuilt = fw.Build(totalSectors: totalSectors);
    if (rebuilt.Length != flat.Length) {
      var sized = new byte[flat.Length];
      Array.Copy(rebuilt, sized, Math.Min(rebuilt.Length, sized.Length));
      rebuilt = sized;
    }

    // Re-encode to MSA.
    using var ms = new MemoryStream();
    MsaWriter.Write(ms, rebuilt, geom.SectorsPerTrack, geom.Sides);
    var msaBytes = ms.ToArray();
    archive.Position = 0;
    archive.Write(msaBytes, 0, msaBytes.Length);
    archive.SetLength(msaBytes.Length);
  }

  // ── IFilesystemExtentMap ─────────────────────────────────────────────

  /// <summary>
  /// Decodes the MSA tracks to a flat FAT12 image and delegates to
  /// <see cref="FatExtentMap.Enumerate"/> for the actual cluster-chain walk.
  /// The returned offsets are relative to the inner flat image (not the MSA
  /// container) — this matches what the defrag window expects for filesystem
  /// extent maps.
  /// </summary>
  /// <summary>
  /// The layout of the volume inside the image. An ST floppy is read as GEMDOS
  /// first — that is what this descriptor writes and what the machine formats —
  /// and as FAT only when the GEMDOS map claims nothing, which is what a
  /// PC-formatted floppy in an MSA wrapper needs.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    image.Position = 0;
    var reader = new MsaReader(image);
    if (reader.Entries.Count == 0) yield break;
    var flat = reader.Extract(reader.Entries[0]);

    using var flatStream = new MemoryStream(flat, writable: false);
    var extents = InnerExtents(flatStream);
    foreach (var extent in extents)
      yield return extent;
  }

  /// <summary>
  /// The inner volume's extents, from whichever reader recognises it. An empty
  /// result means neither did — the callers must treat that as "unknown", never
  /// as "all free".
  /// </summary>
  private static IReadOnlyList<DefragBlockInfo> InnerExtents(Stream flat) {
    flat.Position = 0;
    var gemdos = FileSystem.Gemdos.GemdosExtentMap.Enumerate(flat).ToList();
    if (gemdos.Count > 0) return gemdos;

    flat.Position = 0;
    return FatExtentMap.Enumerate(flat).ToList();
  }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the filesystem inside an MSA image. MSA is an
  /// outer RLE-compressed container whose track bytes hold no in-place free
  /// space — the extent map's offsets are relative to the decoded flat image,
  /// not the container — so wiping is performed the only honest way: decode the
  /// tracks to the flat image, run the wiper of whichever descriptor reads that
  /// volume (free clusters + cluster tips) on it, then re-encode preserving the
  /// original geometry.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);

    image.Position = 0;
    var reader = new MsaReader(image);
    if (reader.Entries.Count == 0) return 0;
    var flat = reader.Extract(reader.Entries[0]);
    var geom = (reader.SectorsPerTrack, reader.Sides);

    // Wipe the inner volume in memory through whichever descriptor reads it.
    long wiped;
    using (var flatStream = new MemoryStream(flat, writable: true)) {
      flatStream.Position = 0;
      var gemdos = FileSystem.Gemdos.GemdosExtentMap.Enumerate(flatStream).Any();
      wiped = gemdos
        ? new FileSystem.Gemdos.GemdosFormatDescriptor()
          .WipeUnusedSpace(flatStream, wipeClusterTips, wipeDeletedEntries)
        : new FatFormatDescriptor().WipeUnusedSpace(flatStream, wipeClusterTips, wipeDeletedEntries);
      flat = flatStream.ToArray();
    }

    if (wiped == 0) return 0;

    // Re-encode the cleaned flat image, preserving geometry.
    using var ms = new MemoryStream();
    MsaWriter.Write(ms, flat, geom.SectorsPerTrack, geom.Sides);
    var rebuilt = ms.ToArray();
    image.Position = 0;
    image.Write(rebuilt, 0, rebuilt.Length);
    image.SetLength(rebuilt.Length);
    return wiped;
  }
}
