# Enrichment troubleshooting — station IP & hostname

**AP Stats for OpenWrt** shows two enriched fields per station beyond what `iwinfo`/`iw`
report: the station's **IP address** and its **hostname**. Both are best-effort and are
gathered by the background poller over SSH. This page explains how each is discovered and
what to check when one is blank.

## How IP discovery works

Access points in bridge/AP mode don't route client L3 traffic, so their neighbour (ARP)
table is usually empty for clients. Each enrichment pass therefore:

1. **Provokes ARP** across the target subnet with a batched sweep — `arping` when the AP has
   it, otherwise `ping` (both make the kernel resolve MAC↔IP for hosts that answer).
2. **Reads the kernel neighbour table** (`ip neigh show`) as the source of truth, mapping
   each station MAC to its IPv4. Last-known (`STALE`) entries are kept; `FAILED` and
   IPv6-only entries are ignored.

> As of **v1.0.2** the neighbour-table read is what resolves IPs, so **no extra package is
> required**. The older behaviour picked `arping` vs `ping` up front and the `ping` path
> never actually produced a mapping — so an AP without `arping` (e.g. a fresh OpenWrt 25.12
> install) resolved no IPs at all. Installing `arping` (`apk add iputils-arping` on 25.12's
> `apk`, `opkg install arping` on opkg builds) is still marginally better for ICMP-filtered
> clients, but it is now optional.

### If station IPs don't appear

- **Subnet scanning must be enabled** for the node, and the sweep must target the right
  subnet. If the AP runs stock **Dropbear**, auto-derivation can't run — set an explicit
  `SubnetScanRanges`. See [Dropbear & SubnetScanRanges](dropbear-subnet-note.md).
- The device must actually **use IPv4 on the swept subnet**. An associated client that is
  idle / power-saving, has no DHCP lease, or is IPv6-only leaves no usable ARP entry — there
  is nothing for the AP to report until it sends traffic.
- Enrichment runs on the refresh interval (default hourly), or trigger it with **Run now** in
  the dashboard.

## How hostname discovery works

For each station that has an IP, the poller runs a **reverse-DNS (PTR) lookup on the AP
itself** (`nslookup <ip>`), using that AP's own resolver. A name only comes back if the
resolver the AP queries holds the record — normally your router's dnsmasq, populated from
DHCP leases.

### If hostnames don't appear on one AP (but do on others)

This is almost always a **DNS configuration gap on that AP**, not the app. The most common
cause on a "dumb" AP is dnsmasq's default **`boguspriv`**: it answers *private* reverse
lookups (`192.168.x.x → name`) with `NXDOMAIN` **locally** instead of forwarding them, and a
bridged AP holds no leases — so every client PTR fails.

**Fix — forward the LAN reverse zone to the box that has the leases** (your router). In LuCI:
**Network → DHCP and DNS → General Settings** (in some builds a **Forwards** tab) →
**DNS forwardings**, add (replace `192.168.0.1` with your router / DHCP server):

```
/168.192.in-addr.arpa/192.168.0.1
```

Or from the shell:

```sh
uci add_list dhcp.@dnsmasq[0].server='/168.192.in-addr.arpa/192.168.0.1'
uci commit dhcp
/etc/init.d/dnsmasq restart
```

A domain-specific `server=` forward overrides `boguspriv` for that zone. (Alternatively, set
the AP's upstream DNS to the router **and** untick **Filter private** / clear `boguspriv`.)
The quickest sanity check is to make the failing AP's DHCP/DNS config match an AP whose
hostnames already resolve.

Verify on the AP — `nslookup 192.168.0.<a-known-host>` should return a name; the next
enrichment pass then logs `Reverse-DNS pass … resolved X/Y` with `X > 0`.

### Randomized (private) MACs

Phones and laptops using a **randomized MAC** (the locally-administered bit set — a first
octet ending in `2`, `6`, `A`, or `E`, e.g. `xA:…`) often register no DHCP hostname, so there
may be no PTR to find even when reverse DNS is healthy. Such a station shows its MAC; give it
a friendly label with the per-station **rename** on its detail page.
