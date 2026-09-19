#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileSystem.BeeGfs;

internal sealed record BeeGfsTargetMember(
  string SourceName,
  FilesystemSourceRole Role,
  string BackingFormatId,
  string RootPath,
  uint NumericId,
  int FormatVersion
);

internal sealed record BeeGfsTargetTopology(
  IReadOnlyList<BeeGfsTargetMember> MetadataTargets,
  IReadOnlyList<BeeGfsTargetMember> StorageTargets
);

internal static class BeeGfsMultiStreamTopology {
  private const int MaxControlFileBytes = 64 * 1024;
  private const int MetadataFormatMinVersion = 3;
  private const int MetadataFormatMaxVersion = 4;
  private const int StorageFormatMinVersion = 2;
  private const int StorageFormatMaxVersion = 3;

  public static BeeGfsTargetTopology Inspect(FilesystemStreamSet sources) {
    ArgumentNullException.ThrowIfNull(sources);

    var metadata = new List<BeeGfsTargetMember>();
    var storage = new List<BeeGfsTargetMember>();

    foreach (var source in sources) {
      if (!source.Stream.CanRead || !source.Stream.CanSeek)
        throw new ArgumentException(
          $"BeeGFS source '{source.Name}' must be readable and seekable.", nameof(sources));
      if (string.IsNullOrWhiteSpace(source.FormatId))
        throw new ArgumentException(
          $"BeeGFS source '{source.Name}' must name its backing filesystem FormatId.", nameof(sources));
      if (string.Equals(source.FormatId, "BeeGfs", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException(
          $"BeeGFS source '{source.Name}' cannot recursively use BeeGfs as its backing FormatId.", nameof(sources));

      var original = source.Stream.Position;
      try {
        source.Stream.Position = 0;
        var backingProfile = FormatRegistry.ProbeFilesystem(source.FormatId, source.Stream);
        if (!backingProfile.CanMount)
          throw new InvalidDataException(
            $"BeeGFS source '{source.Name}' backing format '{source.FormatId}' is not mountable: " +
            string.Join("; ", backingProfile.Limitations));

        source.Stream.Position = 0;
        using var session = FormatRegistry.OpenFilesystem(
          source.FormatId,
          source.Stream,
          new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
        var root = ResolveDirectory(session, source.RootPath, source.Name);

        var hasMetadataSignature =
          IsKind(session, root, "nodeNumID", FilesystemNodeKind.RegularFile) &&
          IsKind(session, root, "dentries", FilesystemNodeKind.Directory) &&
          IsKind(session, root, "inodes", FilesystemNodeKind.Directory);
        var hasStorageSignature =
          IsKind(session, root, "targetNumID", FilesystemNodeKind.RegularFile) &&
          IsKind(session, root, "chunks", FilesystemNodeKind.Directory);

        var role = ResolveRole(source, hasMetadataSignature, hasStorageSignature);
        if (role == FilesystemSourceRole.Auxiliary)
          continue;

        var formatVersion = ParseFormatVersion(ReadSmallText(session, root, "format.conf", source.Name), source.Name);
        switch (role) {
          case FilesystemSourceRole.Metadata: {
            if (formatVersion is < MetadataFormatMinVersion or > MetadataFormatMaxVersion)
              throw new NotSupportedException(
                $"BeeGFS metadata source '{source.Name}' uses storage format version {formatVersion}; " +
                $"supported versions are {MetadataFormatMinVersion}..{MetadataFormatMaxVersion}.");
            var id = ParsePositiveUInt32(ReadSmallText(session, root, "nodeNumID", source.Name), "nodeNumID", source.Name);
            metadata.Add(new BeeGfsTargetMember(
              source.Name, role, source.FormatId, NormalizeRootPath(source.RootPath), id, formatVersion));
            break;
          }
          case FilesystemSourceRole.Data: {
            if (formatVersion is < StorageFormatMinVersion or > StorageFormatMaxVersion)
              throw new NotSupportedException(
                $"BeeGFS storage source '{source.Name}' uses storage format version {formatVersion}; " +
                $"supported versions are {StorageFormatMinVersion}..{StorageFormatMaxVersion}.");
            var id = ParsePositiveUInt32(ReadSmallText(session, root, "targetNumID", source.Name), "targetNumID", source.Name);
            if (id > ushort.MaxValue)
              throw new InvalidDataException(
                $"BeeGFS source '{source.Name}' targetNumID {id} exceeds the 16-bit target-id range.");
            storage.Add(new BeeGfsTargetMember(
              source.Name, role, source.FormatId, NormalizeRootPath(source.RootPath), id, formatVersion));
            break;
          }
          default:
            throw new InvalidDataException($"BeeGFS source '{source.Name}' resolved to unsupported role {role}.");
        }
      } finally {
        source.Stream.Position = original;
      }
    }

    if (metadata.Count == 0)
      throw new InvalidDataException("BeeGFS target set contains no metadata target source.");
    if (storage.Count == 0)
      throw new InvalidDataException("BeeGFS target set contains no storage target source.");

    EnsureUniqueIds(metadata, "metadata nodeNumID");
    EnsureUniqueIds(storage, "storage targetNumID");
    return new BeeGfsTargetTopology(metadata, storage);
  }

  private static FilesystemSourceRole ResolveRole(
      FilesystemStreamSource source,
      bool hasMetadataSignature,
      bool hasStorageSignature) {
    return source.Role switch {
      FilesystemSourceRole.Metadata when hasMetadataSignature => FilesystemSourceRole.Metadata,
      FilesystemSourceRole.Metadata => throw new InvalidDataException(
        $"BeeGFS source '{source.Name}' is labelled Metadata but lacks nodeNumID, dentries, or inodes."),
      FilesystemSourceRole.Data when hasStorageSignature => FilesystemSourceRole.Data,
      FilesystemSourceRole.Data => throw new InvalidDataException(
        $"BeeGFS source '{source.Name}' is labelled Data but lacks targetNumID or chunks."),
      FilesystemSourceRole.Auxiliary => FilesystemSourceRole.Auxiliary,
      FilesystemSourceRole.Journal => throw new NotSupportedException(
        $"BeeGFS source '{source.Name}' uses the generic Journal role, which BeeGFS does not consume as an independent target."),
      FilesystemSourceRole.Unknown when hasMetadataSignature && !hasStorageSignature => FilesystemSourceRole.Metadata,
      FilesystemSourceRole.Unknown when hasStorageSignature && !hasMetadataSignature => FilesystemSourceRole.Data,
      FilesystemSourceRole.Unknown when hasMetadataSignature && hasStorageSignature => throw new InvalidDataException(
        $"BeeGFS source '{source.Name}' matches both metadata and storage target layouts; specify its role explicitly."),
      _ => throw new InvalidDataException(
        $"BeeGFS source '{source.Name}' does not contain a recognizable metadata or storage target root."),
    };
  }

  private static FilesystemNodeId ResolveDirectory(
      IFilesystemSession session,
      string rootPath,
      string sourceName) {
    var current = session.RootNodeId;
    foreach (var part in NormalizeRootPath(rootPath).Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      if (part is "." or "..")
        throw new ArgumentException(
          $"BeeGFS source '{sourceName}' RootPath must not contain '.' or '..' segments.", nameof(rootPath));
      current = session.Lookup(current, part)
        ?? throw new DirectoryNotFoundException(
          $"BeeGFS source '{sourceName}' RootPath '{rootPath}' does not exist in its backing filesystem.");
      if (session.Stat(current).Kind != FilesystemNodeKind.Directory)
        throw new DirectoryNotFoundException(
          $"BeeGFS source '{sourceName}' RootPath '{rootPath}' crosses non-directory component '{part}'.");
    }
    return current;
  }

  private static bool IsKind(
      IFilesystemSession session,
      FilesystemNodeId parent,
      string name,
      FilesystemNodeKind kind) {
    var node = session.Lookup(parent, name);
    return node is { } id && session.Stat(id).Kind == kind;
  }

  private static string ReadSmallText(
      IFilesystemSession session,
      FilesystemNodeId parent,
      string name,
      string sourceName) {
    var node = session.Lookup(parent, name)
      ?? throw new FileNotFoundException($"BeeGFS source '{sourceName}' is missing required file '{name}'.");
    var stat = session.Stat(node);
    if (stat.Kind != FilesystemNodeKind.RegularFile)
      throw new InvalidDataException($"BeeGFS source '{sourceName}' entry '{name}' is not a regular file.");
    if (stat.Size is < 0 or > MaxControlFileBytes)
      throw new InvalidDataException(
        $"BeeGFS source '{sourceName}' control file '{name}' has implausible size {stat.Size} bytes.");

    using var handle = session.OpenFile(node, FileAccess.Read);
    if (handle.Length > MaxControlFileBytes)
      throw new InvalidDataException(
        $"BeeGFS source '{sourceName}' control file '{name}' exceeds {MaxControlFileBytes} bytes.");
    var data = new byte[checked((int)handle.Length)];
    var offset = 0;
    while (offset < data.Length) {
      var read = handle.Read(offset, data.AsSpan(offset));
      if (read <= 0)
        throw new EndOfStreamException(
          $"BeeGFS source '{sourceName}' control file '{name}' ended after {offset} of {data.Length} bytes.");
      offset += read;
    }
    return Encoding.UTF8.GetString(data).Trim();
  }

  private static int ParseFormatVersion(string text, string sourceName) {
    foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
      var line = rawLine.Trim();
      if (line.Length == 0 || line[0] == '#') continue;
      var equals = line.IndexOf('=');
      if (equals <= 0) continue;
      if (!line[..equals].Trim().Equals("version", StringComparison.Ordinal)) continue;
      if (int.TryParse(line[(equals + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
          && version > 0)
        return version;
      throw new InvalidDataException($"BeeGFS source '{sourceName}' format.conf has an invalid version value.");
    }
    throw new InvalidDataException($"BeeGFS source '{sourceName}' format.conf contains no version key.");
  }

  private static uint ParsePositiveUInt32(string text, string field, string sourceName) {
    if (uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value != 0)
      return value;
    throw new InvalidDataException(
      $"BeeGFS source '{sourceName}' {field} must contain a positive decimal integer.");
  }

  private static string NormalizeRootPath(string path) {
    if (string.IsNullOrWhiteSpace(path) || path == "/") return "/";
    return "/" + path.Replace('\\', '/').Trim('/');
  }

  private static void EnsureUniqueIds(IReadOnlyList<BeeGfsTargetMember> members, string label) {
    var duplicate = members.GroupBy(member => member.NumericId).FirstOrDefault(group => group.Count() > 1);
    if (duplicate != null)
      throw new InvalidDataException(
        $"BeeGFS target set contains duplicate {label} {duplicate.Key}: " +
        string.Join(", ", duplicate.Select(member => member.SourceName)) + ".");
  }
}
