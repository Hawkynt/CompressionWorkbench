using System.Security.Cryptography;

namespace FileFormat.Zpaq;

/// <summary>
/// Writes what zpaq 7.15's <c>add</c> and <c>extract -index</c> write beyond a plain archive:
/// encrypted archives (<c>-key</c>), a further part of a multi-part archive (<c>arc???</c>), a new
/// part made from an index alone (<c>-index</c>), and the index of an existing archive.
/// </summary>
/// <remarks>
/// Converted from zpaq.cpp 7.15 (Matt Mahoney, public domain): <c>Jidac::add</c> (part naming,
/// offsets and salts), <c>OutputArchive</c> (the key stream runs over the file offset plus the
/// size of the parts before it; only the first part and the index start with a salt) and the
/// <c>-index</c> branch of <c>Jidac::extract</c> (each version's c block with a data size of 0,
/// then its fragment tables and file index, the data blocks left out). An index's salt is the
/// archive's with the first byte XORed with <c>'7' ^ 'z'</c>.
/// </remarks>
internal static class ZpaqSetEditor {
  /// <summary>An archive holding one version, encrypted under <paramref name="password"/> when one is given.</summary>
  public static void Create(Stream output, string? password, string method, Action<ZpaqWriter> fill) {
    var plain = new MemoryStream();
    using (var writer = new ZpaqWriter(plain, leaveOpen: true, method))
      fill(writer);
    if (password == null) {
      plain.Position = 0;
      plain.CopyTo(output);
      return;
    }
    var salt = RandomNumberGenerator.GetBytes(ZpaqCrypto.SaltSize);
    var data = plain.ToArray();
    ZpaqCrypto.Transform(ZpaqCrypto.DeriveKey(password, salt), salt, ZpaqCrypto.SaltSize, data);
    output.Write(salt);
    output.Write(data);
  }

  /// <summary>
  /// Appends one version to a single-file archive, encrypted or not. Returns false when the
  /// archive cannot take one (streaming layout, incomplete last transaction, damage).
  /// </summary>
  public static bool AppendInPlace(Stream archive, string? password, string method, Action<ZpaqWriter>? add,
      Func<ZpaqReader, List<string>>? removals) {
    archive.Position = 0;
    ZpaqReader reader;
    try {
      reader = new ZpaqReader(archive, password, leaveOpen: true);
    } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or NotSupportedException) {
      if (password != null)
        throw;
      return false;
    }
    List<string> gone;
    IReadOnlyList<(byte[] Sha1, int Usize)> fragments;
    long latest;
    using (reader) {
      if (reader.IsIndex)
        throw new NotSupportedException("ZPAQ: an index takes a new version only when opened from its file (arc000.zpaq), where the next part is written beside it; it was left unchanged.");
      if (reader.VersionCount == 0 || reader.Errors > 0 || reader.HasIncompleteTransaction || reader.HasStreamingEntries)
        return false;
      gone = removals?.Invoke(reader) ?? [];
      fragments = reader.FragmentTable;
      latest = reader.LatestVersionDate;
    }
    if (add == null && gone.Count == 0)
      return true;

