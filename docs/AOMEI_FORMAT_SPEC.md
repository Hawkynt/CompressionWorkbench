# AOMEI Backup Image (.adi / .afi) — Partial Format Specification

Status: **partial spec recovered from headless Ghidra decompilation**
Vendor: Chengdu AOMEI Tech Co., Ltd. ("AOMEI International Network Limited")
Product: AOMEI Backupper Standard (free)
Binaries analysed (extracted from `AOMEIBackupperStd.exe` 199 MB Inno Setup
installer via `innoextract`):

| Binary | SHA / size | Functions | Role |
|---|---|---|---|
| `ambakdrv.sys` (amd64) | 51,120 bytes | 48 | Kernel block-tracking driver for live snapshot |
| `ambakdrv.sys` (i386)  | 46,896 bytes | 104 | Same, 32-bit |
| `ammntdrv.sys` (amd64) | 171,952 bytes | 251 | Kernel mount driver — parses/reads `.adi` |
| `ImgFile.dll` (amd64)  | 472,288 bytes | 1,159 | User-mode `.adi` reader/writer |
| `Compress.dll` (amd64) | 488,176 bytes | 1,311 | LZ4 + zlib (statically linked) compression |
| `Encrypt.dll` (amd64)  | 540,400 bytes | 1,476 | AES + MD5 + CRC-32 |

Total: **4,349 decompiled functions** processed.

Pipeline used to generate the artefacts: `tools/ghidra-pipeline/decompile.sh`
(Ghidra 11.2.1 headless + Jython post-script).

This document only describes what is verifiably extractable from decompiled
assert messages, vtable wiring and explicit constants. Anything marked **TODO**
needs a real `.adi` sample plus dynamic tracing.

---

## 1. Source-tree leakage (build-path strings)

The MSVC `_ASSERT_EXPR` macro embeds the source path. Every `BR_TEST_SUCCESS`
assert leaked these paths verbatim, giving us the full module layout:

### Kernel driver (`d:\work\br\src\ambakdrv\`)

```
combitmap.cpp    region.cpp       session.cpp      urgmem2.cpp
```

### Kernel mount driver (`d:\work\br\src\imgfile\`)
*(shares headers with the user-mode library)*

```
image.cpp        imagereader.cpp  brfiledriver.cpp imagefile.cpp
imagefileset.cpp imagevolume.cpp  dataconvert.cpp  imgwritecache.cpp
blockcontainer.cpp
```

### User-mode ImgFile.dll (`D:\AMWork\branches\BRCloudv2_QT5\src\ImgFile\`)

```
BlockContainer.cpp  BrFileWin.cpp     DataConvert.cpp     DsImgTask.cpp
FlbDataRegion.cpp   FlbDirEntry.cpp   FlbFileRegion.cpp   FlbImage.cpp
FlbImageReader.cpp  FlbImageWriter.cpp FlbImgTask.cpp     Image.cpp
ImageFile.cpp       ImageFileSet.cpp  ImageReader.cpp     ImageReaderHelp.cpp
ImageVolume.cpp     ImageWriter.cpp   ImageWriterHelp.cpp ImgTaskMgr.cpp
ImgWriteCache.cpp
```

Build product names in embedded PDB paths:

```
E:\BRCloudv2_QT5\output\x64\Release\Compress.pdb
E:\BRCloudv2_QT5\output\x64\Release\Encrypt.pdb
D:\AMWork\branches\BRCloudv2_QT5\output\x64\release_MT\ImgFile.pdb
```

Internal product codename: **BRCloud v2** ("Backup/Restore Cloud, Qt5
front-end"). The `Flb` prefix (`FlbImage`, `FlbDataRegion`, `FlbFileRegion`,
`FlbDirEntry`) marks the **file-level backup** path; `Img` prefix marks the
**block-level disk image** path. Both share the same outer file format.

---

## 2. Top-level file layout

The format is bookended by two fixed-size structs:

```
+---------------------------+   offset 0
| BR_IMAGE_FILE_HEAD (0x65C)|   1628 bytes — magic 'BIFH' (0x48464942 LE)
+---------------------------+
| ... payload ...           |   variable; INFO/INDEX records (see §4)
+---------------------------+
| BR_IMAGE_FILE_TAIL (0x674)|   1652 bytes — magic 'BIFT' (0x54464942 LE)
+---------------------------+   offset = file_size - 0x674
```

