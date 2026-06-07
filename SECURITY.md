# Security Policy

## Supported Versions

Security fixes target the latest released version of **AP Stats for OpenWrt**.
Please make sure you are running the most recent published release before
reporting an issue.

## Reporting a Vulnerability

**Please report security issues privately — do not open a public issue for a
security report.**

Use GitHub's built-in private vulnerability reporting:

1. Go to the **Security** tab of this repository.
2. Open **Advisories** → **Report a vulnerability**.
3. Fill in the advisory form with as much detail as you can (affected version,
   reproduction steps, and the impact you observed).

Reports submitted this way stay private between you and the maintainers until a
fix is released, after which the advisory may be published. No email address is
required or published for security intake — the GitHub advisory channel is the
single, private path.

## What to Expect

- We aim to acknowledge a valid report and begin triage promptly.
- If the report is confirmed, we will work on a fix and coordinate disclosure
  through the published advisory once a fixed release is available.
- Reports that turn out to be non-security bugs may be redirected to the public
  issue tracker.

## Scope

This project is designed for deployment on a **trusted LAN segment** alongside
the access points it monitors — it is not intended to be exposed directly to the
public internet. Deployment-hardening guidance and the documented trust boundary
live in the project's threat-model and reverse-proxy documentation. Reports about
the documented trust model (for example, that the encryption keyring sits on the
`/data` volume) are still welcome, but please reference the existing documentation
so we can tell intended boundaries apart from genuine vulnerabilities.
