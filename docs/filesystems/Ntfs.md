# NTFS (`Ntfs`)

NTFS filesystem image with LZNT1 compression and full $MFT system files

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ntfs` |
| Recognised extensions | `.ntfs`, `.img` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4E 54 46 53 20 20 20 20` | 3 | 0.90 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | yes | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | yes | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | yes | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `NtfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### NtfsFormatDescriptor

Descriptor for Microsoft NTFS volume images ("NTFS " boot-sector OEM magic; $MFT-based metadata) with create, in-place modify and defragment support. References:

### NtfsReader

Reads NTFS filesystem images. Parses boot sector, MFT records, attributes ($FILE_NAME, $DATA), and supports both resident and non-resident data extraction with data run decoding.

### NtfsWriter

Builds spec-compliant NTFS filesystem images. All reserved system MFT records (0-15) are populated with real content: $MFT, $MFTMirr, $LogFile, $Volume, $AttrDef, root $., $Bitmap, $Boot, $BadClus, $Secure, $UpCase, and $Extend. Every record carries the mandatory $STANDARD_INFORMATION and $FILE_NAME attributes, the Update Sequence Array (USA) fixup is applied at sector boundaries, and the on-disk cluster bitmap reflects which clusters are actually allocated. Small files (&lt;700 bytes) use a resident $DATA attribute; larger files use non-resident cluster runs.

Images produced by this writer carry all the structure that chkdsk and the Linux ntfs-3g driver check at mount time: volume serial, valid boot signature, every system file has its "FILE" magic, USA fixup at record[510..512] and record[1022..1024], $Volume carries a valid $VOLUME_INFORMATION (version 3.1), the $UpCase data stream is 128 KiB long (65 536 UTF-16 upper-case mappings) and $Bitmap only marks clusters that hold actual filesystem metadata/data.

Large directories: when a directory's $I30 file-name index no longer fits in the resident $INDEX_ROOT inside its MFT record, it spills into a non-resident $INDEX_ALLOCATION (a stream of "INDX" index records, each with its own USA fixup) tracked by a named $BITMAP. The $INDEX_ROOT then holds routing pointer entries (subnode VCN flag 0x01 + 8-byte child VCN at the entry tail) into those INDX leaves, and the FILE_NAME entries live in the leaves sorted by NTFS file-name collation. A single B+tree level is built: the resident root points directly at leaf blocks. To keep all routing pointers resident, the INDX block size is grown (power-of-two, 4 KiB..64 KiB) as the entry count rises. With the default 1024-byte MFT record this handles tens of thousands of short-named entries per directory; only a directory whose routing pointers would overflow even a 64 KiB block (hundreds of thousands of entries) would need a second tree level, which is not yet implemented.

8.3 short names: by default every $FILE_NAME is recorded in the Win32&DOS namespace (3) so the long name also serves as the 8.3 short name, the way a freshly formatted Windows volume does. Passing generateShortNames: false records names in the Win32-only namespace (1) and emits no DOS short name — the equivalent of fsutil behavior set disable8dot3.

### NtfsExtentMap

Walks an NTFS image and yields its actual on-disk byte layout — the boot sector, the $MFT itself (record 0's $DATA runs), the 16 reserved system files (records 0-15), and every regular file's $DATA attribute data runs. Each non-resident $DATA's run-list is decoded; resident $DATA bytes surface as a single MetadataReserved tile inside the MFT record. The 16 reserved system records (e.g. $MFTMirr, $LogFile, $Bitmap, $Boot, $UpCase) are flagged as MetadataReserved.

Streaming: reads only the boot sector + MFT records on demand via a `SectorCache`. A 50 TB NTFS image with a large $MFT keeps memory bounded to ~256 MB regardless of image size.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ClusterSize` | Enum | `Auto` | `Auto`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB` | NTFS allocation unit size. Auto picks the size that minimises slack + MFT-zone overhead. |
| `Compression` | Enum | `Off` | `Off`, `LZNT1` | Stores each non-resident file's $DATA as an NTFS LZNT1 compressed attribute (16-cluster compression units, the 0x0001 compressed flag, sparse runs for saved clusters). Resident files (≤ ~700 bytes) are never compressed. Off stores files uncompressed (default). |
| `Generate8Dot3` | Boolean | `true` | any | Records each $FILE_NAME in the Win32&DOS namespace so the long name doubles as an 8.3 short name (Windows default). Disable to suppress DOS short names (Win32-only names), the equivalent of 'fsutil behavior set disable8dot3'. |
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `16 MB`, `64 MB`, `256 MB`, `1 GB`, `4 GB`, `16 GB` | Total image capacity. Auto sizes the image to exactly hold the files (recommended). |
| `MftRecordSize` | Enum | `Auto` | `Auto`, `512 B`, `1 KB`, `2 KB`, `4 KB` | Size of each $MFT file record. Smaller records pack tighter for many tiny files; larger records keep more attributes resident. Auto co-optimises with cluster size. |
| `NtfsVersion` | Enum | `3.1` | `3.1`, `3.0` | Volume version stamped into $VOLUME_INFORMATION. 3.1 (Windows XP and later) is the modern default; 3.0 marks the volume as a Windows 2000-era NTFS volume. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 32 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://flatcap.github.io/linux-ntfs/ntfs/ — Linux-NTFS project on-disk structure documentation — the de-facto public NTFS spec
- https://github.com/tuxera/ntfs-3g — maintained open-source implementation
- https://learn.microsoft.com/en-us/windows-server/storage/file-server/ntfs-overview — Microsoft's NTFS overview
- https://en.wikipedia.org/wiki/NTFS — Wikipedia article