Source citation: `imagefile.cpp` (kernel mount driver), assert messages at
lines 0x14A, 0x14B, 0x150 (head check) and 0x164, 0x165, 0x16A (tail check).

### 2.1 `BR_IMAGE_FILE_HEAD` (0x65C bytes, 1628 dec)

Confirmed first 12 bytes from `FUN_00015e90` (`ammntdrv.sys` reader):

| Offset | Size | Field   | Verified value / meaning |
|--------|------|---------|--------------------------|
| 0x000  | 4    | `Flag`  | `'BIFH'` = `0x48464942` LE (Backup Image File Head) |
| 0x004  | 4    | `Size`  | `0x65C` = 1628 (struct size, MUST equal real on-disk length) |
| 0x008  | 4    | `Crc32` | CRC-32 (zlib poly, init 0, final XOR 0xFFFFFFFF) over the entire 0x65C bytes with this field zeroed during computation. **See §5 for algorithm details.** |
| 0x00C  | 0x650 | *body* | TODO — likely backup GUID, format version, BR_IMAGE_INFO descriptors. Field layout is not yet recovered. The same struct is read into class member `m_Head` at object offset +8, and written back via `m_pFile->Write(0, &m_Head, uLen)`. |

Reader pseudo-code (verbatim):
```c
m_pFile->Read(0, &Head, BufLen);        // BufLen = 0x65C
ASSERT(Head.Flag == 'BIFH');            // 'HFIB' little-endian
ASSERT(Head.Size == sizeof(BR_IMAGE_FILE_HEAD));   // 0x65C
saved = Head.Crc32; Head.Crc32 = 0;
ASSERT(Head.Crc32 == BRCrc32(&Head, sizeof(Head))); // compare against `saved`
```

### 2.2 `BR_IMAGE_FILE_TAIL` (0x674 bytes, 1652 dec)

| Offset | Size | Field   | Verified value / meaning |
|--------|------|---------|--------------------------|
| 0x000  | 4    | `Flag`  | `'BIFT'` = `0x54464942` LE (Backup Image File Tail) |
| 0x004  | 4    | `Size`  | `0x674` = 1652 |
| 0x008  | 4    | `Crc32` | Same algorithm as head |
| 0x00C  | 0x668 | *body* | TODO. Likely contains offset/length of the index, total payload size, and a back-pointer to the head for verification. The struct is read from `m_pFile->Read(m_pFile->GetSize() - 0x674, &Tail, 0x674)`. |

The class object stores `m_Head` at `+8` and `m_Tail` at `+0x664` (= 8 + 0x65C),
confirming the two structs are contiguous in memory and that there is no
overlap.

---

## 3. INFO / INDEX type enumeration (recovered from assert strings)

All values are unsigned 16-bit (stored in `BR_STANDARD_HEADER.Type`). The
following names are recovered from decompiled assert text in `ImgFile.dll`:

### INFO records (image-level metadata)

| Name | Value | Size | Notes |
|------|-------|------|-------|
| `INFO_TYPE_IMAGE_COMPRESS` | `0x0105` | 0x18 | `{method:u32, level:u32}` — see §6 |
| `INFO_TYPE_IMAGE_ENCRYPT`  | `0x0106` | 0x18 | `{method:u32, key_len:u32}` — see §7 |
| `INFO_TYPE_IMAGE_PASSWORD` | `0x0107` | 0x20 | `Header(0xC) + MD5(0x10) + pad(0x4)` — see §7 |
| `INFO_TYPE_BACKUP_TYPE`    | `0x010C` | 0x14 | u32 backup type code |
| `INFO_TYPE_IMAGE_COMMENT`  | TODO     | var  | UTF-16 string |
| `INFO_TYPE_IMAGE_SPLIT_SIZE` | TODO   | TODO | Split-file boundary in bytes |
| `INFO_TYPE_BACKUP_TIME`    | TODO     | TODO | Likely FILETIME |
| `INFO_TYPE_BACKUP_OPTION`  | TODO     | TODO | Flags |
| `INFO_TYPE_DISK_INFO`      | TODO     | sizeof(DDM_DISK_INFO_EX) | Per-disk geometry |
| `INFO_TYPE_VOLUME_INFO`    | TODO     | sizeof(DDM_VOLUME_INFO)  | Per-volume metadata |
| `INFO_TYPE_VOLUME_DATA_REGION` | TODO | sizeof(BR_IMAGE_INFO_VOLUME_DATA_REGION) | StartSector + TotalSectors + Blocks |
| `INFO_TYPE_FLB_BACKUP_OPTION` | TODO  | TODO | File-level options |
| `INFO_TYPE_FLB_BACKUP_OPTION_EX` | TODO | TODO | Extended file-level options |
| `INFO_TYPE_FLB_PATH_LIST` | TODO     | var  | Source path list |
| `INFO_TYPE_FLB_SUB_ENTRY_LIST` | TODO | var  | Directory children (`FlbSubEntry[]`) |
| `INFO_TYPE_FLB_FILE_DATA_BLOCK_LIST` | TODO | var | `Fdb[]` data-block descriptors |

