# Dropbear & SubnetScanRanges

AP Stats for OpenWrt actively enriches each station with its IP address by
sweeping the AP's local subnet (a batched `arping` pass with a `ping` fallback),
then resolving hostnames by reverse DNS. To do that, it needs to know **which
subnet(s) to sweep** on each AP.

## Why you usually need to set the subnet explicitly

By default the app tries to derive the subnet to sweep from the SSH connection
itself — specifically from the `$SSH_CLIENT` environment variable on the AP.
**Most OpenWRT devices running stock Dropbear do not set `$SSH_CLIENT`** in the
non-interactive command session the app uses. When that variable is unset, the
automatic subnet derivation has nothing to work from, so the sweep cannot guess
the range on its own.

The fix is to declare the range(s) explicitly with `SubnetScanRanges`.

## Setting `SubnetScanRanges`

`SubnetScanRanges` takes a comma-separated list of CIDR ranges to sweep for
station IP/hostname enrichment:

```yaml
    # docker-compose.yml — sweep a single /24 LAN
    environment:
      - SubnetScanRanges=10.0.0.0/24
```

```yaml
    # multiple ranges (e.g. two VLANs your APs serve)
    environment:
      - SubnetScanRanges=10.0.0.0/24,10.0.10.0/24
```

- Provide one or more CIDR ranges. The sweep walks each declared range,
  `arping`s the hosts, falls back to `ping`, and resolves discovered IPs by
  reverse DNS against the AP's configured resolver.
- This pairs with the subnet-sweep probe's CIDR-list behaviour exercised
  end-to-end in the test suite, so a declared list behaves the same in
  production as it does under test.
- IP/hostname enrichment is best-effort: if a station does not answer the sweep
  (powered down, firewalled, on a range you did not declare), its IP/hostname
  fields stay blank — that is a known and accepted limitation, not an error.

## On-router and SBC hosting

If you host the container on an arm64 single-board computer or directly on
router-adjacent hardware inside the same LAN, declaring `SubnetScanRanges` for
the LAN the APs serve is the reliable way to get IP/hostname enrichment working
regardless of whether the AP's Dropbear exports `$SSH_CLIENT`.

See also: [First-boot walkthrough](first-boot.md) ·
[Running behind a reverse proxy](reverse-proxy.md).
