# TrueCrypt legacy-mode reference vectors

`truecrypt43-legacy-sectors.txt` was captured from TrueCrypt 4.3a's own sector encryption. TrueCrypt
4.3a is the last release that wrote CBC, outer-CBC, inner-CBC and 64-bit-block LRW volumes.

Its `Common/Crypto.c` and the `Crypto/` ciphers were compiled standalone on Linux
(`truecrypt-4.3a-source-code.zip` from the `DrWhax/truecrypt-archive` mirror) and driven by
`tc43oracle.c`:

```
gcc -I$TC -I$TC/Common -I$TC/Crypto -D__int8=char -D__int16=short -D__int32=int \
    -D__int64="long long" -DBOOL=int -DTRUE=1 -DFALSE=0 -DMAX_PATH=260 \
    -include stdlib.h -include string.h -fno-strict-aliasing -O1 \
    tc43oracle.c $TC/Common/{Crypto,Crc,Endian,GfMul}.c $TC/Crypto/*.c -o tc43oracle
```

The TrueCrypt sources are under the TrueCrypt License, which this repository cannot take code
from. They were used only as an oracle, and none of their code is in the implementation.

For every encryption algorithm and mode TrueCrypt 4.3a offers, the harness keys a `CRYPTO_INFO` from
a fixed 256-byte key area (`key[i] = i·7 + 1`): IV/LRW secondary key in bytes 0–31, cipher keys
from byte 32. It encrypts two 512-byte sectors (`buf[i] = i·13 + 5`) as sector 77 onwards with
`EncryptSectors`.

Each line holds the TrueCrypt algorithm id, the mode (1 LRW, 2 CBC, 3 outer-CBC, 4 inner-CBC), the
algorithm name, and the 1024 ciphertext bytes in hex.

These vectors exist because cryptsetup's TCRYPT test images keep only the headers of the
Blowfish and CBC-cascade containers, so no other source of their ciphertext was available.