Confirmed values (`0x105`, `0x106`, `0x107`, `0x10C`) match the function-offset
arguments seen in `FUN_180014820`, `FUN_1800148d0`, `FUN_180014a30`,
`FUN_180003490`.

### INDEX records (payload-level index)

| Name | Value | Notes |
|------|-------|-------|
| `INDEX_TYPE_ROOT`      | TODO | Top-level index root |
| `INDEX_TYPE_VOLUME`    | TODO | Per-volume entry |
| `INDEX_TYPE_DATAAREA`  | TODO | Data region descriptor |
| `INDEX_TYPE_DATABLOCK` | TODO | Block-level entry — `BR_IMAGE_INDEX_ENTRY_VDB` |
| `INDEX_TYPE_DIRTREE`   | TODO | File-level directory tree |

`BR_IMAGE_INDEX_ENTRY_VDB.EntrySize == sizeof(BR_IMAGE_INDEX_ENTRY_VDB)` — so
this is a tagged-record format with self-describing entry size.

### 3.1 `BR_STANDARD_HEADER`

Asserts like `Region.Header.Type == INFO_TYPE_VOLUME_DATA_REGION` and
`Length >= sizeof(BR_STANDARD_HEADER)` tell us every INFO/INDEX record starts
with a fixed header. Minimum recovered fields (from access patterns
`pHead->Type`, `pHead->Size`, `Head.Crc32`):

| Offset | Size | Field |
|--------|------|-------|
| 0      | 4    | `Size` (total record bytes including header) |
| 4      | 4    | `Type` (one of the `INFO_TYPE_*` / `INDEX_TYPE_*` constants) |
| 8      | 4    | `Crc32` (zlib CRC over the whole record with Crc32 field zeroed) |
| 0x0C   | var  | *body* — type-dependent |

This matches the pattern observed in the HEAD/TAIL structs and is consistent
with the `CheckInfoCrc((BR_STANDARD_HEADER*)pHead)` invariant.

---

## 4. Callback protocol (split files, prompt for next volume)

`ImageReaderHelp` / `ImageWriterHelp` invoke a host callback for cross-volume
operations. Recovered command codes (`CALBAK_CMD_*`):

| Command | Meaning |
|---------|---------|
| `CALBAK_CMD_ASK_FOR_NEW_IMAGE` | Writer needs a new output file (split-image continuation) |
| `CALBAK_CMD_ASK_FOR_OLD_IMAGE` | Reader needs to open an existing prior chunk |
| `CALBAK_CMD_DEAL_WITH_IMAGE`   | Writer is committing a finished chunk |
| `CALBAK_CMD_DISK_FULL`         | Out-of-space signal |

This implies a `.adi` set can be multi-file (split archives) — confirmed by
`INFO_TYPE_IMAGE_SPLIT_SIZE` being a first-class metadata field and by
`ImageFileSet` being a distinct class from `ImageFile`.

---

## 5. CRC-32 algorithm

`BRCrc32` (Encrypt.dll export) decompiled at `0x1800015c0`:

```c
uint BRCrc32(byte *p, uint n) {
    uint c = 0;
    while (n--) c = (c >> 8) ^ TABLE[(*p++ ^ (byte)c) & 0xFF];
    return ~c;
}
```

