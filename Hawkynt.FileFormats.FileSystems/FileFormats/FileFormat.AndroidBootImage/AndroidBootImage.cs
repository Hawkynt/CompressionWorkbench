#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FileFormat.AndroidBootImage;

/// <summary>One fragment of a vendor_boot ramdisk table (header v4) or the single vendor ramdisk (v3).</summary>
public sealed class VendorRamdisk {
  public byte[] Data { get; set; } = [];
  public uint Type { get; set; }
  /// <summary>The 32-byte NUL-padded name field, kept raw.</summary>
  public byte[] Name { get; set; } = new byte[AndroidBootImage.VendorRamdiskNameSize];
  public uint[] BoardId { get; set; } = new uint[AndroidBootImage.BoardIdWords];
}

/// <summary>
/// An Android <c>boot.img</c> / <c>recovery.img</c> (magic <c>ANDROID!</c>, header v0–v4) or
/// <c>vendor_boot.img</c> (magic <c>VNDRBOOT</c>, header v3–v4), parsed into its header fields and
/// page-aligned components, and written back the way AOSP <c>mkbootimg</c> lays one out.
/// </summary>
/// <remarks>
/// <para>Layout and field offsets follow AOSP <c>system/tools/mkbootimg</c>
/// (<c>include/bootimg/bootimg.h</c>, <c>mkbootimg.py</c>, <c>unpack_bootimg.py</c>; Apache-2.0),
/// converted. All integers are little-endian.</para>
/// <para>Every header byte the parser does not interpret — the rest of the header page(s), the
/// fixed-size string fields past their NUL — is kept and written back unchanged, so an image
/// that is re-laid out without edits comes back byte for byte. Layouts <c>mkbootimg</c> could not
/// have produced (non-zero padding, a relocated recovery DTBO, a sparse ramdisk table) are read
/// but flagged <see cref="IsCanonical"/> = false, and edits refuse them rather than move bytes.</para>
/// </remarks>
public sealed class AndroidBootImage {

  public const string BootMagic = "ANDROID!";
  public const string VendorBootMagic = "VNDRBOOT";
  public const int BootNameSize = 16;
  public const int BootArgsSize = 512;
  public const int BootExtraArgsSize = 1024;
  public const int BootV3ArgsSize = BootArgsSize + BootExtraArgsSize;
  public const int VendorArgsSize = 2048;
  public const int V3PageSize = 4096;
  public const int VendorRamdiskNameSize = 32;
  public const int BoardIdWords = 16;
  public const int VendorRamdiskEntrySize = 108;
  public const uint VendorRamdiskTypePlatform = 1;

  /// <summary>Header lengths per version, as <c>mkbootimg</c> writes them into the header-size field.</summary>
  public const uint BootV1HeaderSize = 1648, BootV2HeaderSize = 1660, BootV3HeaderSize = 1580, BootV4HeaderSize = 1584;
  public const uint VendorV3HeaderSize = 2112, VendorV4HeaderSize = 2128;

  public bool IsVendor { get; set; }
  public uint HeaderVersion { get; set; }
  /// <summary>Flash page size. Fixed at 4096 for boot v3/v4.</summary>
  public uint PageSize { get; set; } = 2048;

  public uint KernelAddress { get; set; }
  public uint RamdiskAddress { get; set; }
  public uint SecondAddress { get; set; }
  public uint TagsAddress { get; set; }
  public ulong DtbAddress { get; set; }
  /// <summary><c>(os_version &lt;&lt; 11) | os_patch_level</c>, raw.</summary>
  public uint OsVersionPatchLevel { get; set; }
  public byte[] Board { get; set; } = new byte[BootNameSize];
  /// <summary>The command-line field, raw: 512 bytes (boot v0–v2), 1536 (boot v3/v4), 2048 (vendor).</summary>
  public byte[] Cmdline { get; set; } = new byte[BootArgsSize];
  public byte[] ExtraCmdline { get; set; } = new byte[BootExtraArgsSize];
  public byte[] ImageId { get; set; } = new byte[32];
  /// <summary>The header-size field (boot v1+, vendor); 0 where the version has none.</summary>
  public uint HeaderSize { get; set; }
  public uint[] Reserved { get; set; } = new uint[4];
  /// <summary>Header-region bytes after the fields this parser knows, up to the first component.</summary>
  public byte[] HeaderTail { get; set; } = [];

