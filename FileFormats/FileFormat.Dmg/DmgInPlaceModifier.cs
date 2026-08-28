#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dmg;

/// <summary>
/// Mutates the raw UDIF profile emitted by <see cref="DmgWriter"/> without
/// rebuilding existing partition payloads. Existing data-fork bytes stay at
/// their physical offsets; replacements/new partitions are appended where the
/// old plist started and the trailing blkx index + koly footer are regenerated.
/// Removed/replaced payloads become unreachable data-fork slack.
/// </summary>
internal static class DmgInPlaceModifier {

  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Mutate(archive, inputs, []);
  }

  public static void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    Mutate(archive, [], entryNames);
  }

  private static void Mutate(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs,
      IReadOnlyCollection<string> removals) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanSeek || !archive.CanRead || !archive.CanWrite)
      throw new ArgumentException("DMG mutation requires a seekable read/write stream.", nameof(archive));

    archive.Position = 0;
    using var reader = new DmgReader(archive, leaveOpen: true);
    if (!reader.IsWorkbenchRawProfile)
      throw new NotSupportedException(
        "DMG mutation is supported for the raw UDIF profile emitted by CompressionWorkbench; " +
        "foreign compressed/signed plist profiles remain read-only.");
    if ((reader.XmlOffset % DmgWriter.SectorSize) != 0)
      throw new InvalidDataException("DMG data fork is not sector-aligned.");

    var entries = reader.Partitions
      .Select(p => (Name: p.Name, Mish: p.Mish, LogicalSize: p.LogicalSize))
      .ToList();

    foreach (var name in removals)
      entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    archive.Position = reader.XmlOffset;
    foreach (var (name, data) in FilesOnly(inputs)) {
      entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

      var offset = archive.Position;
      var paddedLength = DmgWriter.AlignSector(data.Length);
      archive.Write(data);
      WriteZeros(archive, paddedLength - data.Length);

      var sectorCount = checked((ulong)(paddedLength / DmgWriter.SectorSize));
      var mish = DmgWriter.BuildMishBlob(0, sectorCount, checked((ulong)offset), checked((ulong)paddedLength));
      entries.Add((name, mish, data.LongLength));
    }

    var xmlOffset = archive.Position;
    var xmlBytes = Encoding.UTF8.GetBytes(DmgWriter.BuildXmlPlist(entries));
    archive.Write(xmlBytes);

    // The workbench raw profile keeps the data fork at offset zero. Its old
    // payload bytes (including orphaned removed/replaced partitions) remain part
    // of that fork; this is what makes removal metadata-only and preserves every
    // untouched physical partition offset.
    var dataForkLength = xmlOffset;
    var sectors = checked((ulong)(dataForkLength / DmgWriter.SectorSize));
    archive.Write(DmgWriter.BuildKoly(xmlOffset, xmlBytes.LongLength, dataForkLength,
      sectors, reader.KolyTrailer));
    archive.SetLength(archive.Position);
  }

  private static void WriteZeros(Stream output, int count) {
    if (count <= 0) return;
    Span<byte> zero = stackalloc byte[512];
    while (count > 0) {
      var take = Math.Min(count, zero.Length);
      output.Write(zero[..take]);
      count -= take;
    }
  }
}
