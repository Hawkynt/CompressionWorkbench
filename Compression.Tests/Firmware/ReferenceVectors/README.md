# Firmware reference vectors

Small images written by the reference tool of each firmware format, embedded into the test
assembly as `FirmwareVectors.<file name>`.

## vbmeta (Android Verified Boot 2.0)

Written on 2026-10-19 by `avbtool 1.4.0` (`avbtool.py` from
<https://android.googlesource.com/platform/external/avb>, revision
`c5066a96caa7bf4150c0a8cc8cc14ab81733fdc7`) under CPython 3.13, with OpenSSL 3.5.5 doing the RSA
signing, using AOSP's public test key `test/data/testkey_rsa2048.pem` from the same checkout:

```sh
python -c "open('boot.bin','wb').write(b'B'*8192)"
avbtool add_hash_footer --image boot.bin --partition_name boot --partition_size 131072 --algorithm NONE --salt 00112233
avbtool extract_public_key --key testkey_rsa2048.pem --output rsa2048.avbpubkey
avbtool make_vbmeta_image --output signed.vbmeta --algorithm SHA256_RSA2048 --key testkey_rsa2048.pem \
  --include_descriptors_from_image boot.bin --kernel_cmdline "androidboot.cwb=1" --prop "com.cwb.test:yes" \
  --rollback_index 7 --chain_partition system:1:rsa2048.avbpubkey
avbtool make_vbmeta_image --output unsigned.vbmeta --algorithm NONE \
  --include_descriptors_from_image boot.bin --kernel_cmdline "androidboot.cwb=1" --prop "com.cwb.test:yes" --rollback_index 3
```

`avbtool info_image --image signed.vbmeta` reported public key SHA-1
`cdbb77177f731920bbe0a0f94f84d9038ae0617d` and boot hash-descriptor digest
`bf2c275accee5b56c848d7708341b4d2f244b2eebdac087f56c2ad65bbe9cd6c`; the tests assert both.

| File | Bytes | Algorithm | Descriptors |
|------|------:|-----------|-------------|
| `signed.vbmeta` | 2048 | SHA256_RSA2048 | chain partition, property, kernel cmdline, hash |
| `unsigned.vbmeta` | 576 | NONE | property, kernel cmdline, hash |

## TRX (Broadcom)

Written on 2026-10-19 by OpenWrt firmware-utils (<https://github.com/openwrt/firmware-utils>, revision
`0179ab1e367f756ac108a485f26585661d4c528c`, GPL-2.0 — built locally with MinGW-w64 GCC and used only
as a black-box oracle; none of its code is in this repository). The three inputs are generated, so
the tests rebuild them: byte `i` of input `k` is `(i * (31 + 2k) + 7k + 1) & 0xFF`, for inputs of
1001, 5000 and 77 bytes.

```sh
trx -o openwrt-trx-v1.trx -f p0 -f p1 -f p2
otrx create openwrt-otrx-aligned.trx -f p0 -f p1 -a 0x10000 -f p2
```

`otrx check` accepts both. The images show the tools' layout: each input is padded to four bytes,
an `-a` alignment pads the partition before it, and the total length is rounded up to 4 KiB.

| File | Bytes | SHA-256 |
|------|------:|---------|
| `openwrt-trx-v1.trx` | 8192 | `11454da2f56805b463abf20adc818ec4d1e9bb7c2fa0da3987037c4615f8db52` |
| `openwrt-otrx-aligned.trx` | 69632 | `f82e18091d0ab2996a3e0a83c2fed50459ce85554273a324e18c694d7314caac` |

## SEAMA (D-Link)

Written on 2026-10-19 by OpenWrt firmware-utils `seama` 0.20 (same revision and build as above),
from the same generated inputs (`kernel.bin` = input 1, `rootfs.bin` = input 2):

```sh
seama -i kernel.bin -m dev=/dev/mtdblock/1 -m type=firmware        # -> openwrt-seama-entity.seama
seama -i rootfs.bin -m dev=/dev/mtdblock/2 -m type=rootfs          # -> rootfs.seama
seama -s openwrt-seama-seal.seama -m signature=wrgac01_dlink.2013gui_dir850 \
      -i openwrt-seama-entity.seama -i rootfs.seama
```

`oseama info` lists the seal's two entities; `md5sum kernel.bin` is
`e608e0f186f57bb08498b3ba3eb5b271`, the MD5 stored in the entity header.

| File | Bytes | SHA-256 |
|------|------:|---------|
| `openwrt-seama-entity.seama` | 5064 | `6c199ea38ed43122e70fea38206ede9c5ad8043921068f4325de94e4bcdb3379` |
| `openwrt-seama-seal.seama` | 5253 | `1fd9c0ca4276a2dd730306a3eb76221e3ef5b1d7dd09a08d9fa2af64e9729443` |

## CHK (NETGEAR)

Written on 2026-10-19 by OpenWrt firmware-utils `mkchkimg` v0.1 (same revision and build as above),
kernel = input 1, rootfs = input 2:

```sh
mkchkimg -o openwrt-mkchkimg-kernel.chk        -k p1       -b U12H139T00_NETGEAR -r 1
mkchkimg -o openwrt-mkchkimg-kernel-rootfs.chk -k p1 -f p2 -b U12H139T00_NETGEAR -r 2
```

The tool reported kernel checksum `0xb9b8ba25`, rootfs checksum `0xa3fe258d` and image checksum
`0x5b10dfb2` (kernel and rootfs); the tests assert the same values.

| File | Bytes | SHA-256 |
|------|------:|---------|
| `openwrt-mkchkimg-kernel.chk` | 5058 | `febe1c7bbb8224582a1603430cb850986f4aa61eb073922cbbb84d72a4512994` |
| `openwrt-mkchkimg-kernel-rootfs.chk` | 5135 | `46e1e2e4eabda4771e71af0e61ab7646b0a6d76b8204df63df6b2b76c62c273a` |

## Linux kernel reference samples

Set `CWB_KERNEL_SAMPLES` to a directory containing the following filenames to run
the real-image cases in `LinuxKernelImageTests`.

| Local filename | Source | SHA-256 |
| --- | --- | --- |
| `tc-vmlinuz` | [TinyCore 15.x x86 vmlinuz](http://tinycorelinux.net/15.x/x86/release/distribution_files/vmlinuz) | `364eba4da3b791c0a5e2e32fadc4106fe0955638ad6ede07886b2ee887bf5c80` |
| `owrt-armv7-kernel.bin` | [OpenWrt 23.05.5 ARMv7 generic kernel](https://downloads.openwrt.org/releases/23.05.5/targets/armsr/armv7/openwrt-23.05.5-armsr-armv7-generic-kernel.bin) | `b87d5c028a2ae8b4f6c46584a5e7dc8520176ca615af046cd22aefb27728026b` |
| `owrt-armv8-kernel.bin` | [OpenWrt 23.05.5 ARMv8 generic kernel](https://downloads.openwrt.org/releases/23.05.5/targets/armsr/armv8/openwrt-23.05.5-armsr-armv8-generic-kernel.bin) | `89b6ebd23c971afa069889974ad9b4186332cdbde962312cc0e7d6599232760f` |

The OpenWrt image hashes match the release indices. The ARMv7 image contains an XZ
stream with the ARM BCJ filter at byte 55,942. CPython 3.13.1 `lzma` decodes it to
13,189,772 bytes with SHA-256
`ec361666d28405aa3bf1361fc67b47cdc2199e83096550622d633983ed721e43`;
the test compares the complete extracted payload with this digest.
