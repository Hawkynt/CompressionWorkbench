#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Core.DiskImage;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hammer2;

/// <summary>
/// Read-only descriptor for HAMMER2 (DragonFly BSD newer) filesystem images.
/// Surfaces the volume-data sector at offset 0 plus a structured metadata
/// bundle and the raw image. Walking the HAMMER2 cluster B-tree (radix-tree
/// chains, blockrefs, indirect blocks) is explicitly out of scope (multi-week
/// effort).
///
/// Magic: 8-byte uint64 at offset 0 = <c>HAMMER2_VOLUME_ID_HBO</c>
/// (<c>0x48414d3205172011</c>) or <c>HAMMER2_VOLUME_ID_ABO</c>
/// (<c>0x11201705324d4148</c>). The descriptor's <see cref="MagicSignatures"/>
/// list covers the HBO form (LE serialisation: <c>11 20 17 05 32 4D 41 48</c>);
/// the ABO form is recognised by the parser but is rare in practice (only
/// arises when a HAMMER2 image is cross-mounted on opposite-endian hardware).
/// Confidence 0.85: an 8-byte magic at offset 0 is high-confidence but the
/// detector does no secondary sanity check (e.g. volume size plausibility,
/// fstype UUID match).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/DragonFlyBSD/DragonFlyBSD/blob/master/sys/vfs/hammer2/hammer2_disk.h</c></description></item>
///   <item><description><c>https://gitweb.dragonflybsd.org/dragonfly.git/blob/HEAD:/sys/vfs/hammer2/DESIGN</c></description></item>
/// </list>
/// </summary>
public sealed class Hammer2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IArchiveCreatable, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// Sole tunable the HAMMER2 writer honours: the PFS label
  /// (<c>newfs_hammer2 -L</c>) given to the populated PFS that holds the user
  /// files. Volume size is intentionally not exposed — the boot/aux/topology
  /// floor pins the minimum regardless. An empty label falls back to the writer
  /// default ("DATA").
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Label", DisplayName: "PFS label", Kind: FormatOptionKind.String, Default: "",
      Description: "Labelled PFS name (newfs_hammer2 -L); max 63 ASCII chars."),
  ];

  public string Id => "Hammer2";
  public string DisplayName => "HAMMER2 (DragonFly BSD)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify;
  public string DefaultExtension => ".hammer2";
  public IReadOnlyList<string> Extensions => [".hammer2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Hammer2VolumeData.MagicBytesHboLE, Offset: 0, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "HAMMER2 (DragonFly BSD newer) filesystem image — volume-data sector surface only. " +
    "WORM emit deferred: HAMMER2 requires four redundant 64 KB volume-data sectors at " +
    "offsets 0/65536/131072/196608 with consistent generation numbers, a copy-on-write " +
    "blockref radix tree with per-block xxHash64 checksums across every blockref, " +
    "per-superroot PFS clusters with their own sub-radix trees, and a real freemap " +
    "leaf+meta blockmap that survives the COW promotion rules. Multi-week effort, " +
    "deferred to a future phase.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.hammer2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    Hammer2VolumeData hdr;
    try {
      hdr = Hammer2VolumeData.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.hammer2", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    // Walk the blockref tree for the real files. The header parse above used a
    // bounded read; re-read the whole image for the walk only when the header is
    // valid (a deliberately-opened HAMMER2 archive, not speculative carving).
    var files = hdr.Valid ? ReadFiles(stream) : [];

    // A volume that carries files lists exactly those. Surfacing the synthetic
    // header entries alongside them would make every rebuild (shrink, defrag)
    // fold them back in as real files, so they stay on the carver path — empty
    // or foreign images, where the header IS all we can offer.
    var idx = 0;
    if (files.Count == 0) {
      entries.Add(new ArchiveEntryInfo(idx++, "FULL.hammer2", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
      if (hdr.Valid)
        entries.Add(new ArchiveEntryInfo(idx++, "volume_header.bin", hdr.HeaderRaw.LongLength, hdr.HeaderRaw.LongLength, "stored", false, false, null));
      return entries;
    }

    foreach (var file in files)
      entries.Add(new ArchiveEntryInfo(idx++, file.Path, file.Size, file.Size, "stored", false, false, null));

    return entries;
  }

  // Walks the HAMMER2 blockref tree and returns the real regular files it holds.
  // Never throws — returns nothing when the walk fails.
  private static List<Hammer2Reader.FileRef> ReadFiles(Stream stream) {
    try {
      if (stream.CanSeek)
        stream.Position = 0;
      using var reader = new Hammer2Reader(stream);
      return reader.EnumerateFiles();
    } catch {
      return [];
    }
  }

  /// <summary>
  /// Produces a fresh, mountable single-volume HAMMER2 image from
  /// <paramref name="inputs"/>. The output mirrors <c>newfs_hammer2</c>: a
  /// volume header, the super-root inode, and the "LOCAL" + labelled PFS inodes.
  /// The labelled PFS root is populated with the input files — each a regular-file
  /// inode plus a directory entry (see <see cref="Hammer2Writer"/>). The DragonFly
  /// kernel mounts the labelled PFS and reads every file's contents byte-exact
  /// (validated via <c>mount_hammer2 …@&lt;label&gt;</c> + <c>cksum</c>, including
  /// directories large enough to spill into a blockref-indirect block and files
  /// stored in an out-of-line data block).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var writer = new Hammer2Writer();
    var label = options?.GetOption("Label", "DATA");
    if (!string.IsNullOrEmpty(label))
      writer.Label = label;

    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      if (input.InMemoryContent is { } bytes) {
        writer.AddFile(input.ArchiveName, bytes);
        continue;
      }
      var path = input.FullPath;
      writer.AddStreamingFile(input.ArchiveName, new FileInfo(path).Length, () => File.OpenRead(path));
    }

    writer.WriteTo(output);
  }

  /// <summary>
  /// Genuine in-place (copy-on-write) add/replace of files in the labelled PFS
  /// root: new file inodes + data blocks and the rebuilt labelled-PFS →
  /// super-root → volume-header chain are appended past the topology high-water,
  /// leaving every existing file's data byte-identical at its original offset.
  /// Falls back to the verified rebuild path when the change can't be expressed
  /// as a single inline/one-indirect blockset (nested-indirect roots, nested
  /// paths). See <see cref="Hammer2InPlaceModifier"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) =>
    Hammer2InPlaceModifier.Add(archive, inputs,
      (a, i) => ModifyRebuilder.Add(a, i, ReadEntries, BuildImage, largeVolumeCreator: this));

  /// <summary>
  /// Genuine in-place (copy-on-write) removal of files from the labelled PFS
  /// root, rebuilding the chain above without disturbing surviving files' data.
  /// Falls back to the verified rebuild path when out of scope.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) =>
    Hammer2InPlaceModifier.Remove(archive, entryNames,
      (a, n) => ModifyRebuilder.Remove(a, n, ReadEntries, BuildImage, largeVolumeCreator: this));

  // Rebuild-fallback delegates: read every file the reader can surface, and
  // re-emit a fresh image from a file list via the writer.
  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream archive) {
    archive.Position = 0;
    using var reader = new Hammer2Reader(archive);
    foreach (var file in reader.EnumerateFiles()) {
      using var buffer = new MemoryStream();
      reader.ExtractTo(file, buffer);
      yield return (file.Path, buffer.ToArray());
    }
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var writer = new Hammer2Writer();
    foreach (var (name, data) in files)
      writer.AddFile(name, data);
    using var ms = new MemoryStream();
    writer.WriteTo(ms);
    return ms.ToArray();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ImageAccessor image;
    try {
      if (stream.CanSeek) stream.Position = 0;
      image = new ImageAccessor(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    Hammer2VolumeData hdr;
    using (image) {
      try {
        hdr = Hammer2VolumeData.TryParse(image.Read(0, (int)Math.Min(HeaderReadCap, image.Length)));
      } catch {
        WriteFullImage(image, outputDir, files);
        WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
        return;
      }

      if (!hdr.Valid) {
        WriteFullImage(image, outputDir, files);
        WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr), files);
        return;
      }
    }

    // Materialise the real files by walking the blockref tree. The header
    // surface is written only for a volume that holds none, mirroring List.
    var extracted = 0;
    try {
      if (stream.CanSeek) stream.Position = 0;
      using var reader = new Hammer2Reader(stream);
      foreach (var file in reader.EnumerateFiles()) {
        ++extracted;
        if (files is { Length: > 0 } && !MatchesFilter(file.Path, files)) continue;
        var target = Path.Combine(outputDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
        using var output = File.Create(target);
        reader.ExtractTo(file, output);
      }
    } catch {
      // Best-effort; the header surface below still gets written.
    }

    if (extracted > 0) return;

    if (stream.CanSeek) stream.Position = 0;
    using var surface = new ImageAccessor(stream);
    WriteFullImage(surface, outputDir, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr), files);
    WriteIfMatch(outputDir, "volume_header.bin", hdr.HeaderRaw, files);
  }

  private static void WriteFullImage(ImageAccessor image, string outputDir, string[]? files) {
    if (files is { Length: > 0 } && !MatchesFilter("FULL.hammer2", files)) return;
    Directory.CreateDirectory(outputDir);
    using var target = File.Create(Path.Combine(outputDir, "FULL.hammer2"));
    image.CopyTo(0, target, image.Length);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Hammer2VolumeData h) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={(h.Valid ? "ok" : "partial")}\n");
    b.Append(ic, $"magic=0x{h.Magic:X16}\n");
    if (h.Valid) {
      b.Append(ic, $"byte_swapped={h.ByteSwapped}\n");
      b.Append(ic, $"version={h.Version}\n");
      b.Append(ic, $"flags=0x{h.Flags:X8}\n");
      b.Append(ic, $"volu_size={h.VoluSize}\n");
      b.Append(ic, $"copyid={h.CopyId}\n");
      b.Append(ic, $"freemap_version={h.FreemapVersion}\n");
      b.Append(ic, $"peer_type={h.PeerType}\n");
      b.Append(ic, $"volu_id={h.VoluId}\n");
      b.Append(ic, $"nvolumes={h.NVolumes}\n");
      b.Append(ic, $"boot_beg=0x{h.BootBeg:X16}\n");
      b.Append(ic, $"boot_end=0x{h.BootEnd:X16}\n");
      b.Append(ic, $"aux_beg=0x{h.AuxBeg:X16}\n");
      b.Append(ic, $"aux_end=0x{h.AuxEnd:X16}\n");
      b.Append(ic, $"fs_uuid_hex={h.FsidHex}\n");
      b.Append(ic, $"fs_type_uuid_hex={h.FsTypeHex}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // Bounded read — must NOT pull multi-GB images into memory when the carver
  // runs us speculatively. The HAMMER2 sector #0 lives in the first 512 bytes;
  // 64 KB is exactly one VOLUME_BYTES sector and provides comfortable headroom.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

}
