#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dmg;

/// <summary>
/// Mutates the raw UDIF profile emitted by <see cref="DmgWriter"/> without
/// rebuilding existing partition payloads. Readable foreign UDIF profiles use
/// a verified extract/re-create fallback, normalising the edited image to the
/// writer's raw profile instead of attempting to patch compressed block tables,
/// checksums, signatures, or private plist resources in place.
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
    if (!reader.IsWorkbenchRawProfile) {
      RebuildForeignProfile(archive, reader, inputs, removals);
      return;
    }

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

  private static void RebuildForeignProfile(Stream archive, DmgReader reader,
      IReadOnlyList<ArchiveInputInfo> inputs, IReadOnlyCollection<string> removals) {
    var removed = new HashSet<string>(removals, StringComparer.OrdinalIgnoreCase);
    var replacements = FilesOnly(inputs).ToArray();
    var replacementNames = replacements
      .Select(static entry => entry.Name)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var live = new List<(string Name, byte[] Data)>();
    foreach (var entry in reader.Entries) {
      if (removed.Contains(entry.Name) || replacementNames.Contains(entry.Name)) continue;
      live.Add((entry.Name, reader.Extract(entry)));
    }
    live.AddRange(replacements);

    using var staged = new MemoryStream();
    var writer = new DmgWriter();
    foreach (var (name, data) in live)
      writer.AddPartition(name, data);
    writer.WriteTo(staged);

    // Verify both the logical entry set and every payload before touching the
    // original stream. This deliberately makes foreign-profile editing slower
    // but prevents a codec/read bug from becoming a destructive rewrite.
    staged.Position = 0;
    using (var verification = new DmgReader(staged, leaveOpen: true)) {
      if (verification.Entries.Count != live.Count)
        throw new InvalidOperationException("DMG rebuild verification changed the partition count.");

      for (var i = 0; i < live.Count; ++i) {
        var actualEntry = verification.Entries[i];
        var expected = live[i];
        if (!string.Equals(actualEntry.Name, expected.Name, StringComparison.OrdinalIgnoreCase) ||
            !verification.Extract(actualEntry).AsSpan().SequenceEqual(expected.Data))
          throw new InvalidOperationException($"DMG rebuild verification failed for partition '{expected.Name}'.");
      }
    }

    archive.Position = 0;
    archive.SetLength(0);
    staged.Position = 0;
    staged.CopyTo(archive);
    archive.Flush();
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
