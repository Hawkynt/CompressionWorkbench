# TE reference vectors

Real Terse Executable images, carved unmodified out of EDK2 firmware that Debian builds and ships.

| File | Origin |
| --- | --- |
| `aavmf-aarch64-CpuPei.te` | `CpuPei` PEIM of `usr/share/qemu-efi-aarch64/QEMU_EFI.fd` (sha256 `86493b7d…0d69`) from Debian's `qemu-efi-aarch64_2025.02-8+deb13u1_all.deb`; ARM64, 3 sections. |
| `aavmf-aarch64-PlatformPei.te` | `PlatformPei` PEIM of the same firmware volume. |
| `aavmf-arm-CpuPei.te` | `CpuPei` PEIM of `usr/share/AAVMF/AAVMF32_CODE.fd` (sha256 `e2e307d0…dfea`) from Debian's `qemu-efi-arm_2025.02-8+deb13u1_all.deb`; ARM Thumb-2. |
| `7zip-listing.txt` | 7-Zip 26.04's view of each image (`7z l -slt -tTE`, then `7z e -so` per entry): `file entry size packed-size offset sha256`. |

The images were carved with 7-Zip's UEFI firmware-volume handler (`7z x QEMU_EFI.fd`, the TE section
of each FFS file comes out as `1.te`).

EDK2 is © TianoCore contributors, distributed under the BSD-2-Clause-Patent licence
(<https://github.com/tianocore/edk2/blob/master/License.txt>).
