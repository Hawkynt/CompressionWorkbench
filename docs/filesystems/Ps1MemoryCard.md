# PlayStation Memory Card (`Ps1MemoryCard`)

Sony PlayStation 128 KiB memory card and bank-switched multi-card image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.mcr` |
| Recognised extensions | `.mcr`, `.mcd`, `.mem`, `.psm` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4D 43` | 0 | 0.45 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | yes | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### Ps1MemoryCardFormatDescriptor

Sony PlayStation memory-card filesystem. One hardware-visible card bank is always the canonical 128 KiB layout (one metadata block plus fifteen 8 KiB save blocks). Larger third-party cards from the PS1 era are represented as bank-switched collections of independent canonical banks; no enlarged fictional allocation table is invented.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Banks` | Enum | `Auto` | `Auto`, `1`, `2`, `4`, `8`, `16`, `32`, `64` | Number of independent 128 KiB PS1 card banks. Auto chooses the smallest historical power-of-two bank count that fits. |

## Storage methods

- `stored` — 8 KiB save blocks

## Further reading

The implementation cites no sources. Adding a `<list type="bullet">` of them
to the descriptor's doc comment will bring them through to here.

