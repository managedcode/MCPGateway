# Index-build publication race

In scope: repair the confirmed race between background index building and catalog
reconfiguration, cover it through the real MCP catalog, and deliver one patch
release before updating the Storied consumer.

Out of scope: search ranking, public APIs, unrelated local tool changes, and
consumer-side workarounds.

1. Audit graph metadata reads and snapshot publication/retirement.
2. Capture build metadata before publishing or retiring the build's snapshot.
3. Regress a blocked SDK catalog load overtaken by a newer catalog publication.
4. Run owner formatting, build/analyzers, tests, coverage, size and vulnerability
   checks; review the complete repair batch.
5. Increment the canonical patch version once, commit scoped files, push once,
   verify GitHub release and actual NuGet availability, then update Storied.

Validation uses the existing TUnit, format, analyzer and coverage toolchain.

Validation: one confirmed defect in the audited publication/metadata scope. The
new SDK regression passes after the fix and reproduces the consumer's exact
ReaderWriterLockSlim ObjectDisposedException on the original ordering. All 452
tests pass both normally and under coverage (92.46% lines, 80.06% branches).
Formatting, Release build/analyzers, Roslynator, size and vulnerability checks
pass. The complete repair batch uses the single canonical patch bump to 2.3.3
and the existing main-branch Release workflow.
