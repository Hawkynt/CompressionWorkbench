using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Compression.Core.Checksums;

namespace FileFormat.Rar;

/// <summary>
/// RAR recovery volumes (<c>.rev</c>): creating them for a volume set (<c>rar -rv</c>), checking a
/// set against them and rebuilding missing or damaged volumes (<c>rar rc</c>). RAR 5 sets get
/// Reed-Solomon over GF(2^16) with a table of every volume's size and CRC-32; RAR 3/4 sets get
/// the classic systematic Reed-Solomon over GF(2^8) of RAR 3. Both layouts were measured from rar's
/// output (docs/RAR-RECOVERY-ON-DISK.md).
/// </summary>
public static partial class RarRecoveryVolumes {
  private const int Chunk = 1 << 16;
  private static readonly byte[] Rev5Magic = "Rar!\u001aRev"u8.ToArray();

  [GeneratedRegex(@"^(?<base>.*)\.part(?<n>[0-9]+)\.(rar|rev|exe)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex NewStyle();

  [GeneratedRegex(@"^(?<base>.*?)(?<n>[0-9]+)\.rev$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex OldStyleRev();

  /// <summary>
  /// Writes <paramref name="count"/> recovery volumes beside the set whose first volume is
  /// <paramref name="firstVolume"/>; any <paramref name="count"/> missing or damaged volumes can then
  /// be rebuilt. Returns the paths written.
  /// </summary>
  public static IReadOnlyList<string> Create(string firstVolume, int count) {
    ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
    var volumes = ExistingVolumes(firstVolume);
    if (volumes.Count < 1) throw new FileNotFoundException("No volumes found.", firstVolume);
    var streams = volumes.Select(v => (Stream)File.OpenRead(v)).ToList();
    try {
      return Create(streams, volumes[0], count);
    } finally {
      foreach (var s in streams) s.Dispose();
    }
  }

  /// <summary>
  /// Writes recovery volumes for the volumes in <paramref name="volumes"/> (readable, seekable,
  /// in order), named after <paramref name="firstVolumeName"/>.
  /// </summary>
  internal static IReadOnlyList<string> Create(IReadOnlyList<Stream> volumes, string firstVolumeName, int count) {
    var head = new byte[8];
    RarRecovery.ReadAt(volumes[0], 0, head);
    var rar5 = head.AsSpan().SequenceEqual(RarConstants.Rar5Signature);
    var names = RevNames(firstVolumeName, count, rar5 ? 65535 - volumes.Count : 255 - volumes.Count);
    if (rar5) CreateRev5(volumes, names);
    else CreateRev3(volumes, names);
    return names;
  }

  /// <summary>Checks a volume set (named by any of its volumes or recovery volumes) against its recovery volumes.</summary>
  public static RarRecoveryReport Verify(string anyVolume) => Process(anyVolume, rebuild: false);

  /// <summary>
  /// Rebuilds the missing or damaged volumes of a set from its recovery volumes. A damaged volume
  /// is first renamed to <c>name.bad</c>.
  /// </summary>
  public static RarRecoveryReport Reconstruct(string anyVolume) => Process(anyVolume, rebuild: true);

  // ---------------------------------------------------------------- names

  private static List<string> ExistingVolumes(string firstVolume) {
    var seq = RarVolumeNames.SequenceOf(firstVolume) ?? throw new ArgumentException($"'{firstVolume}' is not named like a RAR volume.");
    var list = new List<string>();
    for (var i = 0; File.Exists(seq(i)); ++i) list.Add(seq(i));
    return list;
  }

  /// <summary>rar's names: <c>name.partN.rev</c> beside new-style volumes, <c>nameN.rev</c> beside <c>name.rar</c>/<c>.r00</c>.</summary>
  private static List<string> RevNames(string firstVolume, int count, int max) {
    if (count > max) throw new ArgumentOutOfRangeException(nameof(count), $"At most {max} recovery volumes fit this set.");
    var dir = Path.GetDirectoryName(firstVolume) ?? "";
    var file = Path.GetFileName(firstVolume);
    var m = NewStyle().Match(file);
    var list = new List<string>();
    for (var i = 1; i <= count; ++i)
      list.Add(m.Success
        ? Path.Combine(dir, $"{m.Groups["base"].Value}.part{i.ToString(CultureInfo.InvariantCulture).PadLeft(m.Groups["n"].Value.Length, '0')}.rev")
        : Path.Combine(dir, $"{file[..^4]}{i.ToString(CultureInfo.InvariantCulture)}.rev"));
    return list;
  }

  /// <summary>The recovery volumes beside <paramref name="anyVolume"/> and the first volume's name pattern.</summary>
  private static (List<string> Revs, Func<int, string> Volume) Locate(string anyVolume) {
    var dir = Path.GetDirectoryName(Path.GetFullPath(anyVolume)) ?? "";
    var file = Path.GetFileName(anyVolume);
    var m = NewStyle().Match(file);
    if (m.Success) {
      var b = m.Groups["base"].Value;
      var revs = Directory.GetFiles(dir, b + ".part*.rev").Where(p => NewStyle().Match(Path.GetFileName(p)) is { Success: true } r && r.Groups["base"].Value == b).ToList();
      var width = m.Groups["n"].Value.Length;
      // Volumes keep the width the set was written with; take it from any volume that exists.
      var vol = Directory.GetFiles(dir, b + ".part*.rar").Select(p => NewStyle().Match(Path.GetFileName(p))).FirstOrDefault(r => r.Success);
      if (vol != null) width = vol.Groups["n"].Value.Length;
      return (revs, i => Path.Combine(dir, $"{b}.part{(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}.rar"));
    }
    string baseName;
    if (OldStyleRev().IsMatch(file)) {
      // "name12.rev": the base is the longest prefix before the digits that names a volume.
      var stem = file[..^4];
      var cut = stem.Length;
      while (cut > 0 && char.IsAsciiDigit(stem[cut - 1])) --cut;
      baseName = stem[..cut];
      for (var k = stem.Length - 1; k >= cut; --k)
        if (File.Exists(Path.Combine(dir, stem[..k] + ".rar")) || File.Exists(Path.Combine(dir, stem[..k] + ".r00"))) {
          baseName = stem[..k];
          break;
        }
    } else
      baseName = RarVolumeNames.SequenceOf(Path.Combine(dir, file)) is { } s ? Path.GetFileNameWithoutExtension(s(0)) : Path.GetFileNameWithoutExtension(file);
    var first = Path.Combine(dir, baseName + ".rar");
    var seq = RarVolumeNames.SequenceOf(first)!;
    var revFiles = Directory.GetFiles(dir, baseName + "*.rev").Where(p => {
      var stem = Path.GetFileName(p)[..^4];
      return stem.Length > baseName.Length && stem[baseName.Length..].All(char.IsAsciiDigit);
    }).ToList();
    return (revFiles, seq);
  }

  private static RarRecoveryReport Process(string anyVolume, bool rebuild) {
    var (revs, volume) = Locate(anyVolume);
    if (revs.Count == 0) return new RarRecoveryReport(RarRecoveryKind.None, false, 0, 0, false, false, "No recovery volumes found.");
    var rar5 = revs.Any(r => { using var fs = File.OpenRead(r); var b = new byte[8]; return fs.Read(b) == 8 && b.AsSpan().SequenceEqual(Rev5Magic); });
    return rar5 ? ProcessRev5(revs, volume, rebuild) : ProcessRev3(revs, volume, rebuild);
  }

  private static void ReadAt(Stream? f, long position, Span<byte> buffer) {
    if (f == null || position >= f.Length) {
      buffer.Clear();
      return;
    }
    RarRecovery.ReadAt(f, position, buffer);
  }

  private static void SetAside(string path) {
    if (!File.Exists(path)) return;
    var bad = path + ".bad";
    if (File.Exists(bad)) File.Delete(bad);
    File.Move(path, bad);
  }

  // ---------------------------------------------------------------- RAR 5

  private static void CreateRev5(IReadOnlyList<Stream> files, List<string> names) {
    var nd = files.Count;
    var nr = names.Count;
    if (nd + nr > 65535) throw new NotSupportedException("A RAR 5 set with recovery volumes holds at most 65535 files.");
    var outs = names.Select(n => new FileStream(n, FileMode.Create, FileAccess.ReadWrite, FileShare.None)).ToList();
    try {
      var sizes = files.Select(f => f.Length).ToArray();
      var crcs = files.Select(f => { f.Position = 0; return Crc32Of(f); }).ToArray();
      var length = sizes.Max();
      length += length & 1;
      var hsz = 11 + 12 * nd;
      var headerLength = 16 + hsz;
      var dataCrc = new Crc32[nr];
      for (var i = 0; i < nr; ++i) dataCrc[i] = new Crc32();
      var data = new byte[Chunk];
      var rec = new byte[nr][];
      for (var i = 0; i < nr; ++i) rec[i] = new byte[Chunk];
      for (long c = 0; c < length; c += Chunk) {
        var len = (int)Math.Min(Chunk, length - c);
        foreach (var r in rec) r.AsSpan(0, len).Clear();
        for (var j = 0; j < nd; ++j) {
          ReadAt(files[j], c, data.AsSpan(0, len));
          for (var i = 0; i < nr; ++i) RarGf16.MulAdd(rec[i].AsSpan(0, len), data.AsSpan(0, len), RarGf16.LogOf(RarGf16.Cauchy(nd + i, j)));
        }
        for (var i = 0; i < nr; ++i) {
          outs[i].Position = headerLength + c;
          outs[i].Write(rec[i], 0, len);
          dataCrc[i].Update(rec[i].AsSpan(0, len));
        }
      }
      for (var i = 0; i < nr; ++i) {
        var h = new byte[headerLength];
        Rev5Magic.CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)hsz);
        h[16] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(17), (ushort)nd);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(19), (ushort)nr);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(21), (ushort)(nd + i));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(23), dataCrc[i].Value);
        for (var j = 0; j < nd; ++j) {
          BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(27 + 12 * j), (ulong)sizes[j]);
          BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(35 + 12 * j), crcs[j]);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), Crc32.Compute(h.AsSpan(12)));
        outs[i].Position = 0;
        outs[i].Write(h);
      }
    } finally {
      foreach (var o in outs) o.Dispose();
    }
  }

  private static uint Crc32Of(Stream s) {
    var crc = new Crc32();
    var buf = new byte[Chunk];
    int n;
    while ((n = s.Read(buf, 0, buf.Length)) > 0) crc.Update(buf.AsSpan(0, n));
    return crc.Value;
  }

  private sealed record Rev5(string Path, int Index, long DataOffset, long DataLength);

  private static RarRecoveryReport ProcessRev5(List<string> revPaths, Func<int, string> volume, bool rebuild) {
    var revs = new List<Rev5>();
    long[]? sizes = null;
    uint[]? crcs = null;
    var nd = 0;
    foreach (var p in revPaths) {
      using var fs = File.OpenRead(p);
      var head = new byte[16];
      if (fs.Read(head) != 16 || !head.AsSpan(0, 8).SequenceEqual(Rev5Magic)) continue;
      var hsz = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
      if (hsz < 11 || hsz > 12 * 65536 + 11) continue;
      var h = new byte[16 + hsz];
      fs.Position = 0;
      if (fs.Read(h) != h.Length || Crc32.Compute(h.AsSpan(12)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8))) continue;
      var count = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(17));
      if (hsz != 11 + 12 * count) continue;
      var idx = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(21));
      fs.Position = h.Length;
      if (Crc32Of(fs) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(23))) continue;
      if (sizes == null) {
        nd = count;
        sizes = new long[nd];
        crcs = new uint[nd];
        for (var j = 0; j < nd; ++j) {
          sizes[j] = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(27 + 12 * j));
          crcs[j] = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(35 + 12 * j));
        }
      } else if (count != nd) continue;
      if (idx < nd || revs.Any(r => r.Index == idx)) continue;
      revs.Add(new Rev5(p, idx, h.Length, fs.Length - h.Length));
    }
    if (sizes == null || crcs == null)
      return new RarRecoveryReport(RarRecoveryKind.Rar5Volumes, false, 0, 0, false, false, "The recovery volumes are all damaged.");

    var damaged = new List<int>();
    for (var j = 0; j < nd; ++j) {
      var path = volume(j);
      if (!File.Exists(path) || new FileInfo(path).Length != sizes[j]) {
        damaged.Add(j);
        continue;
      }
      using var fs = File.OpenRead(path);
      if (Crc32Of(fs) != crcs[j]) damaged.Add(j);
    }
    var what = $"RAR 5 recovery volumes: {nd} volumes, {revs.Count} usable recovery volume(s); {damaged.Count} volume(s) missing or damaged.";
    var repairable = damaged.Count <= revs.Count;
    if (!rebuild || damaged.Count == 0)
      return new RarRecoveryReport(RarRecoveryKind.Rar5Volumes, damaged.Count == 0, damaged.Count, revs.Count, repairable, false,
        damaged.Count == 0 ? "RAR 5 recovery volumes: every volume intact." : what);
    if (!repairable)
      return new RarRecoveryReport(RarRecoveryKind.Rar5Volumes, false, damaged.Count, revs.Count, false, false, what + " Not enough recovery volumes.");

    var rows = revs.Take(damaged.Count).ToList();
    var n = damaged.Count;
    var m = new ushort[n, n];
    for (var e = 0; e < n; ++e)
      for (var k = 0; k < n; ++k) m[e, k] = RarGf16.Cauchy(rows[e].Index, damaged[k]);
    if (!RarGf16.Invert(m)) throw new InvalidDataException("RAR 5 recovery volumes: singular recovery matrix.");
    var known = Enumerable.Range(0, nd).Where(j => !damaged.Contains(j)).ToList();
    var knownFiles = known.ToDictionary(j => j, j => File.OpenRead(volume(j)));
    var revFiles = rows.Select(r => File.OpenRead(r.Path)).ToList();
    foreach (var j in damaged) SetAside(volume(j));
    var outs = damaged.Select(j => new FileStream(volume(j), FileMode.Create, FileAccess.ReadWrite, FileShare.None)).ToList();
    try {
      var length = rows[0].DataLength;
      var rhs = new byte[n][];
      for (var e = 0; e < n; ++e) rhs[e] = new byte[Chunk];
      var data = new byte[Chunk];
      var output = new byte[Chunk];
      for (long c = 0; c < length; c += Chunk) {
        var len = (int)Math.Min(Chunk, length - c);
        for (var e = 0; e < n; ++e) RarRecovery.ReadAt(revFiles[e], rows[e].DataOffset + c, rhs[e].AsSpan(0, len));
        foreach (var j in known) {
          ReadAt(knownFiles[j], c, data.AsSpan(0, len));
          for (var e = 0; e < n; ++e) RarGf16.MulAdd(rhs[e].AsSpan(0, len), data.AsSpan(0, len), RarGf16.LogOf(RarGf16.Cauchy(rows[e].Index, j)));
        }
        for (var k = 0; k < n; ++k) {
          output.AsSpan(0, len).Clear();
          for (var e = 0; e < n; ++e)
            if (m[k, e] != 0) RarGf16.MulAdd(output.AsSpan(0, len), rhs[e].AsSpan(0, len), RarGf16.LogOf(m[k, e]));
          var real = (int)Math.Clamp(sizes[damaged[k]] - c, 0, len);
          if (real > 0) {
            outs[k].Position = c;
            outs[k].Write(output, 0, real);
          }
        }
      }
    } finally {
      foreach (var f in knownFiles.Values) f.Dispose();
      foreach (var f in revFiles) f.Dispose();
      foreach (var o in outs) o.Dispose();
    }
    var failed = damaged.Where(j => { using var fs = File.OpenRead(volume(j)); return Crc32Of(fs) != crcs[j]; }).ToList();
    return failed.Count == 0
      ? new RarRecoveryReport(RarRecoveryKind.Rar5Volumes, false, damaged.Count, revs.Count, true, true, what + " Rebuilt.")
      : new RarRecoveryReport(RarRecoveryKind.Rar5Volumes, false, failed.Count, revs.Count, true, false, what + " Rebuilt volumes fail their CRC-32.");
  }

  // ---------------------------------------------------------------- RAR 3

  /// <summary>Parity coefficients: column J of the systematic code (parity of the unit message e_J).</summary>
  private static byte[][] Rev3Coefficients(int d, int r) {
    var g = RarGf8.Generator(r);
    var coef = new byte[d][];
    for (var j = 0; j < d; ++j) {
      var rem = new byte[r];
      for (var k = 0; k < d; ++k) {
        var fb = (byte)((k == j ? 1 : 0) ^ rem[0]);
        Array.Copy(rem, 1, rem, 0, r - 1);
        rem[r - 1] = 0;
        if (fb != 0)
          for (var t = 0; t < r; ++t) rem[t] ^= RarGf8.Mul(fb, g[t + 1]);
      }
      coef[j] = rem;
    }
    return coef;
  }

  /// <summary>Whether a RAR 3/4 volume ends with the 7 spare bytes rar reserves for a recovery volume's trailer.</summary>
  private static bool HasRevSpace(Stream volume) => Rar4End(volume) is { } e && (e.Flags & 0x4) != 0;

  /// <summary>The end-of-archive block of a RAR 3/4 volume, walking the headers with their CRCs; null if broken.</summary>
  private static (long Position, int Size, ushort Flags)? Rar4End(string volume) {
    using var fs = File.OpenRead(volume);
    return Rar4End(fs);
  }

  private static (long Position, int Size, ushort Flags)? Rar4End(Stream fs) {
    var head = new byte[7];
    long pos = 7;
    var len = fs.Length;
    while (pos + 7 <= len) {
      RarRecovery.ReadAt(fs, pos, head);
      var type = head[2];
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(3));
      var size = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(5));
      if (size < 7 || pos + size > len) return null;
      var h = new byte[size];
      RarRecovery.ReadAt(fs, pos, h);
      if ((ushort)Crc32.Compute(h.AsSpan(2)) != BinaryPrimitives.ReadUInt16LittleEndian(h)) return null;
      if (type == RarConstants.Rar4TypeEnd) {
        if ((flags & 0x2) != 0 && size >= 11) {
          var want = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(7));
          fs.Position = 0;
          var crc = new Crc32();
          var buf = new byte[Chunk];
          for (long left = pos; left > 0;) {
            var n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) return null;
            crc.Update(buf.AsSpan(0, n));
            left -= n;
          }
          if (crc.Value != want) return null;
        }
        return (pos, size, flags);
      }
      long add = 0;
      if ((flags & 0x8000) != 0 || type is RarConstants.Rar4TypeFile or RarConstants.Rar4TypeNewSub)
        add = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(7));
      if (type is RarConstants.Rar4TypeFile or RarConstants.Rar4TypeNewSub && (flags & 0x100) != 0 && size >= 32 + 8)
        add |= (long)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(32)) << 32;
      pos += size + add;
    }
    return null;
  }

  private static void CreateRev3(IReadOnlyList<Stream> files, List<string> names) {
    var d = files.Count;
    var r = names.Count;
    if (d + r > 255) throw new NotSupportedException("A RAR 3 set with recovery volumes holds at most 255 files.");
    var revSpace = files.All(HasRevSpace);
    var length = files.Max(v => v.Length) - (revSpace ? 7 : 0);
    var coef = Rev3Coefficients(d, r);
    var outs = names.Select(n => new FileStream(n, FileMode.Create, FileAccess.ReadWrite, FileShare.None)).ToList();
    try {
      var data = new byte[Chunk];
      var par = new byte[r][];
      for (var i = 0; i < r; ++i) par[i] = new byte[Chunk];
      for (long c = 0; c < length; c += Chunk) {
        var len = (int)Math.Min(Chunk, length - c);
        foreach (var p in par) p.AsSpan(0, len).Clear();
        for (var j = 0; j < d; ++j) {
          ReadAt(files[j], c, data.AsSpan(0, len));
          for (var i = 0; i < r; ++i) {
            var f = coef[j][i];
            if (f == 0) continue;
            var dst = par[i];
            for (var k = 0; k < len; ++k)
              if (data[k] != 0) dst[k] ^= RarGf8.Mul(data[k], f);
          }
        }
        for (var i = 0; i < r; ++i) outs[i].Write(par[i], 0, len);
      }
      var tail = new byte[4];
      for (var i = 0; i < r; ++i) {
        outs[i].Write([(byte)(d - 1), (byte)(r - 1), (byte)i]);
        outs[i].Flush();
        outs[i].Position = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(tail, Crc32Of(outs[i]));
        outs[i].Write(tail);
      }
    } finally {
      foreach (var o in outs) o.Dispose();
    }
  }

  private sealed record Rev3(string Path, int Index, long Length);

  private static RarRecoveryReport ProcessRev3(List<string> revPaths, Func<int, string> volume, bool rebuild) {
    var revs = new List<Rev3>();
    int d = 0, r = 0;
    foreach (var p in revPaths) {
      using var fs = File.OpenRead(p);
      if (fs.Length < 8) continue;
      var tail = new byte[7];
      RarRecovery.ReadAt(fs, fs.Length - 7, tail);
      fs.Position = 0;
      var crc = new Crc32();
      var buf = new byte[Chunk];
      for (long left = fs.Length - 4; left > 0;) {
        var got = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
        if (got <= 0) break;
        crc.Update(buf.AsSpan(0, got));
        left -= got;
      }
      if (crc.Value != BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(3))) continue;
      var dd = tail[0] + 1;
      var rr = tail[1] + 1;
      if (d == 0) { d = dd; r = rr; } else if (dd != d || rr != r) continue;
      if (tail[2] >= r || revs.Any(x => x.Index == tail[2])) continue;
      revs.Add(new Rev3(p, tail[2], fs.Length - 7));
    }
    if (revs.Count == 0) return new RarRecoveryReport(RarRecoveryKind.Rar3Volumes, false, 0, 0, false, false, "The recovery volumes are all damaged.");

    var damaged = Enumerable.Range(0, d).Where(j => !File.Exists(volume(j)) || Rar4End(volume(j)) == null).ToList();
    var missingRevs = Enumerable.Range(0, r).Where(i => revs.All(x => x.Index != i)).ToList();
    var unknown = damaged.Count + missingRevs.Count;
    var what = $"RAR 3 recovery volumes: {d} volumes, {revs.Count} of {r} recovery volume(s) usable; {damaged.Count} volume(s) missing or damaged.";
    var repairable = unknown <= r;
    if (!rebuild || damaged.Count == 0)
      return new RarRecoveryReport(RarRecoveryKind.Rar3Volumes, damaged.Count == 0, damaged.Count, revs.Count, repairable, false,
        damaged.Count == 0 ? "RAR 3 recovery volumes: every volume intact." : what);
    if (!repairable)
      return new RarRecoveryReport(RarRecoveryKind.Rar3Volumes, false, damaged.Count, revs.Count, false, false, what + " Not enough recovery volumes.");

    // Codeword symbol degrees: data volume J at n-1-J, parity i at r-1-i (n = d + r).
    var n = d + r;
    var unknownDegrees = damaged.Select(j => n - 1 - j).Concat(missingRevs.Select(i => r - 1 - i)).ToArray();
    var u = unknownDegrees.Length;
    var m = new byte[u, u];
    for (var row = 0; row < u; ++row)
      for (var k = 0; k < u; ++k) m[row, k] = RarGf8.Pow((row + 1) * unknownDegrees[k]);
    if (!RarGf8.Invert(m)) throw new InvalidDataException("RAR 3 recovery volumes: singular recovery matrix.");

    var length = revs[0].Length;
    var known = new List<(FileStream File, int Degree, long Offset)>();
    foreach (var j in Enumerable.Range(0, d).Except(damaged)) known.Add((File.OpenRead(volume(j)), n - 1 - j, 0));
    foreach (var rv in revs) known.Add((File.OpenRead(rv.Path), r - 1 - rv.Index, 0));
    foreach (var j in damaged) SetAside(volume(j));
    var outs = damaged.Select(j => new FileStream(volume(j), FileMode.Create, FileAccess.ReadWrite, FileShare.None)).ToList();
    try {
      var syn = new byte[u][];
      for (var row = 0; row < u; ++row) syn[row] = new byte[Chunk];
      var data = new byte[Chunk];
      var output = new byte[Chunk];
      for (long c = 0; c < length; c += Chunk) {
        var len = (int)Math.Min(Chunk, length - c);
        foreach (var s in syn) s.AsSpan(0, len).Clear();
        foreach (var (file, degree, _) in known) {
          ReadAt(file, c, data.AsSpan(0, len));
          for (var row = 0; row < u; ++row) {
            var f = RarGf8.Pow((row + 1) * degree);
            var dst = syn[row];
            for (var k = 0; k < len; ++k)
              if (data[k] != 0) dst[k] ^= RarGf8.Mul(data[k], f);
          }
        }
        for (var k = 0; k < damaged.Count; ++k) {
          output.AsSpan(0, len).Clear();
          for (var row = 0; row < u; ++row) {
            var f = m[k, row];
            if (f == 0) continue;
            var src = syn[row];
            for (var t = 0; t < len; ++t)
              if (src[t] != 0) output[t] ^= RarGf8.Mul(src[t], f);
          }
          outs[k].Position = c;
          outs[k].Write(output, 0, len);
        }
      }
      // The 7 bytes rar reserves at the end of a volume are not covered; the end block tells the real length.
      foreach (var o in outs) {
        o.Position = o.Length;
        o.Write(new byte[7]);
        o.Flush();
        if (Rar4End(o) is { } end) o.SetLength(end.Position + end.Size);
      }
    } finally {
      foreach (var k in known) k.File.Dispose();
      foreach (var o in outs) o.Dispose();
    }
    var failed = damaged.Where(j => Rar4End(volume(j)) == null).ToList();
    return failed.Count == 0
      ? new RarRecoveryReport(RarRecoveryKind.Rar3Volumes, false, damaged.Count, revs.Count, true, true, what + " Rebuilt.")
      : new RarRecoveryReport(RarRecoveryKind.Rar3Volumes, false, failed.Count, revs.Count, true, false, what + " Rebuilt volumes do not check out.");
  }
}
