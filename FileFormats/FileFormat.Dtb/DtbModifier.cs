#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dtb;

/// <summary>Rewrites mutable DTB structure/string blocks while preserving reservations and boot CPU metadata.</summary>
internal static class DtbModifier {
  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Mutate(archive, properties => {
      foreach (var (name, data) in FilesOnly(inputs)) {
        if (string.Equals(Path.GetFileName(name), "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
        var incoming = DtbWriter.FromArchiveEntry(name, data);
        properties.RemoveAll(p => SameProperty(p, incoming));
        properties.Add(incoming);
      }
    });
  }

  public static void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    Mutate(archive, properties => {
      foreach (var name in entryNames) {
        if (string.Equals(Path.GetFileName(name), "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
        var target = DtbWriter.FromArchiveEntry(name, []);
        properties.RemoveAll(p => SameProperty(p, target));
      }
    });
  }

  private static void Mutate(Stream archive, Action<List<DtbWriter.PropertySpec>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("DTB mutation requires a seekable read/write stream.", nameof(archive));
    if (archive.Length > int.MaxValue)
      throw new NotSupportedException("DTB images larger than 2 GiB are not supported.");

    archive.Position = 0;
    var bytes = new byte[checked((int)archive.Length)];
    archive.ReadExactly(bytes);
    var fdt = DtbReader.Read(bytes);
    var properties = fdt.Properties
      .Select(p => new DtbWriter.PropertySpec(p.NodePath, p.Name, p.Data))
      .ToList();
    edit(properties);

    using var rebuilt = new MemoryStream();
    DtbWriter.Write(rebuilt, properties, fdt.Reservations, fdt.Header.BootCpuidPhys);
    archive.Position = 0;
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.SetLength(archive.Position);
  }

  private static bool SameProperty(DtbWriter.PropertySpec a, DtbWriter.PropertySpec b) =>
    string.Equals(a.NodePath.TrimEnd('/'), b.NodePath.TrimEnd('/'), StringComparison.Ordinal) &&
    string.Equals(a.Name, b.Name, StringComparison.Ordinal);
}