    var plain = Transaction(fragments, latest, method, add, gone, index: null);
    var originalLength = archive.Length;
    if (password != null)
      ZpaqCrypto.Transform(ZpaqCrypto.DeriveKey(password, Salt(archive)), Salt(archive), originalLength, plain);
    try {
      archive.Position = originalLength;
      archive.Write(plain);
      archive.Flush();
      archive.Position = 0;
      using var check = new ZpaqReader(archive, password, leaveOpen: true);
      Verify(check);
    } catch {
      archive.SetLength(originalLength);
      throw;
    }
    return true;
  }

  /// <summary>
  /// Adds one version as a new part file: to a multi-part set opened from its first part, or, opened
  /// from an index (part 0), as the next part beside it with the index updated; the earlier parts
  /// need not be present then. Returns false when <paramref name="opened"/> is neither.
  /// </summary>
  public static bool AppendPart(FileStream opened, string? password, string method, Action<ZpaqWriter>? add,
      Func<ZpaqReader, List<string>>? removals) {
    if (!ZpaqArchiveSet.TryParse(opened.Name, out var name) || name.Number > 1)
      return false;
    opened.Position = 0;
    var indexMode = name.Number == 0;
    ZpaqReader reader;
    if (indexMode) {
      // The index alone is the archive's catalogue: its own salt, no data.
      reader = new ZpaqReader(opened, password, leaveOpen: true);
      if (!reader.IsIndex) {
        reader.Dispose();
        return false;
      }
    } else {
      reader = ZpaqArchiveSet.Open(opened, password);
      if (!reader.IsMultiPart) {
        reader.Dispose();
        return false;
      }
    }

    List<string> gone;
    IReadOnlyList<(byte[] Sha1, int Usize)> fragments;
    long latest, offset;
    string partPath;
    using (reader) {
      if (reader.VersionCount == 0 || reader.Errors > 0 || reader.HasIncompleteTransaction || reader.HasStreamingEntries)
        throw new NotSupportedException("ZPAQ: the set cannot take another version (incomplete, damaged or streaming); nothing was written.");
      gone = removals?.Invoke(reader) ?? [];
      fragments = reader.FragmentTable;
      latest = reader.LatestVersionDate;
      if (indexMode) {
        offset = opened.Length + reader.DataBlockBytes;
        partPath = name.Path(reader.VersionCount + 1);
      } else {
        offset = 0;
        for (var i = 1; i <= reader.PartCount; ++i)
          offset += new FileInfo(name.Path(i)).Length;
        partPath = name.Path(reader.PartCount + 1);
      }
    }
    if (add == null && gone.Count == 0)
      return true;
    if (File.Exists(partPath))
      throw new IOException($"ZPAQ: the next part '{Path.GetFileName(partPath)}' already exists; nothing was written.");

    var indexPlain = indexMode ? new MemoryStream() : null;
    var part = Transaction(fragments, latest, method, add, gone, indexPlain);
    byte[]? indexBytes = indexPlain?.ToArray();
    if (password != null) {
      if (indexMode) {
        var indexSalt = Salt(opened);
        ZpaqCrypto.Transform(ZpaqCrypto.DeriveKey(password, indexSalt), indexSalt, opened.Length, indexBytes!);
        var archiveSalt = ZpaqCrypto.IndexSalt(indexSalt);
        ZpaqCrypto.Transform(ZpaqCrypto.DeriveKey(password, archiveSalt), archiveSalt, offset, part);
      } else {
        var salt = Salt(opened); // opened is part 1, which alone starts with the salt
        ZpaqCrypto.Transform(ZpaqCrypto.DeriveKey(password, salt), salt, offset, part);
      }
    }

    var indexLength = opened.Length;
    try {
      File.WriteAllBytes(partPath, part);
      if (indexBytes != null) {
        opened.Position = indexLength;
        opened.Write(indexBytes);
        opened.Flush();
        opened.Position = 0;
        using var index = new ZpaqReader(opened, password, leaveOpen: true);
        if (index.Errors > 0 || !index.IsIndex)
          throw new InvalidDataException("ZPAQ: the updated index does not read back.");
      } else {
        opened.Position = 0;
        using var check = ZpaqArchiveSet.Open(opened, password);
        if (check.PartCount != reader.PartCount + 1)
          throw new InvalidDataException("ZPAQ: the new part does not continue the set.");
        Verify(check);
      }
    } catch {
      if (indexBytes != null)
        opened.SetLength(indexLength);
      File.Delete(partPath);
      throw;
    }
    return true;
  }

  /// <summary>
  /// Writes the index of the archive <paramref name="archive"/> opens (its parts found as zpaq
  /// finds them), as <c>zpaq extract arc -index</c> does; an encrypted archive gives an index
  /// encrypted under the same password with the index salt.
  /// </summary>
  public static void WriteIndex(Stream archive, string? password, Stream index) {
    archive.Position = 0;
    using var reader = ZpaqArchiveSet.Open(archive, password);
    if (reader.IsIndex)
      throw new NotSupportedException("ZPAQ: this is already an index.");
    if (reader.VersionCount == 0 || reader.HasStreamingEntries || reader.Errors > 0 || reader.HasIncompleteTransaction)
      throw new NotSupportedException("ZPAQ: only complete journaling archives have an index.");
    var plain = new MemoryStream();
    var writer = new ZpaqStreamWriter(plain);
    foreach (var v in reader.VersionLayout) {
      var header = new ZpaqBuffer();
      for (var i = 0; i < 8; ++i)
        header.Put(0);
      ZpaqMethods.CompressBlock(header, writer, "0", "jDC" + ZpaqMethods.Itos(v.Date, 14) + "c" + ZpaqMethods.Itos(v.FragmentNumber, 10), [.. "jDC\x01"u8]);
      var tablesStart = v.DataOffset + v.CompressedSize;
      plain.Write(reader.ReadRaw(tablesStart, checked((int)(v.End - tablesStart))));
    }
    var data = plain.ToArray();
    if (password == null) {
      index.Write(data);
      return;
    }
    archive.Position = 0;
    var salt = ZpaqCrypto.IndexSalt(Salt(archive));
    ZpaqCrypto.Transform(ZpaqCrypto.DeriveKey(password, salt), salt, ZpaqCrypto.SaltSize, data);
    index.Write(salt);
    index.Write(data);
  }

  /// <summary>One plaintext transaction appended after <paramref name="fragments"/>, plus its index form when asked.</summary>
  private static byte[] Transaction(IReadOnlyList<(byte[] Sha1, int Usize)> fragments, long latest, string method,
      Action<ZpaqWriter>? add, List<string> removals, Stream? index) {
    var plain = new MemoryStream();
    using (var writer = ZpaqWriter.Appending(plain, fragments, latest, method, index)) {
      add?.Invoke(writer);
      foreach (var name in removals)
        writer.RemoveEntry(name);
    }
    return plain.ToArray();
  }

  private static void Verify(ZpaqReader check) {
    if (check.Errors > 0 || check.HasIncompleteTransaction)
      throw new InvalidDataException("ZPAQ: the appended version does not read back.");
    foreach (var e in check.Entries.Where(e => !e.IsDirectory))
      check.ExtractBytes(e); // every fragment's SHA-1 is checked on the way
  }

  private static byte[] Salt(Stream stream) {
    var salt = new byte[ZpaqCrypto.SaltSize];
    stream.Position = 0;
    stream.ReadExactly(salt);
    return salt;
  }
}

/// <summary>The index of a zpaq archive (<c>zpaq extract arc -index F</c>).</summary>
public static class ZpaqIndex {
  /// <summary>
  /// Writes to <paramref name="index"/> the index of the archive in <paramref name="archive"/>:
  /// every version's transaction header, fragment tables and file index, without the data. Opened
  /// from part 1 of a multi-part set (<c>arc001.zpaq</c>), the whole set is indexed. With an index,
  /// <c>zpaq add arc??? -index F</c> (or Add on the index) writes further parts without the earlier ones.
  /// </summary>
  /// <param name="archive">The archive, or the first part of a set (opened from its file).</param>
  /// <param name="index">Where the index goes.</param>
  /// <param name="password">The archive's <c>-key</c>, or null; the index is encrypted under it too.</param>
  public static void Write(Stream archive, Stream index, string? password = null) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(index);
    ZpaqSetEditor.WriteIndex(archive, string.IsNullOrEmpty(password) ? null : password, index);
  }
}
