#pragma warning disable CS1591
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Reiser4;

/// <summary>
/// Creator for a Reiser4 filesystem image that starts from what
/// <c>mkfs.reiser4 -fffy</c> from <c>reiser4progs 1.2.2</c> produces and adds
/// directories, regular files, symbolic links, device nodes, FIFOs, sockets and hard links
/// through the native tree — stat data, directory entries and tails in leaves, extents in twigs,
/// as many internal levels above them as the tree needs — so that <c>fsck.reiser4</c> validates
/// the result as <c>"FS is consistent."</c> and a Reiser4 kernel mounts and reads it.
///
/// <para>
/// The image holds 25 reserved blocks at fixed positions (block size = 4 KB):
/// <list type="bullet">
///   <item><description>0..15 — jump-area / partition data (zero).</description></item>
///   <item><description>16 — master superblock (<c>"ReIsEr4"</c> at offset 65536).</description></item>
///   <item><description>17 — format40 superblock (<c>"ReIsEr40FoRmAt"</c> at offset 52).</description></item>
///   <item><description>18 — block-allocator bitmap (4-byte adler32 + bit array).</description></item>
///   <item><description>19..20 — journal header / footer (zero for an unjournalled volume).</description></item>
///   <item><description>21 — status block (<c>"ReiSeR4StATusBl"</c>).</description></item>
///   <item><description>22 — superblock backup record.</description></item>
///   <item><description>23 — storage-tree root (twig, level 2, or higher).</description></item>
///   <item><description>24 — first leaf (the root directory's stat data and entries).</description></item>
/// </list>
/// </para>
///
/// <para>
/// The fixed blocks are byte-exact captures of a real <c>mkfs.reiser4</c> image with only the
/// per-image fields patched in: UUID, label, mkfs id, block and free-block counts, the root, the
/// next object id, the file count and the tree height. Regular files are written as extents by
/// default; <see cref="FileBody" /> selects tails, the kernel's smart policy or compressed
/// (cryptcompress) files instead.
/// </para>
/// </summary>
public sealed class Reiser4Writer {
  /// <summary>Filesystem block size — 4 KB is the only supported value. Reiser4
  /// theoretically supports 512 / 1024 / 2048 / 4096 / 8192, but the embedded
  /// templates are 4096-byte exact captures.</summary>
  public const int BlockSize = 4096;

  /// <summary>Minimum block count we'll emit. mkfs.reiser4 itself rejects
  /// images below ~750 blocks, but we round up to 4096 (= 16 MB) to match the
  /// reference capture and avoid bitmap-truncation edge cases.</summary>
  public const ulong MinBlockCount = 4096;

  private const ulong ReservedBlockCount = 25;

  private const int MasterUuidOff = 20;
  private const int MasterLabelOff = 36;
  private const int BackupUuidOff = 0x15;
  private const int BackupLabelOff = 0x25;

  /// <summary>Customisable label written to the master SB (NUL-padded to 16
  /// bytes; longer strings are truncated; null = empty/zero).</summary>
  public string? Label { get; set; }

  /// <summary>Optional 16-byte UUID. When null, a random Guid is used.</summary>
  public byte[]? Uuid { get; set; }

  /// <summary>Optional 32-bit mkfs identifier. When null, a random value is
  /// drawn from <see cref="Random.Shared"/>.</summary>
  public uint? MkfsId { get; set; }

  /// <summary>Total filesystem size in 4 KB blocks. Clamped to
  /// <see cref="MinBlockCount"/>.</summary>
  public ulong BlockCount { get; set; } = MinBlockCount;

  /// <summary>How regular files are stored. <see cref="Reiser4FileBody.Profile" /> follows the
  /// captured mkfs profile, whose root names cryptcompress for new files.</summary>
  public Reiser4FileBody FileBody { get; set; } = Reiser4FileBody.Extents;

  /// <summary>Marker written at <see cref="MasterPayloadMarkerOff" /> of the master superblock by the earliest workbench writer.</summary>
  /// <remarks>The value spells nothing: a marker that reads as words names whoever chose them.</remarks>
  internal static readonly byte[] PayloadMarker =
    [0x8D, 0x17, 0x0C, 0xE1, 0x93, 0x1A, 0x0F, 0xB6, 0x81];

