# CTW reference vectors

Files written by the CTW 0.1 utility (Context Tree Weighting; Erik Franken and Marcel Peeters,
TU Eindhoven, 2002). They are the test data for `CtwReferenceTests`.

| | |
|---|---|
| Written by | `ctw e -y <options> <input> <output>` with the reference Win32 binary from `ctw-v01_win32.zip` (`www.ele.tue.nl/ctw/download/`, Internet Archive capture of 2006-09-07; `ctw.exe` SHA-256 `8acba769575418f363f4c92270ee87fa99c9ddf7494efd59e92f04c4437ea39d`) |
| Cross-checked | the reference sources (`ctw-v01_source.tar.gz`) built with MinGW-w64 GCC, with the file buffer zero-filled (`calloc` for `malloc`), write identical files for every case. The reference reads one uninitialised buffer byte (see `docs/CTW-ON-DISK.md`); only cases where it was zero are kept. |
| Inputs | deterministic generators (random words, runs, noise), mirrored in the test class and in `make-fixtures.py`; only their size and SHA-256 are kept |
| Checked | the reference `ctw d` returned each input before the file was saved |

`manifest.tsv` lists file, decoded size, SHA-256 of the decoded bytes, the ctw options and the file size.
Regenerate with `python make-fixtures.py <reference ctw.exe> <zero-filled ctw build>`.

| File | What it covers |
|---|---|
| `text-20k.ctw` | the defaults: depth 6, 2^22 nodes, zero-redundancy estimator, strict pruning |
| `text-20k-d2.ctw`, `text-20k-d10.ctw` | shallow and deep trees |
| `text-20k-k.ctw` | the Krichevsky–Trofimov estimator |
| `text-20k-r.ctw` | weighting at the roots |
| `text-20k-s.ctw` | strict pruning off |
| `text-20k-n1k-t2.ctw` | a 1024-node table with 2 probes: searches fail and nodes run out |
| `text-20k-f512.ctw` | a 512-byte file buffer: the trees freeze after 512 bytes |
| `text-20k-b16.ctw` | log-beta bounded to ±16 |
| `runs-60k.ctw`, `noise-3000.ctw` | counts reaching 255 and halving; incompressible input |
| `short-5.ctw`, `one-byte.ctw`, `empty.ctw` | inputs no longer than depth + 1 bytes: only the probability-½ prefix |
