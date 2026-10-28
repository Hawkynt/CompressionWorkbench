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
