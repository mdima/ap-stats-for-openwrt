# Fixture: openwrt-23.05.5

provenance: SKELETON-PENDING-CAPTURE
captured: NOT YET — Plan 02-04 will populate
captured_by: TBD (operator runs capture tool against a real OpenWRT 23.05.x device)
notes: |
  Skeleton folder created by Plan 02-02 so the test csproj's CopyToOutputDirectory glob has
  something to copy. Plan 02-04 will fill this folder with verbatim captured output and flip
  `provenance:` to `live-capture`. FixturePresenceTests.RealDeviceFixture_Exists is currently
  marked Skip; Plan 02-04 removes the Skip and the test flips green.