  /// <summary>Offset in the master superblock of the marker, past uuid and label.</summary>
  internal const int MasterPayloadMarkerOff = 52;

  /// <summary>Offset in the master superblock of the first directory block number (d64).</summary>
  internal const int MasterPayloadDirOff = MasterPayloadMarkerOff + 12;

  /// <summary>Magic at the head of every legacy payload directory block.</summary>
  internal static readonly byte[] DirMagic =
    [0x9E, 0x0B, 0x14, 0xA7, 0x02, 0xDB, 0x18, 0x8F];

  internal const int DirHeadSize = 8 + 8 + 4;
  internal const int DirNameLength = 224;
  internal const int DirEntrySize = DirNameLength + 8 + 8;
  internal const int DirEntriesPerBlock = (BlockSize - DirHeadSize) / DirEntrySize;

  /// <summary>
  /// Blocks one bitmap block accounts for. The block's first four bytes hold the
  /// adler32 of the rest, so the bit array is four bytes short of the block.
  /// </summary>
  internal const ulong BlocksPerBitmap = (ulong)(BlockSize - 4) * 8;

  private readonly List<Action<Reiser4Plan>> _edits = [];
  private readonly HashSet<string> _names = new(StringComparer.Ordinal);

  /// <summary>Adds a regular file.</summary>
  public void AddFile(string name, byte[] data, Reiser4Reader.FileMetadata? metadata = null) {
    ArgumentNullException.ThrowIfNull(data);
    this.Claim(name);
    this._edits.Add(plan => plan.AddFile(name, FilePayload.FromBytes(data), metadata));
  }

