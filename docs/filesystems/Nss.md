# NSS (Novell Storage Services) (`Nss`)

NSS (Novell Storage Services) — best-effort anchor detection from publicly available reverse-engineered material; object tree contents cannot be reconstructed. WORM emit deferred: NSS's on-disk format was never publicly documented by Novell (now OpenText). The 'Beast' object record layout, the per-volume B-tree node format, and the trustee ACL tree encoding are not described in any vendor or open-source material we have access to. Emitting a pool that NetWare 5+ / OES would recognise would require a real instance to validate, which we don't have. Pinned at read-only with anchor-detection metadata.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.nss` |
| Recognised extensions | `.nss` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4E 53 53 20 50 6F 6F 6C` | 0 | 0.70 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### NssReader

Best-effort NSS image reader. Parses no object tree — only surfaces the anchors NssHeaders located. Because the on-disk layout is proprietary and lacks a verifiable public spec, we never claim to reconstruct files; we expose the located pool/volume/superblock offsets as synthetic entries the user can correlate with the raw image.

## Storage methods

- `stored` — Stored

## Further reading

The implementation cites no sources. Adding a `<list type="bullet">` of them
to the descriptor's doc comment will bring them through to here.

