# Contributing to AP Stats for OpenWrt

Contributions are welcome — bug fixes, new device/version support, documentation
improvements, and well-scoped features.

## License of Contributions

This project is licensed under the **GNU General Public License v3.0**
(see [`LICENSE`](LICENSE)). Contributions are **inbound = outbound**: by opening a
pull request, you agree that your contribution is offered under the same
**GPL-3.0** terms that govern the rest of the project.

There is **no CLA** (Contributor License Agreement) and **no DCO** sign-off
requirement. You do not need to sign anything or add a `Signed-off-by` trailer —
keeping friction low matters more here than ceremony, especially for drive-by
fixture and documentation contributions.

## How to Contribute

This is a standard fork-and-pull-request flow:

1. **Fork** the repository to your own account.
2. **Create a branch** for your change off `main`.
3. **Make your change**, keeping it focused and self-contained.
4. **Open a pull request** targeting `main`.
5. **CI must pass.** Every pull request runs the full build and the test suite.
   The required status checks that gate a merge to `main` are:
   - the **test + coverage** job — the suite must pass and meet the
     **80% line + 80% branch** coverage gate, and
   - the **smoke** job — the container builds and starts cleanly.

   A pull request cannot be merged into `main` until these required checks are
   green.

For anything non-trivial, please open an issue first so we can agree on the
shape of the change before you invest time in the code.

## Reporting Bugs and Requesting Features

Use the issue forms when you open an issue:

- **Bug report** — include your app version, the OpenWRT version of the affected
  access point, relevant logs, and reproduction steps.
- **Feature request** — describe the use case. Please skim the public
  out-of-scope / vNext list (linked from the form and the project README) first,
  so the request is informed.
- **Questions** are handled in **Discussions**, not the issue tracker.

## Contributing AP / OpenWRT Version Support (Fixtures)

The most valuable contribution for growing device coverage is a **captured set of
SSH fixtures** from a real access point running a version not yet in the support
matrix. This is how the support matrix grows beyond versions tested on the
maintainer's own hardware.

To contribute fixtures, use the **AP-version support** issue form, which walks you
through:

1. Running the bundled `tools/FixtureCapture` tool against your access point.
   It captures the verbatim command outputs into
   `Fixtures/ssh-output/openwrt-<version>/`.
2. **Redacting** the captured output before you attach it — remove real MAC
   addresses, IP addresses, and hostnames following the redaction guidance the
   form points to. **Never attach unredacted captures.**
3. Attaching the redacted fixture set to the issue.

The issue form is the canonical, guided path — it spells out the exact steps and
the redaction policy.

## Code Conventions

Match the surrounding code style and keep changes consistent with the existing
architecture. Tests are expected for behavior changes; the coverage gate is
enforced in CI.

Thank you for contributing.