  public byte[] Kernel { get; set; } = [];
  public byte[] Ramdisk { get; set; } = [];
  public byte[] Second { get; set; } = [];
  public byte[] RecoveryDtbo { get; set; } = [];
  public byte[] Dtb { get; set; } = [];
  public byte[] BootSignature { get; set; } = [];
  public byte[] Bootconfig { get; set; } = [];
  public List<VendorRamdisk> VendorRamdisks { get; set; } = [];
  /// <summary>Bytes after the last component: partition padding, an AVB footer, anything appended.</summary>
  public byte[] Trailer { get; set; } = [];

  /// <summary>True when the stored image id equals the SHA-1 <c>mkbootimg</c> computes (boot v0–v2).</summary>
  public bool ImageIdValid { get; private set; }
  public bool IsCanonical { get; private set; } = true;
  public string? NonCanonicalReason { get; private set; }

  /// <summary>True when the trailer ends in an AVB footer (<c>AVBf</c> in the last 64 bytes).</summary>
  public bool HasAvbFooter => this.Trailer.Length >= 64
    && this.Trailer.AsSpan(this.Trailer.Length - 64, 4).SequenceEqual("AVBf"u8);

  public uint EffectivePageSize => !this.IsVendor && this.HeaderVersion >= 3 ? V3PageSize : this.PageSize;

  public int KnownHeaderLength => this.IsVendor
    ? (this.HeaderVersion >= 4 ? (int)VendorV4HeaderSize : (int)VendorV3HeaderSize)
    : this.HeaderVersion switch {
      0 => 1632, 1 => (int)BootV1HeaderSize, 2 => (int)BootV2HeaderSize,
      3 => (int)BootV3HeaderSize, _ => (int)BootV4HeaderSize,
    };

  private int HeaderRegionLength {
    get {
      var page = (int)this.EffectivePageSize;
      var declared = this.IsVendor ? Math.Max((int)this.HeaderSize, this.KnownHeaderLength) : page;
      return Pages(declared, page) * page;
    }
  }

  public static bool IsPowerOfTwoPage(uint page) => page is >= 2048 and <= 262144 && (page & (page - 1)) == 0;

  public static int Pages(long size, int page) => checked((int)((size + page - 1) / page));

  // ── parse ────────────────────────────────────────────────────────────

  public static AndroidBootImage Parse(ReadOnlySpan<byte> data) {
    if (data.Length < 8) throw new InvalidDataException("Android boot image: shorter than the magic.");
    var img = new AndroidBootImage();
    if (data[..8].SequenceEqual("ANDROID!"u8)) img.ParseBoot(data);
    else if (data[..8].SequenceEqual("VNDRBOOT"u8)) img.ParseVendor(data);
    else throw new InvalidDataException("Android boot image: magic is neither ANDROID! nor VNDRBOOT.");
    return img;
  }

  private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
  private static ulong U64(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d[o..]);

  private static void Need(ReadOnlySpan<byte> d, long end, string what) {
    if (end > d.Length)
      throw new InvalidDataException($"Android boot image: {what} runs to byte {end}, past the end of the {d.Length}-byte file.");
  }

