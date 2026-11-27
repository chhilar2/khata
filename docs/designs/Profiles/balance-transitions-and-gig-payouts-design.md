# Balance transitions and gig payouts design

Proposal; not approved. It details the transition and gig-state parts of
[profile-investments-and-gigs-design.md](./profile-investments-and-gigs-design.md),
which remains authoritative for routes, limits and valuation. Routes,
handlers and Cosmos code for `POST .../transition`, gig create/update and the
deletion guard exist in `BalanceFunctions.cs` and `CosmosBalanceStore.cs`, but
are unverified against live Cosmos and gated by `FinancialFeatures:Enabled`
(default off).

## Goal and scope

Let a parent move value between cash and investments, and change a gig's state
so its payout posts to or reverses from cash, with every write exactly-once,
owner-scoped and safe against concurrent profile deletion.

Excluded: market valuation, partial withdrawals, extra contributions, manual
cash entries, and the write UI.

## Approach

Every mutation is one Cosmos transactional batch in the `/ownerId` partition:

```text
validate -> read profile, balance, targets -> compute -> batch:
  replace profile (If-Match, not deleting)   <- deletion guard
  replace balance (If-Match)                 <- cash projection
  replace/create investment(s) or gig
  create ledger entries
  create receipt (id = idempotency key)
```

If any operation fails, nothing is written.

**Transitions.**
- Cash to investment: the investment must be unfunded and the amount must not
  exceed cash.
- Investment to cash, or to an unfunded investment (deferred until valuation
  exists): the server computes the source's current value, rejects a differing
  amount and closes the source.
- Source and destination must be distinct and belong to this balance.
- Both sides are ledger entries sharing a transfer ID.
- An unavailable valuation is rejected.

**Gig states** are `planned`, `inprogress`, `completed` and `cancelled`.
- Entering `completed` credits the payout and records a payout ledger entry.
- Leaving `completed` (reopen or cancel) debits it with a reversal entry that
  references the payout. This is the only write allowed to overdraw cash.
- Each completion cycle posts one payout.
- While completed, payout and completion details are locked.
- A gig created as completed posts its payout in the creation batch.

## Idempotency and concurrency

- `Idempotency-Key` is a GUID. The receipt stores a request fingerprint and
  the original outcome. The same key and payload replay the outcome (balances
  are marked as snapshots). The same key with a different payload returns a
  conflict.
- Receipts live as long as the profile, so a delayed retry cannot post again.
- `If-Match` ETags on the balance and the affected investment or gig reject
  stale writes with a conflict. The client reloads and confirms again.
- If a batch fails on an ETag, re-read once: if the receipt now exists,
  return it; otherwise return a conflict. Never retry blindly.

## Ownership and deletion guard

- The owner comes from the session. Reads and writes use the owner's
  partition key, and the profile's `BalanceId` must match the route balance.
  Mismatch or deleted profile returns not found, avoiding existence leaks.
- The profile document is replaced in every batch with its ETag. Deletion
  marks the profile `Deleting`, which changes the ETag, so a racing write
  either lands before the mark or fails.
- Deletion removes records and receipts in bounded batches and reports
  completion only after cleanup. Older API versions that bypass the guard must
  be drained before financial writes are enabled.

## Failure handling

| Condition | Result |
| --- | --- |
| Insufficient cash, limit exceeded, invalid state change | 4xx; nothing written |
| Stale version or concurrent change | 409; reload and retry |
| Profile deleting or missing | 404 |
| Batch too large (more than 100 operations) | Not expected; all operations are fixed-size |
| Transient Cosmos failure | 503; retry with the same key |

## Security and privacy

Writes need a session, CSRF token and Origin. Request bodies are size-limited
(4 KB). Amounts are integer cents within the IEEE-754 safe range. Logs must
not include names or financial values. Backup retention for children's data
still needs review.

## Verification

- Unit: each transition type, each gig state edge, and reversal after a
  payout.
- Replay: same key and payload; same key and different payload; a retry
  after the profile's other writes.
- Concurrency: two transitions funding one investment; double completion;
  write racing deletion. Exactly one must win.
- Invariants: cash equals the sum of ledger entries; each completion has one
  payout and at most one reversal.
- Live: run against development Cosmos before enabling the feature flag.

## Assumptions and open decisions

- Decided: receipts keep the full original outcome, not just ledger
  references.
- Decided: a profile may hold at most 10,000 receipts. Creating a mutation
  beyond that limit is rejected (limit exceeded, nothing written) and is
  never evicted, because receipts must outlive delayed retries. The receipt
  count is kept on the balance and updated in the same batch. Replays of
  existing keys still succeed at the limit.
- Assumed that gig reversal may make cash negative. Confirm whether that is
  acceptable or should be blocked.
- Deferred: investment-to-cash and investment-to-investment transitions
  depend on valuation and will be designed after this work. This design
  delivers cash-to-investment funding and gig payouts first.
