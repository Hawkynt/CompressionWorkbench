# MoleBox 2.x file box — on-disk notes

MoleBox (Teggo, later Alexey Sudachen) packs an application and its data files into one
executable. The program's own sections are covered by `MoleboxExecutablePackerHandler`;
this page is about the **file box**, the data files MoleBox 2.x appends to the executable.

No specification was published, and the GPL source of the later open-source MoleBox
(sudachen/Molebox, 2014) writes a different container (SVFS/`STELPACK`). None of the
real 2.x programs measured here carries that container. Everything below was measured on
real MoleBox-packed programs (see *Provenance*) and checked against an independent
extractor's output for the same programs.

## Key

The box is encrypted with the **section key**, the 16 bytes at offset `0x20` of the
section-key blob the loader chain yields (the chain itself is described in
`MoleboxExecutablePackerHandler`). The same blob holds, from offset `0x30`, the
NUL-terminated name the loader gives the box: `<program>.exe.BOX`, `<program>.BOX`, or a
plain name such as `data`.

In every build measured the section-key blob starts `0x32` bytes into the decoded loader
blob, keystreamed with the same constant (`0x3C6EF35F`) seeded with its own address. Where
the configuration record decodes, its key-blob address names exactly that place. Some
builds (the Allok and Ultra Video programs of 2006) carry a configuration record that does
not decode under the loader key; their key blob is read from that fixed place instead, and
the directory's MD5 confirms the key.

The cipher is IDEA in ECB mode, decryption direction (the inverted key schedule of the
section key). Every region below is encrypted from its own first byte over its whole
8-byte blocks; a trailing partial block is stored as is.

## Trailer and box header (end of file)

The last 56 bytes (the directory ends right before them):

| Offset from end | Size | Field |
|---|---|---|
| −56 | 4 | program copy: file offset of its first block (see below) |
| −52 | 4 | program copy: size |
| −48 | 4 | program copy: item flags |
| −44 | 4 | directory length (bytes, multiple of 8) |
| −40 | 4 | directory length again (equal in every sample) |
| −36 | 4 | directory file offset |
| −32 | 4 | length of the name table at the start of the directory |
| −28 | 4 | number of directory records |
| −24 | 4 | **length of the whole file** |
| −20 | 16 | **MD5 of the decrypted directory** |
| −4 | 4 | build marker: `0xCAFEBABE` in most samples, other values in some builds |

The length field and the MD5 identify the box; the marker is not relied on.

The box normally ends the file. Programs built with authoring tools whose player is
MoleBox-packed (Flash-based children's titles, Garden Planner, safey, 2008–2010) carry the
tool's own data after it; there the length field names the end of the box, not of the file,
and the box ends at the last place whose length field names exactly that place.

The program-copy slot names a copy of the packed program's own executable kept inside the
box. Some builds have no slot at all: the header is then the last 44 bytes and the
directory ends right before its length fields (the `zen.exe` sample); where the directory
ends tells the two apart. Builds with the slot that keep no copy leave it zero, so it
counts only when its flags, offset and block table are consistent with the box.

## Directory

Decrypted, the directory is

1. the name table: NUL-terminated names, `\`-separated paths (e.g. `titlescreen\ss_logo.png`);
2. one 16-byte record per file: `u32 name offset` (into the name table), `u32 file offset`,
   `u32 size`, `u32 item flags`;
3. further data (registry and COM registration records in some boxes), not needed to read
   the files;
4. zero padding to a multiple of 8.

## Files

Item flags seen are always `0x33`: bit 0 — blocks are zlib streams, bit 1 — blocks are
encrypted. Bits 4 and 5 are set in every sample and do not change how a file is read.

A file of `size` bytes is cut into `ceil(size / 65536)` blocks of 64 KiB (the last one
shorter). Right before the file offset sits one `u32` per block: the block's stored
length, with bit 31 set when that block is **stored** rather than compressed (MoleBox
stores blocks that do not shrink, such as PNG and Ogg data). The blocks follow back to
back from the file offset. A compressed block is a complete zlib stream (RFC 1950) whose
Adler-32 checks the block; a stored block is the raw bytes. Files sit a few bytes apart.

## Integrity

- directory: MD5 in the trailer;
- compressed blocks: zlib's Adler-32 and the block length (64 KiB, or the rest of the file);
- stored blocks: length only.

## Provenance

Nineteen MoleBox-packed programs from magazine cover discs and ftp.winsite.com (2003–2013),
as indexed by discmaster.textfiles.com (format "MoleBox Packed"), were measured.

- Fifteen of them carry a file box (length field equals the file length).
- Twelve of those open: nine through the configuration record, three (Allok Video to PSP
  Converter, Easy Video to Audio Converter, Ultra Video Splitter, all 2006) through the
  key blob at its fixed place. Every directory passes its MD5 and every block its checks.
- For seven of them (`zen.exe` 2005, `bruceLee.exe` 2008, `Encounter.exe` 2006,
  `Helium_Frog_2_06.exe` 2013 and the three 2006 programs above) the files discmaster's
  extraction lists were fetched and compared: all 19 compared files are byte-identical,
  including a 2 MiB executable mixing compressed and stored blocks. The program copy of
  `Encounter.exe` equals the extractor's unpacked program except for 17 PE-header bytes the
  extractor rewrites.

Seven more programs of 2008–2010 (the newest in the index short of one 348 MiB file) were
measured afterwards. Six carry a box followed by an authoring tool's data (see above) or
end in one (The Bloobles Demo); all six open, every directory passing its MD5 and every
block its checks. discmaster's extractor lists no box files for them, so there is no
reference to compare with. The seventh (StuntPlane, 2009) is wrapped like the two below.

None of the 26 programs carries the SVFS/`STELPACK` container of the later open-source
MoleBox (no catalog or package header at the end, under the default or catalog password),
and a search of archive.org for MoleBox found no program packed with it; that container is
not implemented.

Refused, with what differs:

- Primitive Duplicate Finder (2009), Smart CD Catalog (2008), StuntPlane (2009): a valid box trailer, but
  the executable is wrapped by a further protector (sections `.text`, `.data`, `.rsrc`,
  `.adata`, entry outside the MoleBox stub), so no MoleBox loader blob is reachable in the
  file.
- An Enter the Matrix tool (2004): a valid box trailer without the program-copy slot, but
  an older entry stub (`60 E8 4F 00 00 00`, without the leading `E8 00 00 00 00`) whose
  loader blob does not decode with the keystream and LZSS above at any header position
  from `EP+0x40` to `EP+0x70`, with the entry point shifted by up to 8 bytes either way.
- Three programs of 2003/2004 (Desert Race, IMCast, a UT server tool) with the same older
  stub carry no file box at all; there is nothing to list.
- One further program could not be read on the measuring machine.
