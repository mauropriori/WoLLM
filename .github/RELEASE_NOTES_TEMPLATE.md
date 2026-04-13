# Release Notes Template

## Downloads

- Download only from the official GitHub Release page for `mauropriori/WoLLM`.
- Verify the published `.sha256` checksum before running the Windows or Linux package.

## Windows SmartScreen

`wollm.exe` is currently distributed without an Authenticode signature, so Windows may display Microsoft Defender SmartScreen with `Editore sconosciuto`.

Safe path for users:

1. Download the official `wollm-<version>-win-x64.zip`.
2. Verify `wollm-<version>-win-x64.zip.sha256`.
3. Extract the archive.
4. Run `install-windows-release.ps1` as Administrator from the extracted folder.
5. If SmartScreen appears, choose `Altre info` and then `Esegui comunque` only after confirming the asset and checksum match this release.

## Checksums

- `wollm-<version>-win-x64.zip.sha256`
- `wollm-<version>-linux-x64.tar.gz.sha256`
