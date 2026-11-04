#pragma warning disable CS1591
using System.Text;
using System.Text.RegularExpressions;
using Compression.Core.Streams;
using FileFormat.Gzip;
using FileFormat.Tar;

namespace FileFormat.UnityPackage;

/// <summary>
/// One asset of a Unity package: its project path, and the bytes Unity stores for it. A folder has no
/// <see cref="Asset"/>.
/// </summary>
public sealed record UnityPackageAsset(string Guid, string Path, byte[]? Asset, byte[]? Meta, byte[]? Preview);

/// <summary>
/// Reads and writes Unity's asset-package export (<c>.unitypackage</c>): a gzip-compressed tar holding
/// one directory per asset, named by the asset's GUID, with the members
/// <c>pathname</c> (the project path, first line), <c>asset</c> (the content; absent for a folder),
/// <c>asset.meta</c> (Unity's import settings, carrying the same GUID) and an optional
/// <c>preview.png</c>.
/// </summary>
public static partial class UnityPackage {

  [GeneratedRegex(@"^guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Multiline)]
  private static partial Regex MetaGuid();

  /// <summary>Reads every asset of a package, ordered by path.</summary>
  public static IReadOnlyList<UnityPackageAsset> Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;
    using var tarImage = new MemoryStream();
    using (var gz = new GzipStream(stream, CompressionStreamMode.Decompress, leaveOpen: true))
      gz.CopyTo(tarImage);
    tarImage.Position = 0;

    var members = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
    using (var tar = new TarReader(tarImage, leaveOpen: true)) {
      while (tar.GetNextEntry() is { } entry) {
        if (!entry.IsFile) continue;
        var name = entry.Name.Replace('\\', '/');
        if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        var slash = name.IndexOf('/');
        if (slash <= 0 || name.IndexOf('/', slash + 1) >= 0) continue; // only <guid>/<member>
        using var data = new MemoryStream();
        tar.CopyEntryDataTo(data);
        if (!members.TryGetValue(name[..slash], out var files))
          members[name[..slash]] = files = new(StringComparer.Ordinal);
        files[name[(slash + 1)..]] = data.ToArray();
      }
    }

    var assets = new List<UnityPackageAsset>(members.Count);
    foreach (var (guid, files) in members) {
      if (!files.TryGetValue("pathname", out var pathname))
        throw new InvalidDataException($"Unity package: asset {guid} has no pathname.");
      var path = Encoding.UTF8.GetString(pathname).Split('\n')[0].TrimEnd('\r');
      if (path.Length == 0)
        throw new InvalidDataException($"Unity package: asset {guid} has an empty pathname.");
      assets.Add(new UnityPackageAsset(guid, path, files.GetValueOrDefault("asset"), files.GetValueOrDefault("asset.meta"),
        files.GetValueOrDefault("preview.png")));
    }
    assets.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
    return assets;
  }

  /// <summary>Writes a package; the tar holds each asset's directory with its members in Unity's order.</summary>
  public static void Write(Stream output, IEnumerable<UnityPackageAsset> assets) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(assets);
    using var gz = new GzipStream(output, CompressionStreamMode.Compress, leaveOpen: true);
    using (var tar = new TarWriter(gz, leaveOpen: true, TarHeaderFormat.Ustar)) {
      var now = DateTimeOffset.UtcNow;
      foreach (var a in assets) {
        tar.AddEntry(new TarEntry { Name = a.Guid + "/", TypeFlag = TarConstants.TypeDirectory, Mode = 0x1ED, ModifiedTime = now });
        if (a.Asset != null) AddFile(tar, a.Guid + "/asset", a.Asset, now);
        if (a.Meta != null) AddFile(tar, a.Guid + "/asset.meta", a.Meta, now);
        AddFile(tar, a.Guid + "/pathname", Encoding.UTF8.GetBytes(a.Path), now);
        if (a.Preview != null) AddFile(tar, a.Guid + "/preview.png", a.Preview, now);
      }
      tar.Finish();
    }
  }

  /// <summary>The GUID a <c>.meta</c> file declares, or null.</summary>
  public static string? GuidOf(byte[] meta) {
    var m = MetaGuid().Match(Encoding.UTF8.GetString(meta));
    return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
  }

  /// <summary>The minimal <c>.meta</c> Unity accepts for a new asset or folder; Unity fills in the importer.</summary>
  public static byte[] MinimalMeta(string guid, bool folder)
    => Encoding.UTF8.GetBytes($"fileFormatVersion: 2\nguid: {guid}\n" + (folder ? "folderAsset: yes\n" : ""));

  private static void AddFile(TarWriter tar, string name, byte[] data, DateTimeOffset time)
    => tar.AddEntry(new TarEntry { Name = name, Size = data.Length, Mode = 0x1A4, ModifiedTime = time }, (ReadOnlySpan<byte>)data);
}