The table at `DAT_18006e040` is a 256-entry, 4-byte-each polynomial table. The
table's first non-trivial entry can be calculated: this is **standard zlib
CRC-32** (polynomial 0xEDB88320, init 0x00000000, final XOR 0xFFFFFFFF — i.e.
the `crc32()` from RFC 1952). The same implementation is duplicated in
`ammntdrv.sys` at `FUN_0002053c` (identical disassembly) so the kernel and user
paths agree byte-for-byte.

---

## 6. Compression (Compress.dll)

Compress.dll exports a single entry point:

```c
ICompress* CreateCompressObject(void);   // returns vtable-based object
```

ImgFile.dll consumes it via:
```c
m_pCompress = CreateCompressObject();
m_pCompress->Init(method);                       // vtable[0]
m_pCompress->BRCompress(in, in_len, out, &out_len);
m_pCompress->BRUnCompress(in, in_len, out, &out_len);
```

Decompiled internals reveal **two backends**:

### 6.1 LZ4 (raw block format)

`FUN_180008d80` is the small-buffer compressor (≤65547 bytes per chunk). It
implements LZ4 raw block format verbatim:

- 16 KB hash table (`local_4048[8192]` of ushort) — classic LZ4 1.x sliding
  hash with skip-step `((iVar13>>6))` matching the reference encoder.
