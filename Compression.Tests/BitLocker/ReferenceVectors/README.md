# BitLocker reference vectors

`windows-bitlocker.txt` holds data Windows wrote, cut out of cryptsetup's BITLK test images
(`tests/bitlk-images.tar.xz` at cryptsetup revision 086591ce183ef40b8ad6b75a67e7f298bc1767f2,
the archive the "BitLocker oracle" CI step downloads). It lets the required test tier check the
key stretch, the AES-CCM key datums and every sector transform against Windows without the
100 MB images.

- `vmk <protection> <salt> <nonce> <tag> <ciphertext>` — the two wrapped VMKs of
  `bitlk-aes-xts-128.img` (password `anaconda`, recovery password
  `235818-357951-253979-013365-241120-245575-342914-591910` from `images.conf`). Both must
  unwrap, to the same VMK.
- `sector <image> <mode> <fvek> <iv sector> <plaintext sha256> <ciphertext>` — the first
  512 bytes of each volume's relocated volume header (the NTFS boot sector), as stored at the
  volume-header offset, with the FVEK as dm-crypt takes it and the IV sector number it is
  encrypted under. The plaintext SHA-256 is that of cryptsetup's decrypted view of sector 0;
  the images' `SHA256SUM` values in `images.conf` cover the whole view, so the full-image tests
  (`BitlkImageConformanceTests`) vouch for these digests.

The FVEKs were obtained by unlocking the images with their documented passwords;
`cryptsetup bitlkDump --dump-volume-key` prints the same keys.
