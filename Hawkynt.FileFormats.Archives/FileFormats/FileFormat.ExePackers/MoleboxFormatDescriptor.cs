#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// MoleBox 2.x: the files a MoleBox-packed executable carries in its appended file box, and
/// the copy of the program itself when the box keeps one. Layout in
/// <c>docs/MOLEBOX-ON-DISK.md</c>; the key comes from the loader chain the
/// <see cref="MoleboxExecutablePackerHandler"/> replays.
/// </summary>
public sealed class MoleboxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Molebox";
  public string DisplayName => "MoleBox";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".exe";
  // Detected by its box trailer (FormatDetector.DetectInstaller), never by ".exe".
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zlib", "zlib, IDEA-encrypted"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "MoleBox 2.x: the files boxed into a packed executable";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    MoleboxBox? box;
    string? error = null;
    try {
      box = MoleboxBox.TryOpen(ReadAll(stream));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException or EndOfStreamException) {
      box = null;
      error = ex.Message;
    }
    if (box == null) {
      var note = Encoding.UTF8.GetBytes($"format=molebox\nparse_status=partial\nerror={(error ?? "no MoleBox 2.x file box this reader knows").Replace('\n', ' ')}\n");
      return [new ArchiveEntryInfo(0, "metadata.ini", note.Length, note.Length, "stored", false, false, null, Kind: "metadata")];
    }
    return box.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, box.StoredLength(e), (e.Flags & 1) != 0 ? "zlib" : "stored", false, (e.Flags & 2) != 0, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var box = MoleboxBox.TryOpen(ReadAll(stream)) ?? throw new InvalidDataException("MoleBox: no file box this reader knows.");
    foreach (var e in box.Entries) {
      if (files != null && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, box.Extract(e));
    }
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek)
      stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
