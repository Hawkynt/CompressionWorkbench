#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dtb;

/// <summary>
/// Transactional DTB editor and packer. Rewrites the FDT blocks tightly while preserving
/// the complete node tree, reservations, boot CPU id, and bytes outside header totalsize.
/// </summary>
internal static class DtbModifier {
  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Mutate(archive, (nodes, properties) => {
      foreach (var input in inputs) {
        if (input.IsDirectory) {
          AddNode(nodes, DtbWriter.FromArchiveDirectory(input.ArchiveName));
          continue;
        }

        var name = input.ArchiveName;
        if (string.Equals(Path.GetFileName(name), "metadata.ini", StringComparison.OrdinalIgnoreCase))
          continue;
        var incoming = DtbWriter.FromArchiveEntry(name, input.ReadContent());
        properties.RemoveAll(property => SameProperty(property, incoming));
        properties.Add(incoming);
        AddNode(nodes, incoming.NodePath);
      }
    });
  }

  public static void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    Mutate(archive, (nodes, properties) => {
      foreach (var name in entryNames) {
        if (string.Equals(Path.GetFileName(name.TrimEnd('/', '\\')), "metadata.ini", StringComparison.OrdinalIgnoreCase))
          continue;

        if (name.EndsWith('/') || name.EndsWith('\\')) {
          var nodePath = DtbWriter.FromArchiveDirectory(name).TrimEnd('/');
          if (nodePath.Length == 0) nodePath = "/";
          if (nodePath == "/") continue;
          var prefix = nodePath + "/";
          nodes.RemoveAll(path => string.Equals(path.TrimEnd('/'), nodePath, StringComparison.Ordinal)
            || path.StartsWith(prefix, StringComparison.Ordinal));
          properties.RemoveAll(property => string.Equals(property.NodePath.TrimEnd('/'), nodePath, StringComparison.Ordinal)
            || property.NodePath.StartsWith(prefix, StringComparison.Ordinal));
          continue;
        }

        var target = DtbWriter.FromArchiveEntry(name, []);
        properties.RemoveAll(property => SameProperty(property, target));
      }
    });
  }

  public static void Pack(Stream archive)
    => Mutate(archive, static (_, _) => { });

  public static void Pack(Stream input, Stream output) {
    var source = Read(input);
    Write(output, source.Fdt, source.Fdt.Nodes.Select(node => node.Path), source.Fdt.Properties.Select(ToSpec), source.Suffix);
  }

  public static void Purge(Stream archive) {
    var source = Read(archive);
    using var rebuilt = new MemoryStream();
    Write(rebuilt, source.Fdt, ["/"], [], source.Suffix);
    Commit(archive, rebuilt);
  }

  private static void Mutate(Stream archive, Action<List<string>, List<DtbWriter.PropertySpec>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("DTB mutation requires a seekable read/write stream.", nameof(archive));

    var source = Read(archive);
    var nodes = source.Fdt.Nodes.Select(node => node.Path).ToList();
    if (!nodes.Contains("/", StringComparer.Ordinal)) nodes.Insert(0, "/");
    var properties = source.Fdt.Properties.Select(ToSpec).ToList();
    edit(nodes, properties);

    using var rebuilt = new MemoryStream();
    Write(rebuilt, source.Fdt, nodes, properties, source.Suffix);
    Commit(archive, rebuilt);
  }

  private static Source Read(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("DTB reading requires a readable, seekable stream.", nameof(input));
    if (input.Length > int.MaxValue)
      throw new NotSupportedException("DTB images larger than 2 GiB are not supported.");

    input.Position = 0;
    var bytes = new byte[checked((int)input.Length)];
    input.ReadExactly(bytes);
    var fdt = DtbReader.Read(bytes);
    var suffixOffset = checked((int)fdt.Header.TotalSize);
    var suffix = bytes.AsSpan(suffixOffset).ToArray();
    return new Source(fdt, suffix);
  }

  private static void Write(Stream output, DtbReader.Fdt fdt, IEnumerable<string> nodes,
      IEnumerable<DtbWriter.PropertySpec> properties, ReadOnlySpan<byte> suffix) {
    using var packed = new MemoryStream();
    DtbWriter.Write(packed, properties.ToList(), nodes.Distinct(StringComparer.Ordinal).ToList(),
      fdt.Reservations, fdt.Header.BootCpuidPhys);
    if (!suffix.IsEmpty) packed.Write(suffix);

    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("DTB output must be writable and seekable.", nameof(output));
    output.Position = 0;
    output.SetLength(0);
    packed.Position = 0;
    packed.CopyTo(output);
    output.Flush();
    output.Position = 0;
  }

  private static void Commit(Stream archive, MemoryStream rebuilt) {
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
    archive.Position = 0;
  }

  private static void AddNode(List<string> nodes, string nodePath) {
    var normalized = nodePath.Length == 0 ? "/" : nodePath;
    if (nodes.Contains(normalized, StringComparer.Ordinal)) return;
    nodes.Add(normalized);
  }

  private static DtbWriter.PropertySpec ToSpec(DtbReader.Property property)
    => new(property.NodePath, property.Name, property.Data);

  private static bool SameProperty(DtbWriter.PropertySpec a, DtbWriter.PropertySpec b)
    => string.Equals(a.NodePath.TrimEnd('/'), b.NodePath.TrimEnd('/'), StringComparison.Ordinal)
      && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

  private sealed record Source(DtbReader.Fdt Fdt, byte[] Suffix);
}
