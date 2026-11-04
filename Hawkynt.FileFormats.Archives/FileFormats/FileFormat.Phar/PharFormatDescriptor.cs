#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Phar;

/// <summary>
/// PHP archive (PHAR) in the native phar layout — the format of <c>composer.phar</c>,
/// <c>phpunit.phar</c> and other self-contained PHP applications.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.php.net/manual/en/phar.fileformat.php</c> — PHP manual, Phar file format (stub, manifest, entries, signature)</description></item>
///   <item><description><c>https://www.php.net/manual/en/book.phar.php</c> — PHP's Phar extension, the reference reader and writer (the oracle)</description></item>
/// </list>
/// </summary>
public sealed class PharFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {

  public string Id => "Phar";
  public string DisplayName => "PHAR";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: edits rebuild the archive with the stub, alias, metadata and every untouched member's stored
  // bytes kept, and re-sign it with the same digest — what PHP does on flush. OpenSSL-signed archives
  // refuse edits (they would need the publisher's private key).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".phar";
  public IReadOnlyList<string> Extensions => [".phar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // The stub is arbitrary PHP; nothing sits at a fixed offset, so detection is by extension.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("store", "Stored"), new("gzip", "Raw DEFLATE"), new("bzip2", "bzip2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "PHP archive (native phar layout): stored, gzip (raw DEFLATE) and bzip2 members, MD5/SHA-1/SHA-256/SHA-512 " +
    "signatures verified and written. Create options: alias, signature (md5|sha1|sha256|sha512, default sha256).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PharReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.IsDirectory ? e.Name.TrimEnd('/') : e.Name,
      e.UncompressedSize, e.CompressedSize, e.IsDirectory ? "store" : MethodName(e.Compression), e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PharReader(stream);
    foreach (var e in r.Entries) {
      var name = e.IsDirectory ? e.Name.TrimEnd('/') : e.Name;
      if (files != null && !MatchesFilter(name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, name)); continue; }
      WriteFile(outputDir, name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var compression = options.MethodName?.ToLowerInvariant() switch {
      null or "" or "store" or "none" => PharCompression.None,
      "gzip" or "gz" or "deflate" => PharCompression.Gzip,
      "bzip2" or "bz2" => PharCompression.Bzip2,
      var m => throw new NotSupportedException($"PHAR: unknown method '{m}' (store, gzip, bzip2)."),
    };
    var signature = (options.GetString("signature") ?? "sha256").ToLowerInvariant() switch {
      "md5" => PharSignatureType.Md5,
      "sha1" => PharSignatureType.Sha1,
      "sha256" => PharSignatureType.Sha256,
      "sha512" => PharSignatureType.Sha512,
      var s => throw new NotSupportedException($"PHAR: unknown signature '{s}' (md5, sha1, sha256, sha512)."),
    };
    var entries = inputs.Select(i => i.IsDirectory
      ? new PharWriteEntry(i.ArchiveName.TrimEnd('/') + "/", [], Timestamp(i), PharWriter.DefaultDirectoryPermissions)
      : new PharWriteEntry(i.ArchiveName, i.ReadContent(), Timestamp(i), PharWriter.DefaultFilePermissions, compression)).ToList();
    PharWriter.Write(output, entries, alias: options.GetString("alias"), signature: signature);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    Rewrite(archive, entries => {
      foreach (var i in inputs) {
        var name = i.IsDirectory ? i.ArchiveName.TrimEnd('/') + "/" : i.ArchiveName;
        var replacement = i.IsDirectory
          ? new PharWriteEntry(name, [], Timestamp(i), PharWriter.DefaultDirectoryPermissions)
          : new PharWriteEntry(name, i.ReadContent(), Timestamp(i), PharWriter.DefaultFilePermissions);
        var existing = entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        if (existing < 0) { entries.Add(replacement); continue; }
        // A replaced file keeps its permissions, compression and metadata.
        var old = entries[existing];
        entries[existing] = replacement with { Permissions = old.Permissions, Compression = old.Compression, Metadata = old.Metadata };
      }
    });
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var drop = new HashSet<string>(entryNames.Select(n => n.TrimEnd('/')), StringComparer.Ordinal);
    Rewrite(archive, entries => entries.RemoveAll(e => drop.Contains(e.Name.TrimEnd('/'))));
  }

  private static void Rewrite(Stream archive, Action<List<PharWriteEntry>> edit) {
    ArgumentNullException.ThrowIfNull(archive);
    var r = new PharReader(archive);
    if (r.SignatureType is PharSignatureType.OpenSsl or PharSignatureType.OpenSslSha256 or PharSignatureType.OpenSslSha512)
      throw new NotSupportedException("PHAR: the archive is OpenSSL-signed; re-signing it needs the publisher's private key.");
    var entries = r.Entries.Select(e => new PharWriteEntry(e.Name, r.Extract(e), e.Timestamp, e.Permissions, e.Compression,
      e.Metadata, e.IsDirectory ? null : r.ReadStored(e))).ToList();
    edit(entries);
    using var rebuilt = new MemoryStream();
    PharWriter.Write(rebuilt, entries, r.Stub, r.Alias, r.Metadata, r.SignatureType ?? PharSignatureType.Sha256, keepStubVerbatim: true);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }

  private static uint Timestamp(ArchiveInputInfo input) {
    var time = input.Metadata?.LastWriteTimeUtc
      ?? (!input.IsDirectory && input.InMemoryContent == null && File.Exists(input.FullPath) ? File.GetLastWriteTimeUtc(input.FullPath) : DateTime.UtcNow);
    var seconds = (long)(time - DateTime.UnixEpoch).TotalSeconds;
    return (uint)Math.Clamp(seconds, 0, uint.MaxValue);
  }

  private static string MethodName(PharCompression c) => c switch {
    PharCompression.Gzip => "gzip",
    PharCompression.Bzip2 => "bzip2",
    _ => "store",
  };
}
