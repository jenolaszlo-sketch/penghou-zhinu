# ZG-RQ-01 acceptance recommendation

## Verdict

**REMEDIATION REQUIRED.**

All technical qualification gates pass with the evidence recorded in this
packet: persistence and schema compatibility (Q1), activity-groups durability
and recovery (Q2, within the qualified selected profiles), legacy binary and
API compatibility (Q3), local package closure and artifact qualification
(Q4), and the local Zhinu/Fuwen paired qualification (Q5). Known findings are
dispositioned per policy (Q7).

Publication must not proceed until the narrowly scoped release-automation
defects found by gate Q6 are repaired and verified:

1. **RG-01 (Zhinu):** add an enforceable exact-commit qualification binding to
   the Zhinu publish workflow — verify the CI conclusion for the exact
   dispatch/tag commit is `success` before any pack/push (and/or publish only
   artifacts produced by the qualifying run, and/or protect the publish job
   behind a required approval environment). Today the gate is a documented
   operator procedure only.
2. **RG-03 (Fuwen):** resolve the native-feed prerequisite — either prepare
   the pinned native candidate feed inside the tag workflow exactly as CI
   does, or (after native publication and indexing) repoint the exclusive
   native-ID source mappings at the public feed and validate the full restore
   path. The workflow currently cannot run at all; this must be fixed and
   validated before any Fuwen tag.
3. **RG-02 (owner decision):** configure required reviewers on the Fuwen
   `nuget` environment or explicitly record that tag creation is the single
   authorization point.

None of these findings implicate the candidate packages or runtime behavior;
they are confined to release automation. The remediation is small and
independently verifiable: after the workflow changes land, re-run the
publisher-adjacent checks (dry validation of the publish path and a
verification run of the changed workflow logic) and re-issue this
qualification with a delta limited to gate Q6 — gates Q1-Q5 stand on the
recorded evidence for as long as the candidate source remains identical
(`f0edf1714013106bdbce090179342ccb029eaf83`, src/tests-identical to the
qualified native baseline `bf33c05`).

## Conditions attached to any future ACCEPTED verdict

- Owner-authorized Zhinu publication only; then public-archive qualification
  (download after indexing, repository-signature-aware hash records, fresh
  caches) before the public runtime may be selected as a dependency.
- Fuwen publication only after the native set is public, indexed and publicly
  qualified, with RG-03 resolved and validated.
- REL-SQLITE-01 stays open; its recorded recurrence response (diagnostic
  retention procedure and the 64-execution bounded Windows stress batch) is
  now due per the recorded policy and should be scheduled as its own work
  item; it does not block publication under the recorded release policy.
- ZG-RQ-01-OB-01 is retained as environmental evidence and handed to the
  Windows-I/O reliability follow-up for consideration.
- No claim in this packet covers the public package pair: public-pair
  qualification is a post-publication step by design.

## Recommended next action

**A narrowly scoped remediation milestone (release automation only)** —
implement and verify RG-01 and RG-03 (and record/resolve RG-02), then re-issue
this qualification for gate Q6. No additional evidence collection is required
for gates Q1-Q5; no runtime, package, or schema changes are needed or
permitted by that remediation.
