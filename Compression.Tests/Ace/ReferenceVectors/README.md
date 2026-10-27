# ACE reference vectors

| File | Origin |
| --- | --- |
| `cafe.ace` | acefile's test corpus (droe/acefile-testdata, `codepage/ace32-cafe-filename_encoding=cp437_filenamecontains=café.ace`), written by WinAce's `ace32`: one stored member whose CP437 name contains `é`. |
| `winappdbg-head.ace` | the main header and first 35 members of acefile-testdata's `blocked_unregistered/winappdbg-winappdbg_v1.6_plain.ace` (an unregistered WinAce 2.0 solid archive of the BSD-licensed WinAppDbg 1.6 sources, 5.6 MB), cut on a member boundary. Solid members only depend on earlier ones, so the cut is itself a valid archive. ACE 2.0 blocked and stored members, directories, archive comment. |
| `ours-lz77.ace`, `ours-store.ace`, `ours-solid.ace`, `ours-encrypted.ace` | written by our `AceWriter` from the inputs in `AceTests.OursArchive` (fixed timestamps), then read by acefile. The writer must keep reproducing them byte for byte. |
| `acefile-listing.txt` | acefile (master 11ed076a, pure-Python bit stream, `encoding="cp437"`) reading every archive above: name, size, compression type, quality, dictionary bits, directory flag and the SHA-256 of the extracted bytes. `ours-encrypted.ace` was read with password `secret`. |