  private void ParseBoot(ReadOnlySpan<byte> d) {
    Need(d, 44, "header");
    this.HeaderVersion = U32(d, 40);
    if (this.HeaderVersion > 4) throw new InvalidDataException($"Android boot image: header version {this.HeaderVersion} is unknown.");
    Need(d, this.KnownHeaderLength, "header");

    uint kernelSize, ramdiskSize, secondSize = 0, recoveryDtboSize = 0, dtbSize = 0, signatureSize = 0;
    ulong recoveryDtboOffset = 0;
    if (this.HeaderVersion < 3) {
      kernelSize = U32(d, 8);
      this.KernelAddress = U32(d, 12);
      ramdiskSize = U32(d, 16);
      this.RamdiskAddress = U32(d, 20);
      secondSize = U32(d, 24);
      this.SecondAddress = U32(d, 28);
      this.TagsAddress = U32(d, 32);
      this.PageSize = U32(d, 36);
      if (!IsPowerOfTwoPage(this.PageSize))
        throw new InvalidDataException($"Android boot image: page size {this.PageSize} is not a power of two in 2048..262144.");
      this.OsVersionPatchLevel = U32(d, 44);
      this.Board = d.Slice(48, BootNameSize).ToArray();
      this.Cmdline = d.Slice(64, BootArgsSize).ToArray();
      this.ImageId = d.Slice(576, 32).ToArray();
      this.ExtraCmdline = d.Slice(608, BootExtraArgsSize).ToArray();
      if (this.HeaderVersion >= 1) {
        recoveryDtboSize = U32(d, 1632);
        recoveryDtboOffset = U64(d, 1636);
        this.HeaderSize = U32(d, 1644);
      }
      if (this.HeaderVersion == 2) {
        dtbSize = U32(d, 1648);
        this.DtbAddress = U64(d, 1652);
      }
    } else {
      this.PageSize = V3PageSize;
      kernelSize = U32(d, 8);
      ramdiskSize = U32(d, 12);
      this.OsVersionPatchLevel = U32(d, 16);
      this.HeaderSize = U32(d, 20);
      for (var i = 0; i < 4; ++i) this.Reserved[i] = U32(d, 24 + 4 * i);
      this.Cmdline = d.Slice(44, BootV3ArgsSize).ToArray();
      if (this.HeaderVersion >= 4) signatureSize = U32(d, 1580);
    }

    var page = (int)this.EffectivePageSize;
    var region = this.HeaderRegionLength;
    Need(d, region, "header page");
    this.HeaderTail = d[this.KnownHeaderLength..region].ToArray();

    long offset = region;
    this.Kernel = this.Take(d, ref offset, kernelSize, page, "kernel");
    this.Ramdisk = this.Take(d, ref offset, ramdiskSize, page, "ramdisk");
    if (this.HeaderVersion >= 4 && signatureSize > 0)
      this.BootSignature = this.Take(d, ref offset, signatureSize, page, "boot signature");
    this.Second = this.Take(d, ref offset, secondSize, page, "second-stage bootloader");
    if (recoveryDtboSize > 0) {
      if ((ulong)offset != recoveryDtboOffset) {
        // unpack_bootimg honours the stored offset; so do we, but such an image is not ours to re-lay out.
        this.MarkNonCanonical($"recovery DTBO sits at {recoveryDtboOffset}, not at {offset} where mkbootimg puts it");
        Need(d, (long)recoveryDtboOffset + recoveryDtboSize, "recovery DTBO");
        this.RecoveryDtbo = d.Slice(checked((int)recoveryDtboOffset), (int)recoveryDtboSize).ToArray();
        offset = Math.Max(offset, (long)recoveryDtboOffset + Pages(recoveryDtboSize, page) * (long)page);
      } else
        this.RecoveryDtbo = this.Take(d, ref offset, recoveryDtboSize, page, "recovery DTBO");
    }
    this.Dtb = this.Take(d, ref offset, dtbSize, page, "DTB");
    this.TakeTrailer(d, offset);

    if (this.HeaderVersion < 3) {
      this.ImageIdValid = this.ComputeImageId().AsSpan().SequenceEqual(this.ImageId);
      // mkbootimg leaves a zero offset when there is no recovery DTBO; anything else would be lost on rebuild.
      if (this.HeaderVersion >= 1 && recoveryDtboSize == 0 && recoveryDtboOffset != 0)
        this.MarkNonCanonical("an empty recovery DTBO carries a non-zero offset");
    }
  }

