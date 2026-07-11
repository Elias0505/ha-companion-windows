# Contributing to HA Companion for Windows

Thanks for your interest! Contributions of code, bug reports, and ideas are welcome.

## Developer Certificate of Origin (DCO) + licensing

This project is licensed under **AGPL-3.0-only**, and the maintainer additionally offers **commercial licenses** to companies that cannot accept the AGPL's copyleft. For that dual-licensing to stay legally clean, the maintainer must be able to relicense the whole codebase — which requires clear rights to every contribution.

By submitting a contribution (a pull request, patch, or commit) to this project, you agree that:

1. You certify the **[Developer Certificate of Origin 1.1](https://developercertificate.org/)** — i.e. you wrote the contribution yourself (or have the right to submit it) and are allowed to submit it under the project's license.
2. You license your contribution under **AGPL-3.0-only**, **and** you grant the project maintainer (Elias0505) a perpetual, worldwide, non-exclusive, royalty-free right to **also** license your contribution under other terms (including commercial/proprietary licenses) as part of this project.

Sign your commits with `git commit -s` — this adds a `Signed-off-by:` line certifying the DCO.

> If you cannot agree to point 2 (the relicensing grant), please open an issue to discuss before sending code.

## How to contribute

1. **Open an issue first** for anything non-trivial, so we can agree on direction before you write code.
2. Fork, create a feature branch.
3. Keep the MVVM separation: no Home Assistant/network logic in views; put reusable, UI-independent logic in `HaCompanion.Core`.
4. Match the existing style (`.editorconfig` is enforced — file-scoped namespaces, `_camelCase` private fields).
5. Make sure `dotnet build` passes (the CI builds on a real Windows runner).
6. `git commit -s` and open a PR describing **what** and **why**.

## Reporting bugs

Open an issue with: your Windows version, Home Assistant version, what you did, what you expected, what happened, and any relevant log output. **Never paste your Home Assistant token** into an issue.
