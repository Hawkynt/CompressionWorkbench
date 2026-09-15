# Exporting independent GPFS evidence corpora

IBM/StorageScaleVagrant configures `../setup` at `/vagrant` with Vagrant's
`rsync` synced-folder implementation. That transport is host-to-guest only. A
corpus written under `/var/tmp` therefore has to be exported explicitly before
destroying the VM to create an independently formatted filesystem.

After `run-controlled-corpus.sh` completes inside `m1`, run on the host:

```bash
./tools/gpfs-lab/export-corpus.sh \
  /path/to/.gpfs-lab/StorageScaleVagrant \
  libvirt \
  /var/tmp/cw-gpfs-corpus \
  /path/to/gpfs-evidence/corpus-a
```

The destination must be empty. `export-corpus.sh` obtains the exact SSH identity,
host and forwarded port from `vagrant ssh-config m1`, pulls the corpus with
`rsync --sparse` through remote `sudo rsync`, and runs `verify-corpora.sh` on the
host copy before returning success. The raw NSD image contents and their
SHA-256-labelled names are unchanged; sparse zero ranges remain sparse in the
host files.

For the second promotion corpus, destroy the first VM only **after** that export
passes. Provision a new lab directory so provider-specific backing disk files
cannot be accidentally reused:

```bash
cd /path/to/.gpfs-lab/StorageScaleVagrant/libvirt
vagrant destroy -f

./tools/gpfs-lab/provision-vagrant.sh \
  /path/to/Storage_Scale_Developer-6.0.1.0-x86_64-Linux-install \
  libvirt \
  /path/to/.gpfs-lab/StorageScaleVagrant-b
```

Run the same controlled corpus in the new `m1`, export it to `corpus-b`, then
verify both host copies together:

```bash
./tools/gpfs-lab/verify-corpora.sh \
  /path/to/gpfs-evidence/corpus-a \
  /path/to/gpfs-evidence/corpus-b
```

The verifier rejects identical filesystem UIDs. Do not work around that check: a
new corpus ID on the same formatted filesystem is not independent evidence.

Once both exports pass, point the optional repository acceptance tests at them:

```bash
CWB_GPFS_CORPUS_A=/path/to/gpfs-evidence/corpus-a \
CWB_GPFS_CORPUS_B=/path/to/gpfs-evidence/corpus-b \
  dotnet test Compression.Tests --filter 'TestCategory=ExternalFsInterop'
```

For a disposable Linux lab host, the complete A/B workflow can instead be run as
one fail-closed command:

```bash
./tools/gpfs-lab/run-two-corpora.sh \
  /path/to/Storage_Scale_Developer-6.0.1.0-x86_64-Linux-install \
  libvirt \
  /path/to/new-empty-gpfs-work-root
```

`run-two-corpora.sh` requires a new or empty work root. It provisions lab A, runs
and exports the full controlled corpus, destroys and removes lab A, provisions a
separate lab B checkout/backing-disk set, runs and exports corpus B, verifies both
filesystem UIDs are independent, and finally runs only
`Compression.Tests.Gpfs.GpfsCorpusExternalTests` with both corpus environment
variables set. Lab B is deliberately left provisioned for the later mutation,
remount and `mmfsckx` gate. The script never downloads the IBM installer.

The large corpus directories stay outside Git. Only mechanically extracted,
provenance-labelled byte ranges should ever be considered for small checked-in
test vectors after the clean-room derivation has identified what they prove.