  private void ParseVendor(ReadOnlySpan<byte> d) {
    this.IsVendor = true;
    Need(d, 12, "header");
    this.HeaderVersion = U32(d, 8);
    if (this.HeaderVersion is < 3 or > 4)
      throw new InvalidDataException($"Android vendor_boot image: header version {this.HeaderVersion} is unknown.");
    Need(d, this.KnownHeaderLength, "header");
    this.PageSize = U32(d, 12);
    if (!IsPowerOfTwoPage(this.PageSize))
      throw new InvalidDataException($"Android vendor_boot image: page size {this.PageSize} is not a power of two in 2048..262144.");
    this.KernelAddress = U32(d, 16);
    this.RamdiskAddress = U32(d, 20);
    var ramdiskTotal = U32(d, 24);
    this.Cmdline = d.Slice(28, VendorArgsSize).ToArray();
    this.TagsAddress = U32(d, 2076);
    this.Board = d.Slice(2080, BootNameSize).ToArray();
    this.HeaderSize = U32(d, 2096);
    var dtbSize = U32(d, 2100);
    this.DtbAddress = U64(d, 2104);
    uint tableSize = 0, entryCount = 0, entrySize = 0, bootconfigSize = 0;
    if (this.HeaderVersion >= 4) {
      tableSize = U32(d, 2112);
      entryCount = U32(d, 2116);
      entrySize = U32(d, 2120);
      bootconfigSize = U32(d, 2124);
    }
    if (this.HeaderSize > 1 << 20) throw new InvalidDataException($"Android vendor_boot image: header size {this.HeaderSize} is implausible.");

    var page = (int)this.PageSize;
    var region = this.HeaderRegionLength;
    Need(d, region, "header pages");
    this.HeaderTail = d[this.KnownHeaderLength..region].ToArray();

    long offset = region;
    var ramdiskBase = offset;
    Need(d, offset + ramdiskTotal, "vendor ramdisk");
    var ramdisks = d.Slice((int)offset, (int)ramdiskTotal);
    offset += Pages(ramdiskTotal, page) * (long)page;
    this.CheckPadding(d, ramdiskBase + ramdiskTotal, offset, "vendor ramdisk");
    this.Dtb = this.Take(d, ref offset, dtbSize, page, "DTB");

    if (this.HeaderVersion < 4) {
      this.VendorRamdisks.Add(new VendorRamdisk { Data = ramdisks.ToArray(), Type = VendorRamdiskTypePlatform });
    } else {
      if (entrySize != VendorRamdiskEntrySize && entryCount > 0)
        this.MarkNonCanonical($"ramdisk table entries are {entrySize} bytes, not {VendorRamdiskEntrySize}");
      if ((long)entryCount * entrySize > tableSize || entrySize < VendorRamdiskEntrySize && entryCount > 0)
        throw new InvalidDataException($"Android vendor_boot image: {entryCount} ramdisk table entries of {entrySize} bytes do not fit the {tableSize}-byte table.");
      if (tableSize != entryCount * VendorRamdiskEntrySize)
        this.MarkNonCanonical($"ramdisk table is {tableSize} bytes for {entryCount} entries");
      var tableStart = offset;
      Need(d, tableStart + tableSize, "ramdisk table");
      long expected = 0;
      for (var i = 0; i < entryCount; ++i) {
        var e = d.Slice((int)(tableStart + i * (long)entrySize), VendorRamdiskEntrySize);
        var size = U32(e, 0);
        var rel = U32(e, 4);
        if ((ulong)rel + size > ramdiskTotal)
          throw new InvalidDataException($"Android vendor_boot image: ramdisk table entry {i} runs past the {ramdiskTotal}-byte ramdisk section.");
        if (rel != expected) this.MarkNonCanonical($"ramdisk table entry {i} starts at {rel}, not at {expected}");
        expected = rel + (long)size;
        var boardId = new uint[BoardIdWords];
        for (var w = 0; w < BoardIdWords; ++w) boardId[w] = U32(e, 44 + 4 * w);
        this.VendorRamdisks.Add(new VendorRamdisk {
          Data = ramdisks.Slice((int)rel, (int)size).ToArray(),
          Type = U32(e, 8),
          Name = e.Slice(12, VendorRamdiskNameSize).ToArray(),
          BoardId = boardId,
        });
      }
      if (expected != ramdiskTotal) this.MarkNonCanonical("the ramdisk table does not cover the whole ramdisk section");
      offset = tableStart + Pages(tableSize, page) * (long)page;
      this.CheckPadding(d, tableStart + tableSize, offset, "ramdisk table");
      this.Bootconfig = this.Take(d, ref offset, bootconfigSize, page, "bootconfig");
    }
    this.TakeTrailer(d, offset);
  }

