#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Lfs;

/// <summary>
/// 4.4BSD log-structured file system as NetBSD keeps it (LFS version 2, 32-bit addresses):
/// superblock magic <c>0x070162</c> at 8 KiB.
/// </summary>
/// <remarks>
/// <para>Read: a volume NetBSD 11.1's <c>newfs_lfs</c> created, filled through the kernel
/// (rewrites, a deleted file, a hard link, a file with indirect blocks) and checked with
/// <c>fsck_lfs</c> reads back byte-identical to what the kernel's own mount returned. The last
/// checkpoint is read; segments written after it are not rolled forward.</para>
/// <para>Create and modify: volumes are laid out as <c>newfs_lfs</c> lays them out, and every
/// change is appended to the log as new partial segments (summary, file blocks, inode blocks,
/// then the IFILE) followed by a checkpoint, so nothing already written moves. NetBSD's
/// <c>fsck_lfs -n</c> finds our volumes clean, its kernel mounts them and reads the same bytes,
/// and volumes the kernel has written into after us still edit and read back.</para>
/// <para>64-bit LFS and byte-swapped volumes are refused. Sourcing: rung 1 — converted from
/// NetBSD's BSD-licensed LFS headers, <c>newfs_lfs</c> and <c>fsck_lfs</c> segment writer.</para>
/// </remarks>
public sealed class LfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Lfs";
  public string DisplayName => "4.4BSD LFS (NetBSD)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".lfs";
  public IReadOnlyList<string> Extensions => [".lfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x62, 0x01, 0x07, 0x00, 0x02, 0x00, 0x00, 0x00], Offset: 8192, Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "4.4BSD log-structured file system (NetBSD LFS v2, 32-bit); read, create, and append-only edits.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Path, e.IsDirectory ? 0 : e.Size, e.IsDirectory ? 0 : e.Size,
      "Stored", e.IsDirectory, false, DateTime.UnixEpoch.AddSeconds(e.MTime),
      IsSymlink: e.IsSymlink, LinkTarget: e.IsSymlink ? System.Text.Encoding.UTF8.GetString(r.Extract(e)) : null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LfsReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) Directory.CreateDirectory(Path.Combine(outputDir, e.Path.Replace('/', Path.DirectorySeparatorChar)));
      else if (e.IsFile) WriteFile(outputDir, e.Path, r.Extract(e));
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var bytes = this.ExtractEntryToMemory(archive, entryName, password);
    return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new LfsReader(archive);
    var name = entryName.Replace('\\', '/').TrimStart('/');
    var e = r.Entries.FirstOrDefault(x => x.IsFile && x.Path == name)
      ?? throw new FileNotFoundException($"LFS volume has no file '{entryName}'.", entryName);
    return r.Extract(e);
  }

  /// <summary>
  /// Formats a volume as <c>newfs_lfs</c> would and writes <paramref name="inputs"/> into it.
  /// The size comes from the <c>ImageSize</c> option, or is chosen to leave room for edits.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory).Select(i => (Input: i, Data: i.ReadContent())).ToList();
    long requested = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    var need = files.Sum(f => f.Data.LongLength + 4096) + inputs.Count * 1024L;
    var auto = (need * 3 / 2 + (12L << 20) + (1 << 20) - 1) & ~((1L << 20) - 1);
    var size = requested > 0 ? requested : Math.Max(16L << 20, auto);
    var now = DateTimeOffset.UtcNow;
    var v = LfsVolume.Format(size, now: now);
    var e = LfsEditor.ForNew(v, now);
    foreach (var d in inputs.Where(i => i.IsDirectory)) e.AddDirectory(d.ArchiveName, d.Metadata?.LastWriteTimeUtc);
    foreach (var (input, data) in files) e.AddFile(input.ArchiveName, data, input.Metadata?.LastWriteTimeUtc, input.Metadata?.UnixMode);
    e.Commit();
    v.WriteAllSuperblocks();
    if (output.CanSeek) { output.Position = 0; output.SetLength(0); }
    output.Write(v.Image);
  }

  /// <summary>Adds or replaces files by appending them to the log; nothing written before moves.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Edit(archive, e => {
      foreach (var d in inputs.Where(i => i.IsDirectory)) e.AddDirectory(d.ArchiveName, d.Metadata?.LastWriteTimeUtc);
      foreach (var f in inputs.Where(i => !i.IsDirectory)) e.AddFile(f.ArchiveName, f.ReadContent(), f.Metadata?.LastWriteTimeUtc, f.Metadata?.UnixMode);
    });
  }

  /// <summary>Removes files, or directories with everything below them, and checkpoints.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    Edit(archive, e => {
      foreach (var name in entryNames) e.Remove(name);
    });
  }

  private static void Edit(Stream archive, Action<LfsEditor> change) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanSeek || !archive.CanWrite) throw new NotSupportedException("LFS edits need a seekable, writable stream.");
    if (archive.Length > int.MaxValue) throw new NotSupportedException("LFS volumes over 2 GiB are not edited.");
    var image = new byte[archive.Length];
    archive.Position = 0;
    archive.ReadExactly(image);
    var reader = new LfsReader(new MemoryStream(image, writable: false));
    var now = DateTimeOffset.UtcNow;
    var v = LfsVolume.Load(image, reader, now);
    var e = LfsEditor.ForExisting(v, reader, now);
    change(e);
    e.Commit();
    foreach (var (offset, length) in v.Written) {
      archive.Position = offset;
      archive.Write(image, (int)offset, length);
    }
    archive.Flush();
  }
}
