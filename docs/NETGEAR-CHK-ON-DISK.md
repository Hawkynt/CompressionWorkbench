# NETGEAR `.chk` firmware — on-disk layout

The container NETGEAR routers accept as a firmware upgrade. This page is the specification the
`NetgearChk` descriptor implements. It was derived by running OpenWrt firmware-utils `mkchkimg`
(GPL-2.0, <https://github.com/openwrt/firmware-utils>, revision `0179ab1e`) as a black box on
generated inputs and measuring its output; field names follow the header structure the tool's
source declares. No code was taken from it. Captured outputs are checked in under
`Compression.Tests/Firmware/ReferenceVectors/` and the tests reproduce them byte for byte.

## Layout

All integers are big-endian.

| Offset | Size | Field | Measured value / rule |
|-------:|-----:|-------|-----------------------|
| 0 | 4 | magic | `2A 23 24 5E` |
| 4 | 4 | header length | 40 + length of the board id (`0x3A` for an 18-byte id) |
| 8 | 1 | region | `1` worldwide, `2` North America (`-r`) |
| 9 | 7 | reserved | `0x63` (`'c'`) in every byte `mkchkimg` writes |
| 16 | 4 | kernel checksum | checksum of the kernel |
| 20 | 4 | rootfs checksum | checksum of the rootfs; `0` when there is none |
| 24 | 4 | kernel length | bytes |
| 28 | 4 | rootfs length | bytes; `0` when there is none |
| 32 | 4 | image checksum | checksum of kernel followed by rootfs, as one run |
| 36 | 4 | header checksum | checksum of the whole header (board id included) with this field zero |
| 40 | n | board id | ASCII, not NUL-terminated; runs to the header length (`U12H072T00_NETGEAR` by default) |
| 40+n | | kernel | |
| | | rootfs | optional |

Nothing is padded: the kernel starts right after the board id and the rootfs right after the kernel.

## Checksum

A Fletcher-style pair of 32-bit sums over the bytes: for every byte `b`, `c0 += b` then `c1 += c0`
(both wrapping at 2³²). Each sum is folded to 16 bits with end-around carry —
`t = (c & 0xFFFF) + (c >> 16); c = ((t >> 16) + t) & 0xFFFF` — and the checksum is `c1 << 16 | c0`.

Measured: the 77-byte generated input 2 checks to `0xa3fe258d`, the 5000-byte input 1 to
`0xb9b8ba25`, the two in sequence to `0x5b10dfb2`; the header checksum of
`openwrt-mkchkimg-kernel-rootfs.chk` is `0xdf380ffd`. The hypothesis above reproduces all four.