  private byte[] Take(ReadOnlySpan<byte> d, ref long offset, uint size, int page, string what) {
    if (size == 0) return [];
    Need(d, offset + size, what);
    var result = d.Slice((int)offset, (int)size).ToArray();
    var end = offset + Pages(size, page) * (long)page;
    this.CheckPadding(d, offset + size, end, what);
    offset = end;
    return result;
  }

  private void CheckPadding(ReadOnlySpan<byte> d, long from, long to, string what) {
    var end = Math.Min(to, d.Length);
    if (from < end && d[(int)from..(int)end].ContainsAnyExcept((byte)0))
      this.MarkNonCanonical($"the page padding after the {what} is not zero");
  }

  private void TakeTrailer(ReadOnlySpan<byte> d, long offset) {
    // A final component whose page padding was cut off is still complete: the image ends at its data.
    this.Trailer = offset < d.Length ? d[(int)offset..].ToArray() : [];
  }

  private void MarkNonCanonical(string reason) {
    this.IsCanonical = false;
    this.NonCanonicalReason ??= reason;
  }

  // ── image id ─────────────────────────────────────────────────────────

  /// <summary>
  /// The boot v0–v2 image id exactly as <c>mkbootimg</c> computes it: SHA-1 over each component
  /// followed by its 32-bit length (kernel, ramdisk, second, then recovery DTBO from v1 and DTB
  /// from v2), zero-extended to 32 bytes.
  /// </summary>
  public byte[] ComputeImageId() {
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    var size = new byte[4];
    void Add(byte[] part) {
      sha.AppendData(part);
      BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)part.Length);
      sha.AppendData(size);
    }
    Add(this.Kernel);
    Add(this.Ramdisk);
    Add(this.Second);
    if (this.HeaderVersion > 0) Add(this.RecoveryDtbo);
    if (this.HeaderVersion > 1) Add(this.Dtb);
    var id = new byte[32];
    sha.GetHashAndReset().CopyTo(id, 0);
    return id;
  }

  // ── write ────────────────────────────────────────────────────────────

  /// <summary>
  /// Writes the image in <c>mkbootimg</c> order: header page(s), then each component padded to the
  /// page size. A boot v0–v2 image id is recomputed when <paramref name="recomputeImageId"/> is set
  /// (a fresh image, or one whose stored id was valid), else kept as it was.
  /// </summary>
  public void Write(Stream output, bool recomputeImageId) {
    ArgumentNullException.ThrowIfNull(output);
    this.Validate();
    var page = (int)this.EffectivePageSize;
    var region = this.HeaderRegionLength;
    var header = new byte[region];
    if (recomputeImageId && !this.IsVendor && this.HeaderVersion < 3) this.ImageId = this.ComputeImageId();
    if (this.IsVendor) this.WriteVendorHeader(header);
    else this.WriteBootHeader(header, region);
    this.HeaderTail.AsSpan(0, Math.Min(this.HeaderTail.Length, region - this.KnownHeaderLength))
      .CopyTo(header.AsSpan(this.KnownHeaderLength));
    output.Write(header);

    if (this.IsVendor) {
      foreach (var r in this.VendorRamdisks) output.Write(r.Data);
      Pad(output, this.VendorRamdisks.Sum(r => (long)r.Data.Length), page);
      WritePadded(output, this.Dtb, page);
      if (this.HeaderVersion >= 4) {
        var table = new byte[this.VendorRamdisks.Count * VendorRamdiskEntrySize];
        uint rel = 0;
        for (var i = 0; i < this.VendorRamdisks.Count; ++i) {
          var r = this.VendorRamdisks[i];
          var e = table.AsSpan(i * VendorRamdiskEntrySize, VendorRamdiskEntrySize);
          BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)r.Data.Length);
          BinaryPrimitives.WriteUInt32LittleEndian(e[4..], rel);
          BinaryPrimitives.WriteUInt32LittleEndian(e[8..], r.Type);
          r.Name.AsSpan(0, Math.Min(r.Name.Length, VendorRamdiskNameSize)).CopyTo(e[12..]);
          for (var w = 0; w < BoardIdWords; ++w) BinaryPrimitives.WriteUInt32LittleEndian(e[(44 + 4 * w)..], r.BoardId[w]);
          rel += (uint)r.Data.Length;
        }
        output.Write(table);
        Pad(output, table.Length, page);
        WritePadded(output, this.Bootconfig, page);
      }
    } else {
      WritePadded(output, this.Kernel, page);
      WritePadded(output, this.Ramdisk, page);
      if (this.HeaderVersion >= 4) WritePadded(output, this.BootSignature, page);
      WritePadded(output, this.Second, page);
      if (this.HeaderVersion is 1 or 2) WritePadded(output, this.RecoveryDtbo, page);
      if (this.HeaderVersion == 2) WritePadded(output, this.Dtb, page);
    }
    output.Write(this.Trailer);
  }

  private void Validate() {
    if (this.IsVendor ? this.HeaderVersion is < 3 or > 4 : this.HeaderVersion > 4)
      throw new InvalidOperationException($"Android {(this.IsVendor ? "vendor_boot" : "boot")} image: header version {this.HeaderVersion} is not supported.");
    if (!IsPowerOfTwoPage(this.EffectivePageSize))
      throw new InvalidOperationException($"Android boot image: page size {this.EffectivePageSize} is not a power of two in 2048..262144.");
    if (!this.IsVendor) {
      if (this.HeaderVersion >= 3 && this.Second.Length > 0)
        throw new InvalidOperationException("Android boot image: header v3/v4 has no second-stage bootloader.");
      if (this.HeaderVersion is not (1 or 2) && this.RecoveryDtbo.Length > 0)
        throw new InvalidOperationException($"Android boot image: header v{this.HeaderVersion} has no recovery DTBO.");
      if (this.HeaderVersion != 2 && this.Dtb.Length > 0)
        throw new InvalidOperationException($"Android boot image: header v{this.HeaderVersion} has no DTB (only v2, or vendor_boot).");
      if (this.HeaderVersion < 4 && this.BootSignature.Length > 0)
        throw new InvalidOperationException("Android boot image: only header v4 carries a boot signature.");
      if (this.Bootconfig.Length > 0 || this.VendorRamdisks.Count > 0)
        throw new InvalidOperationException("Android boot image: vendor ramdisks and bootconfig belong in a vendor_boot image.");
    } else {
      if (this.Kernel.Length + this.Ramdisk.Length + this.Second.Length + this.RecoveryDtbo.Length + this.BootSignature.Length > 0)
        throw new InvalidOperationException("Android vendor_boot image: kernel, ramdisk, second, recovery DTBO and boot signature belong in a boot image.");
      if (this.HeaderVersion < 4 && (this.VendorRamdisks.Count > 1 || this.Bootconfig.Length > 0))
        throw new InvalidOperationException("Android vendor_boot image: header v3 holds one vendor ramdisk and no bootconfig.");
    }
  }

  private void WriteBootHeader(Span<byte> h, int region) {
    "ANDROID!"u8.CopyTo(h);
    if (this.HeaderVersion < 3) {
      W32(h, 8, (uint)this.Kernel.Length);
      W32(h, 12, this.KernelAddress);
      W32(h, 16, (uint)this.Ramdisk.Length);
      W32(h, 20, this.RamdiskAddress);
      W32(h, 24, (uint)this.Second.Length);
      W32(h, 28, this.SecondAddress);
      W32(h, 32, this.TagsAddress);
      W32(h, 36, this.PageSize);
      W32(h, 40, this.HeaderVersion);
      W32(h, 44, this.OsVersionPatchLevel);
      CopyField(this.Board, h.Slice(48, BootNameSize));
      CopyField(this.Cmdline, h.Slice(64, BootArgsSize));
      CopyField(this.ImageId, h.Slice(576, 32));
      CopyField(this.ExtraCmdline, h.Slice(608, BootExtraArgsSize));
      if (this.HeaderVersion >= 1) {
        var page = (long)this.PageSize;
        W32(h, 1632, (uint)this.RecoveryDtbo.Length);
        var dtboOffset = this.RecoveryDtbo.Length == 0 ? 0UL : (ulong)(region
          + Pages(this.Kernel.Length, (int)page) * page + Pages(this.Ramdisk.Length, (int)page) * page
          + Pages(this.Second.Length, (int)page) * page);
        BinaryPrimitives.WriteUInt64LittleEndian(h[1636..], dtboOffset);
        W32(h, 1644, this.HeaderSize);
      }
      if (this.HeaderVersion == 2) {
        W32(h, 1648, (uint)this.Dtb.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[1652..], this.DtbAddress);
      }
    } else {
      W32(h, 8, (uint)this.Kernel.Length);
      W32(h, 12, (uint)this.Ramdisk.Length);
      W32(h, 16, this.OsVersionPatchLevel);
      W32(h, 20, this.HeaderSize);
      for (var i = 0; i < 4; ++i) W32(h, 24 + 4 * i, this.Reserved[i]);
      W32(h, 40, this.HeaderVersion);
      CopyField(this.Cmdline, h.Slice(44, BootV3ArgsSize));
      if (this.HeaderVersion >= 4) W32(h, 1580, (uint)this.BootSignature.Length);
    }
  }

  private void WriteVendorHeader(Span<byte> h) {
    "VNDRBOOT"u8.CopyTo(h);
    W32(h, 8, this.HeaderVersion);
    W32(h, 12, this.PageSize);
    W32(h, 16, this.KernelAddress);
    W32(h, 20, this.RamdiskAddress);
    W32(h, 24, checked((uint)this.VendorRamdisks.Sum(r => (long)r.Data.Length)));
    CopyField(this.Cmdline, h.Slice(28, VendorArgsSize));
    W32(h, 2076, this.TagsAddress);
    CopyField(this.Board, h.Slice(2080, BootNameSize));
    W32(h, 2096, this.HeaderSize);
    W32(h, 2100, (uint)this.Dtb.Length);
    BinaryPrimitives.WriteUInt64LittleEndian(h[2104..], this.DtbAddress);
    if (this.HeaderVersion >= 4) {
      W32(h, 2112, (uint)(this.VendorRamdisks.Count * VendorRamdiskEntrySize));
      W32(h, 2116, (uint)this.VendorRamdisks.Count);
      W32(h, 2120, VendorRamdiskEntrySize);
      W32(h, 2124, (uint)this.Bootconfig.Length);
    }
  }

  private static void W32(Span<byte> h, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(h[offset..], value);

  private static void CopyField(byte[] value, Span<byte> field) {
    field.Clear();
    value.AsSpan(0, Math.Min(value.Length, field.Length)).CopyTo(field);
  }

  private static void WritePadded(Stream output, byte[] data, int page) {
    output.Write(data);
    Pad(output, data.Length, page);
  }

  private static void Pad(Stream output, long written, int page) {
    var pad = (int)((page - written % page) % page);
    if (pad > 0) output.Write(new byte[pad]);
  }

  // ── os version / patch level ─────────────────────────────────────────

  public static string FormatOsVersion(uint packed) {
    var v = packed >> 11;
    return $"{v >> 14}.{(v >> 7) & 0x7F}.{v & 0x7F}";
  }

  public static string FormatOsPatchLevel(uint packed) {
    var p = packed & 0x7FF;
    return $"{2000 + (p >> 4):D4}-{p & 0xF:D2}";
  }

  /// <summary>Parses <c>a[.b[.c]]</c>, 7 bits per field, as <c>mkbootimg --os_version</c> does.</summary>
  public static bool TryParseOsVersion(string text, out uint version) {
    version = 0;
    var parts = text.Trim().Split('.');
    if (parts.Length is < 1 or > 3) return false;
    uint packed = 0;
    for (var i = 0; i < 3; ++i) {
      uint field = 0;
      if (i < parts.Length && (!uint.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out field) || field > 127))
        return false;
      packed = (packed << 7) | field;
    }
    version = packed;
    return true;
  }

  /// <summary>Parses <c>YYYY-MM[-DD]</c> as <c>mkbootimg --os_patch_level</c> does (7 bits of year past 2000, 4 of month).</summary>
  public static bool TryParseOsPatchLevel(string text, out uint level) {
    level = 0;
    var parts = text.Trim().Split('-');
    if (parts.Length is < 2 or > 3
        || !uint.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var year)
        || !uint.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var month)
        || year is < 2000 or > 2127 || month > 15)
      return false;
    level = ((year - 2000) << 4) | month;
    return true;
  }
}
