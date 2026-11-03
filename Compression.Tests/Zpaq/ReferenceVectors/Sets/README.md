# zpaq 7.15 encrypted, multi-part and indexed archives

Every `.zpaq` file here was written by the reference archiver **zpaq v7.15** (Matt Mahoney, public
domain), the Windows x64 build `zpaq64.exe` from <http://mattmahoney.net/dc/zpaq715.zip>
("compiled Aug 17 2016", SHA-256 `7a94b4f1d6323a758c7b0b6344036f166bff0fd44f1c3c86f05b3688023496cb`),
on 2026-11-03. `mkfixtures.py` (documentation, not a build input) generated the inputs
deterministically, ran zpaq, and wrote `manifest.tsv` from `zpaq x <archive> -until N` for every
version: name, size and SHA-256 of each file it extracted.

| Files | How they were made | What they exercise |
|-------|--------------------|--------------------|
| `enc.zpaq` | two `a enc.zpaq v -key "pässwört 1"` runs | AES-256-CTR after the 32-byte salt, scrypt key from the UTF-8 password, two versions |
| `part001.zpaq` .. `part003.zpaq` | three `a "part???" v` runs | one part per update; the archive is their concatenation |
| `ep001.zpaq`, `ep002.zpaq` | two `a "ep???" v -key "pässwört 1"` runs | encrypted parts: only part 1 has the salt, part 2 continues its key stream |
| `ix000.zpaq`, `ix001.zpaq`, `ix002.zpaq` | two `a "ix???" v -index ix000.zpaq` runs | `ix000.zpaq` is the index (transaction headers with no data, fragment tables, file indexes); the parts hold the data |
| `dec1.zpaq`, `dec2.zpaq` | two separate `a decN.zpaq v` runs | unrelated archives whose names look like parts of one set; they must not be joined |

Between versions the inputs change (a file is rewritten with a later date, one is added, one is
deleted), so each version's manifest rows differ.
