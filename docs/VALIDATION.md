# DiskPeek 0.1.0 validation

Validated on Windows x64 (10.0.26300) with .NET 10.0.14. These are local
observations, not speed guarantees for other drives or comparisons with WizTree
or WinDirStat.

Checked-in JSON reports retain the original measurements, with local workspace
paths and machine names replaced by reproducible fixture labels. CI produces
fresh reports as downloadable artifacts. Embedded debug source paths use the
neutral `/_/DiskPeek/` prefix.

## Automated coverage

Release builds complete with zero warnings and zero errors. The standalone
verification runner reports **25 passed, 0 failed, 0 skipped**. Coverage includes
out-of-order aggregation, hard-link attribution, unknown allocation, parent
cycles, Unicode CSV, valid/malformed MFT records, 512-byte fixups on 4Kn layouts,
fragmented/sparse runlists, attribute references, a malformed-input corpus,
aligned noncached I/O, bounded worker frames, long paths, real hard links,
sparse/compressed data, alternate streams, junction loops, denied ACLs, locked
content, cancellation, and concurrent filesystem changes.

The aligned-I/O regression uses an ordinary workspace file opened without
caching; it does not claim a live volume scan. ACL fixtures restore their original
access rules after verification. Fixture data remains under `work`.

## Directory scanning benchmark

The standard fixture contains 16,384 files of 4,096 bytes in 64 directories:
16,449 entries including the root, 64 MiB logical and allocated main-stream data.
Both engines produced matching entry counts and size/coverage totals.

Five warm-cache runs, alternating engine order and starting a fresh process for
each sample, measured these medians outside the command sandbox:

| Engine | Median wall time |
| --- | ---: |
| Straightforward managed walker with per-entry metadata handles | 553.7 ms |
| Batched directory scanner | 40.8 ms |

The observed ratio is **13.56× on this fixture**. Earlier sandboxed measurements
were excluded because per-file sandbox checks affect the two approaches
differently. No OS cache flush was performed. HDD, other SSD, removable, SMB,
and rigorous cold-cache comparisons remain unmeasured.

A separate 1 ms cancellation request completed in 7.13 ms and retained partial
results. Cancellation latency varies with outstanding native I/O.
`FirstMetadataMilliseconds` in the JSON means the first qualifying progress
callback, not the first rendered tree row.

## Million-file model and WPF checks

The compact-model benchmark measured 158.3 ms aggregation, 9.83 ms substring
search, 7.20 ms top-1,000 selection, and about 156 MB retained managed memory after
collection. It does not perform filesystem I/O.

The actual self-contained WPF app was also exercised with one million synthetic
files. Its report passed: 7 realized table rows initially and 6 after scrolling
to the last row, 4,097 bounded treemap tiles, valid geometry, and 1,000 largest
files. Initial projection/map took about 2.27 seconds, scrolling to the last row
44 ms, wildcard search 752 ms, and largest-file selection 62 ms. Maximum observed
dispatcher delay was 55 ms; peak working set was about 465 MiB. This includes the
GUI, native renderer, result arrays, projection, and transient allocations.

The packaged app was visually inspected in dark and light themes, scanned the
real fixture, successfully filtered it using `*.json` and the `.log` type selector,
and zoomed a grouped treemap folder to its correct 256-file/1-MiB scope. CLI export of that
fixture completed from the self-contained package without an installed runtime.

## User confirmation and follow-up behavior

The user subsequently confirmed that the fast scan worked. This is a
user-confirmed successful fast path, separate from the agent-run automated
coverage and performance measurements above; no elevated timing is inferred.

Fast NTFS scanning is enabled by default for supported local NTFS drive roots.
The app still starts idle: selecting Scan starts the worker and its administrator
prompt. Folders, non-NTFS drives, and network paths use the directory engine
automatically, without an elevation attempt or an unsupported-target warning.
Declined elevation or worker errors retain the directory fallback and its reason.
Hovering a file tile highlights visible files with the same immediate parent
folder and accents that folder in the tree. A short hover reveals offscreen or
collapsed ancestor branches, preserving the selected file and current filters.
The hover accent clears when the pointer leaves the map. Largest files remains
a file-only list and is not switched automatically by hovering.

The follow-up build was visually checked with fast scan selected by default:
a folder scan used Directory without an elevation prompt or fallback warning.
Hovering a file outlined and tinted its folder's tiles; moving outside the map
cleared the group highlight while retaining the selected file.

The tree-link follow-up builds successfully and was reviewed for selection,
filter preservation, virtualized-row recycling, and scan replacement. Its live
visual verification was interrupted when the user stopped Computer Use with
Escape, so that interaction is not recorded as visually verified here.

Treemap double-click activation now targets the highlighted folder group for
both file tiles and condensed folder tiles. Enter in the treemap uses the same
folder target. Activating the current root group leaves the current scope alone.
This follow-up was build-checked; no additional desktop interaction was used.

## Explicit verification limits

- The session token is not an administrator and cannot open a local raw volume.
  Automated end-to-end live elevated MFT scanning has **not** been verified by
  the agent. The user's successful fast-scan confirmation is recorded separately
  above. Declined/cancelled UAC and elevated-process failure recovery remain
  unverified end to end; directory fallback is available.
- Provider-backed cloud-placeholder behavior requires a configured provider
  fixture. Reparse/offline entries are conservatively left unmeasured and file
  contents are not opened for sizing.
- Live-volume scans are not atomic snapshots; file changes can create partial
  observations. Alternate streams and filesystem overhead remain outside the
  default file totals.
- The map shows the full selected folder; search/type filters apply to table
  results. Grouped folder tiles limit draw work and can be opened by zooming.

Raw machine-readable reports accompany the release: `verification-results.json`,
`benchmark-warm.json`, `benchmark-synthetic.json`, and `benchmark-ui.json`.
Reproduce with `scripts/build.ps1`, `scripts/benchmark.ps1`, and the app's explicit
`--ui-benchmark` mode described in README.md.