- Token byte `((literals_len & 0xF) << 4) | (match_len & 0xF)`.
- 0xFF run-length encoding for literals/match overflow.
- Match offset is 2 bytes little-endian after the literals run.
- Match-length minimum 4 (the `param_4 < 0xd` early-out at the start matches
  LZ4's `MFLIMIT == 12`).

### 6.2 zlib `inflate` / `deflate`

`FUN_180001ba0` is a 2 KB+ function with a switch over inflate state
constants 0..0x1F and the literal check `uVar13 == 0x8b1f` (gzip magic in
little-endian byte order). It is a verbatim port of zlib's `inflate.c`
state machine — there are no zlib symbols in `imports.txt` so zlib is
statically linked into `Compress.dll`. The path is selected when
`method >= 0x1000B` in `BRCompress`.

### 6.3 Header

`BR_IMAGE_INFO_COMPRESS` (0x18 bytes total, payload 0xC after the
`BR_STANDARD_HEADER`):

```c
struct BR_IMAGE_INFO_COMPRESS {
    BR_STANDARD_HEADER hdr;   // hdr.Type = 0x105, hdr.Size = 0x18
    uint32_t method;          // 0 = none, 1 = LZ4 (default for small chunks),
                              // ≥0x1000B = zlib path (level encoded in low bits)
    uint32_t level;           // compression level (zlib 0..9, LZ4 ignored)
    // 0x4 trailing pad
};
```

Method numeric mapping is **TODO** — only the threshold `< 0x1000B` is
proven. Treat method codes as an opaque vendor enum until a real .adi sample
can be cross-referenced.

---

## 7. Encryption + password (Encrypt.dll)

Encrypt.dll exports:

```c
IEncrypt* CreateEncryptObject(void);
uint     BRCrc32(byte*, uint);            // see §5
```

`CEncrypt` vtable methods (from `CreateEncryptObject` and ImgFile.dll usage):
- `[0x00]` vtable
- `[0x10]` `SetMethod(uint method)` — set algorithm
- `[0x18]` `SetKey(void* key, uint key_len)` — install key material
- `BREncrypt(in, in_len, out, &out_len)` / `BRDecrypt(...)`

Encrypt.dll does **not** import `bcrypt.dll`, `advapi32!Crypt*`, or
`ncrypt.dll` — the AES implementation is entirely self-contained. No
`CryptAcquireContext` etc. anywhere in the binary. Algorithm identification by
S-box scan was inconclusive in the headless dump (S-box may be split-banked);
this is **TODO** for dynamic confirmation but the call patterns are consistent
with AES-CBC.

### 7.1 Password handling — the MD5 + scheduled-task backdoor

`FUN_180014a30` (= `ImageWriter::AddPassword`) is the password-record writer:

```c
void AddPassword(BYTE* psw, UINT psw_len) {
    if (!psw || psw_len == 0) ASSERT_FAIL("PswBuf&&PswLen");

    BR_IMAGE_INFO_PASSWORD r;
    r.hdr.Size = 0x20;       // record size including 0xC header
    r.hdr.Type = 0x107;      // INFO_TYPE_IMAGE_PASSWORD

    UINT md5_state[4] = {0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476};
    MD5_Update(md5_state, psw, psw_len);
    MD5_Final(md5_state);
    r.md5 = *(UINT128*)&md5_state[0];

    // === Scheduled-task override ===
    if (memcmp(psw, L"AomeiTech.SchduleTask", psw_len) == 0 && task_ctx != NULL) {
        r.md5_low  = *(UINT64*)(task_ctx + 700);    // +0x2BC
        r.md5_high = *(UINT64*)(task_ctx + 0x2C4);  // +0x2C4
    }

    AddImageInfo(INFO_TYPE_IMAGE_PASSWORD, &r, sizeof(r));
}
```

This means:
- The literal UTF-16 string **`"AomeiTech.SchduleTask"`** (sic — "Schdule" is
  misspelled in the binary, confirmed at addresses `180061520`, `18006baa0`,
  `18006bad0`, `18006bb00`, `18006bb30` in ImgFile.dll) triggers a substitution
  of the MD5 hash with values from a runtime context struct.
- Scheduled (unattended) backups use this magic string as the user-visible
  password so that the running service can decrypt without prompting; the
  *actual* key bits come from the scheduler context at offset 0x2BC/0x2C4.
- For interactive backups the password's UTF-16 bytes are MD5'd directly. The
  16-byte MD5 is stored in the file header — comparison happens via
  `IsPswEqual(sPassword, PswLen, ((BR_IMAGE_INFO_PASSWORD*)pInfo)->MD5, 16)`.
- AES key derivation from the MD5 is **TODO** — current evidence shows the
  16-byte MD5 is the input to the key schedule (consistent with AES-128, no
  PBKDF). No iteration count or salt was observed.

### 7.2 INFO_TYPE_IMAGE_ENCRYPT layout

```c
struct BR_IMAGE_INFO_ENCRYPT {
    BR_STANDARD_HEADER hdr;   // hdr.Type = 0x106, hdr.Size = 0x18
    uint32_t method;          // algorithm selector
    uint32_t key_len;         // bits or bytes — TODO disambiguate
    // 0x4 trailing pad
};
```

---

## 8. Block-tracking kernel driver (`ambakdrv.sys`)

This driver is **not** a format parser — it implements a copy-on-write block
tracker for the live-snapshot path. The format I/O happens in user-mode
`ImgFile.dll` and in the **mount driver** (`ammntdrv.sys`), not here.

### 8.1 IRP_MJ_DEVICE_CONTROL surface

Recovered IOCTLs from `ammntdrv.sys` strings + decompiled IRP dispatchers:

| Symbol | Value | Description |
|--------|-------|-------------|
| `IOCTL_AMBAK_GET_BLOCK` | (assert string only — value not yet extracted) | Returns the next dirty block descriptor + cached payload for an in-progress backup. Dispatched in `ambakdrv.sys` only when `IrpSp->MajorFunction == IRP_MJ_DEVICE_CONTROL`. |
| `IOCTL_DISK_GET_DRIVE_GEOMETRY` | `0x002D1080` | Standard Windows IOCTL, used internally by `GetGeometry()` to discover sector size on attached volumes. |

### 8.2 Session lifecycle (from `session.cpp` asserts)

```
STATE_OPENNED    →  BeginBackup({SecPerBit, BmpBuf, BmpLen, CacheBuf, CacheLen,
                                 hMutex, hSemaphore})  →  STATE_FLUSHED
                 →  IOCTL_AMBAK_GET_BLOCK (poll loop) →  STATE_FLUSHED
                 →  EndBackup
```

The driver:
1. Attaches to a target device (`IoAttachDeviceToDeviceStack`).
2. Intercepts `IRP_MJ_WRITE` to mark a bitmap (one bit per N sectors, N =
   `SecPerBit`).
3. When the user-mode service issues `IOCTL_AMBAK_GET_BLOCK`, the driver
   returns the next dirty block from `m_pRegion` (`region.cpp`), backed by a
   ring buffer (`urgmem2.cpp`) of pre-snapshot data preserved via copy-on-write
   on the intercepted writes.

The kernel driver is therefore a **changed-block tracker**, equivalent in
purpose to Microsoft's VSS but implementing its own bitmap + ring buffer.
None of the .adi/.afi file format is parsed in `ambakdrv.sys`.

---

## 9. Mount driver (`ammntdrv.sys`)

This is the kernel-mode mount-as-virtual-disk driver. It re-implements the
ImgFile format parser in kernel mode (so an `.adi` can be exposed as a virtual
block device). The format constants and structures match ImgFile.dll exactly:
the `'BIFH'`/`'BIFT'` magic, the 0x65C / 0x674 head/tail sizes, the same
`BR_STANDARD_HEADER` layout, and the same `BRCrc32` implementation.

The kernel reader (`FUN_00015e90`) is the cleanest reference for the head
parse logic and is what §2.1 above is sourced from.

---

## 10. What is *not* yet recovered

Honest list of remaining gaps that need either a real `.adi` sample or
dynamic tracing to close:

1. **Head/tail body fields** beyond the first 12 bytes (Flag/Size/Crc32). The
   remaining 0x650 / 0x668 bytes are presumably backup GUIDs, version, index
   offset, block size, and a back-pointer from tail to head — but exact field
   layout is **TODO**.
2. **Numeric mapping of `method` codes** for compression and encryption.
3. **AES variant** (CBC vs CTR vs GCM) — call shape is consistent with
   AES-CBC but the IV-handling code wasn't isolated in this pass.
4. **IV / salt derivation** — none observed in decompiled output; possibly
   embedded in the encrypt INFO record body.
5. **INDEX record body layouts** (`INDEX_TYPE_DATABLOCK`, `INDEX_TYPE_DIRTREE`,
   etc.) — only the type-code enumeration is recovered.
6. **`IOCTL_AMBAK_GET_BLOCK` numeric value** — only confirmed by assert text.
7. **`.afi` vs `.adi` difference** — both share the `Flb` (file-level) and
   `Img` (block-level) paths; suspected that `.afi` is file-level only,
   `.adi` is the block-level disk image, but no proof yet.

---

## 11. References (file paths in this PoC)

- Decompiled functions: `~/output/{ambakdrv-amd64,ammntdrv-amd64,imgfile-dll,
  compress-dll,encrypt-dll}/functions/` in WSL home (4,349 .c files total)
- Pipeline: `tools/ghidra-pipeline/decompile.sh` + `dump-functions.py`
- Key cited function offsets in this doc:

| Binary | Address | Role |
|--------|---------|------|
| `ammntdrv.sys`  | `0x00015b20` | `WriteHead` — writes BIFH magic |
| `ammntdrv.sys`  | `0x00015e90` | `ReadHead`  — verifies BIFH + size + CRC |
| `ammntdrv.sys`  | `0x0001601c` | `ReadTail`  — verifies BIFT + size + CRC |
| `ammntdrv.sys`  | `0x0002053c` | `BRCrc32` (inline) |
| `ImgFile.dll`   | `0x180018ef0` | `ReadHead`  — user-mode equivalent |
| `ImgFile.dll`   | `0x180019110` | `ReadTail`  — user-mode equivalent |
| `ImgFile.dll`   | `0x180014820` | `AddImageInfo(ENCRYPT, ...)` — confirms 0x106/0x18 |
| `ImgFile.dll`   | `0x1800148d0` | `AddImageInfo(COMPRESS, ...)` — confirms 0x105/0x18 |
| `ImgFile.dll`   | `0x180014a30` | `AddImageInfo(PASSWORD, ...)` — MD5 + backdoor |
| `ImgFile.dll`   | `0x1800025c0` | `Init(compress_method, encrypt_method)` |
| `Compress.dll`  | `0x1800010c0` | `CreateCompressObject` |
| `Compress.dll`  | `0x180001040` | `CCompress::BRCompress` dispatch |
| `Compress.dll`  | `0x180008d80` | LZ4 raw-block encoder |
| `Compress.dll`  | `0x180001ba0` | zlib `inflate` state machine |
| `Encrypt.dll`   | `0x180001580` | `CreateEncryptObject` |
| `Encrypt.dll`   | `0x1800015c0` | `BRCrc32` (exported) |