  /// <summary>Adds a file whose bytes are pulled from <paramref name="openStream" /> as the image is written.</summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream, Reiser4Reader.FileMetadata? metadata = null) {
    ArgumentNullException.ThrowIfNull(openStream);
    this.Claim(name);
    this._edits.Add(plan => plan.AddFile(name, FilePayload.FromStream(size, openStream), metadata));
  }

  /// <summary>Retains an explicit directory, including an empty directory.</summary>
  public void AddDirectory(string name, Reiser4Reader.FileMetadata? metadata = null) {
    var normalized = name.Replace('\\', '/').TrimEnd('/');
    if (normalized.Length > 0 && RejectName(normalized) is { } reason)
      throw new NotSupportedException("Reiser4: " + reason + ".");
    this._edits.Add(plan => plan.AddDirectory(normalized, metadata));
  }

  /// <summary>Adds a symbolic link whose target is <paramref name="target" />.</summary>
  public void AddSymlink(string name, string target, Reiser4Reader.FileMetadata? metadata = null) {
    ArgumentNullException.ThrowIfNull(target);
    this.Claim(name);
    this._edits.Add(plan => plan.AddSymlink(name, target, metadata));
  }

  /// <summary>Adds a device node, FIFO or socket; <paramref name="mode" /> carries the file type bits.</summary>
  public void AddSpecialFile(string name, ushort mode, ulong device = 0, Reiser4Reader.FileMetadata? metadata = null) {
    this.Claim(name);
    this._edits.Add(plan => plan.AddSpecial(name, mode, device, metadata));
  }

  /// <summary>Adds <paramref name="name" /> as a further name of the file at <paramref name="existing" />.</summary>
  public void AddHardLink(string name, string existing) {
    ArgumentNullException.ThrowIfNull(existing);
    this.Claim(name);
    this._edits.Add(plan => plan.AddHardLink(name, existing));
  }

  private void Claim(string name) {
    if (RejectName(name) is { } reason)
      throw new NotSupportedException("Reiser4: " + reason + ".");
    if (!this._names.Add(name.Replace('\\', '/')))
      throw new ArgumentException($"Reiser4: '{name}' is already in the root directory.", nameof(name));
  }

  /// <summary>Validates the path components supported by the native namespace writer.</summary>
  internal static string? RejectName(string? name) {
    if (string.IsNullOrEmpty(name)) return "an entry needs a name";
    var components = name.Replace('\\', '/').Split('/');
    if (components.Length > 129) return "this profile supports at most 129 path components";
    if (components.Any(static component => component.Length == 0 || component is "." or ".."))
      return "path components must be non-empty and cannot be dot entries";
    if (name.Contains('\0') || name.Contains(':') || name.Any(static c => c > 127))
      return "this profile requires ASCII names without NUL or colon";
    if (components.Any(static component => component.Length > 255)) return "a name is longer than 255 bytes";
    return null;
  }

  /// <summary>
  /// Smallest block count that holds <paramref name="fileSizes" />: the reserved blocks, every
  /// file's data blocks, the tree, and the bitmap blocks interleaved among them.
  /// </summary>
  public static ulong EstimateBlockCount(IEnumerable<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    var sizes = fileSizes as IReadOnlyCollection<long> ?? [.. fileSizes];
    var blocks = ReservedBlockCount;
    blocks += (ulong)((sizes.Count + DirEntriesPerBlock - 1) / Math.Max(1, DirEntriesPerBlock));
    foreach (var size in sizes)
      blocks += (ulong)((size + BlockSize - 1) / BlockSize);
    blocks += blocks / BlocksPerBitmap + 2;
    return Math.Max(MinBlockCount, blocks + blocks / 20);
  }

  /// <summary>Builds the image fully in memory and returns the byte array.</summary>
  public byte[] Build() {
    using var ms = new MemoryStream();
    this.Write(ms);
    return ms.ToArray();
  }

  /// <summary>Streams the full image into <paramref name="output"/> and leaves the stream positioned at the end.</summary>
  public void Write(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      using var staged = Compression.Registry.RebuildVerb.CreateScratchStream();
      this.Write(staged);
      staged.Position = 0;
      staged.CopyTo(output);
      output.Flush();
      return;
    }
    var plan = this.CreatePlan();
    foreach (var edit in this._edits) edit(plan);
    plan.Write(output);
  }

  /// <summary>The empty volume: the captured mkfs blocks with this writer's identity patched in.</summary>
  internal Reiser4Plan CreatePlan() {
    var uuid = this.Uuid ?? Guid.NewGuid().ToByteArray(bigEndian: true);
    if (uuid.Length != 16)
      throw new ArgumentException("UUID must be exactly 16 bytes.", nameof(this.Uuid));
    var mkfsId = this.MkfsId ?? unchecked((uint)Random.Shared.Next(int.MinValue, int.MaxValue));
    var label = TrimLabel(this.Label);

    var master = LoadTemplate(16);
    uuid.AsSpan().CopyTo(master.AsSpan(MasterUuidOff, 16));
    Array.Clear(master, MasterLabelOff, 16);
    label.AsSpan().CopyTo(master.AsSpan(MasterLabelOff, 16));

    var backup = LoadTemplate(22);
    uuid.AsSpan().CopyTo(backup.AsSpan(BackupUuidOff, 16));
    Array.Clear(backup, BackupLabelOff, 16);
    label.AsSpan().CopyTo(backup.AsSpan(BackupLabelOff, 16));

    var plan = Reiser4Plan.Fresh(master, LoadTemplate(17), LoadTemplate(21), backup, LoadTemplate(24), mkfsId,
      Math.Max(this.BlockCount, MinBlockCount));
    plan.FileBody = this.FileBody;
    return plan;
  }

  private static byte[] TrimLabel(string? label) {
    if (string.IsNullOrEmpty(label)) return [];
    var bytes = Encoding.ASCII.GetBytes(label);
    return bytes.Length <= 16 ? bytes : bytes[..16];
  }

  internal static byte[] LoadTemplate(int blockNumber) {
    var resource = $"FileSystem.Reiser4.Templates.block_{blockNumber}.bin";
    var asm = typeof(Reiser4Writer).Assembly;
    using var stream = asm.GetManifestResourceStream(resource)
      ?? throw new InvalidOperationException(
        $"Embedded template '{resource}' not found. Ensure FileSystem.Reiser4.csproj " +
        $"includes the Templates\\block_{blockNumber}.bin EmbeddedResource.");
    var buf = new byte[BlockSize];
    var off = 0;
    while (off < BlockSize) {
      var got = stream.Read(buf, off, BlockSize - off);
      if (got <= 0) break;
      off += got;
    }
    if (off != BlockSize)
      throw new InvalidOperationException(
        $"Embedded template '{resource}' is {off} bytes, expected {BlockSize}.");
    return buf;
  }
}
