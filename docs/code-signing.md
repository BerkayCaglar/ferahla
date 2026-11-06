# Code signing policy

Release binaries of Ferahla (`ferahla.exe` attached to GitHub Releases) are built only by the GitHub Actions workflow
[`release.yml`](../.github/workflows/release.yml) from a tagged commit of this repository. No binary is ever built or
uploaded from a developer's machine. Each release lists the SHA-256 of `ferahla.exe` next to it.

When code signing is available (applied for with the [SignPath Foundation](https://signpath.org/) for open-source
projects), signing will happen in that same workflow, and only for builds of tags on the `main` branch.

## Roles

| Role | Who |
|---|---|
| Authors / committers | Maintainers listed on the repository |
| Reviewers | Every change from outside the maintainers is reviewed in a pull request before merging |
| Approvers (release) | The repository owner approves each signing request and publishes each release |

## What is signed

Only `ferahla.exe` built from this repository. Ferahla's single executable contains no third-party binaries; the
libraries it is compiled with are listed in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## Privacy

See [privacy.md](privacy.md): Ferahla makes no network connections and collects no data.
