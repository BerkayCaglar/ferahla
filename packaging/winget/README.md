# winget manifests

Templates for submitting Ferahla to [winget-pkgs](https://github.com/microsoft/winget-pkgs) as a portable package
(`ferahla` on PATH, no installer). After a release is published:

1. Replace `VERSION` with the release version and `SHA256` with the hash from `ferahla.exe.sha256` (upper case).
2. Validate: `winget validate --manifest packaging/winget` and test: `winget install --manifest packaging/winget`.
3. Copy the three files to `manifests/b/BerkayCaglar/Ferahla/<version>/` in a fork of winget-pkgs and open a PR.
   Later versions can use `wingetcreate update` or Komac.
