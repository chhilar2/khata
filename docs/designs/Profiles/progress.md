# Profile balances, investments and gigs: progress

Status as of 2026-11-27: **in progress; not ready to enable or deploy as a
complete feature**. See the [feature design](./profile-investments-and-gigs-design.md)
and [deployment guidance](../Deployment/code-deployment-design.md).

## Implemented

- Each new managed profile is created atomically with an empty balance;
  existing profiles initialize one on first balance lookup.
- Owner-scoped APIs exist for balance discovery, USD currency setup, cash
  lookup, ledger listing and entry lookup, and manual deposit/withdrawal.
  Cash adjustments use conditional updates, transactional ledger entries and
  idempotency receipts. Net worth is currently reported as unavailable.
- Profile deletion marks the profile, removes associated records in bounded
  batches and can be resumed. The profile creation receipt is removed with
  the profile; retrying the original creation after deletion can recreate it.
  The profile UI handles pending and interrupted deletion.
- Application-level rules cover gig payout/reversal, investment transfers,
  and daily, monthly, quarterly, annual and simple growth. Persistence and
  endpoints remain incomplete and have not been verified for financial use.
- `FinancialFeatures__Enabled` defaults to disabled; currency changes and
  cash adjustments are gated. Do not enable financial writes yet.
- Investment and gig domain records, persisted Cosmos documents, paginated
  owner-scoped reads, creation/update mutations and balance transition hooks
  are now connected to the balance store and API routes. Fixed-rate and
  market-linked investment terms are represented; market-linked funding and
  valuation remain unavailable.
- The profile and investment pages use the selected Story design, with
  paginated investment, gig and ledger reads, independent errors and retries,
  and a link to profile editing and deletion. Current investment values and
  growth are unavailable; no mock chart or financial write controls are shown.

## Remaining work

1. **Partially implemented:** Implement and complete investment and gig persistence and APIs under
   `/api/balances/{balanceId}`, including pagination, versions and validation.
   Keep market-linked pricing deferred as described in the design.
2. **Partially implemented:** Cash-to-investment funding and gig state changes
   post or reverse payouts in one owner-partition batch with the profile
   deletion guard, ledger entry, full-outcome lifetime receipt (limit of
   10,000 per profile) and replay/conflict handling. Investment-to-cash and
   investment-to-investment transitions await valuation; reversal ledger
   entries record the gig as their source but not the original payout entry.
3. Implement net-worth and investment valuations, then complete the profile
   and investment pages with gated financial write, transfer and gig flows.
4. Verify all financial write contracts and rollout compatibility, including
   idempotency, monetary limits and precision, deletion races, stale versions,
   concurrent operations, legacy profile migration and receipt cleanup.
5. Test against development Cosmos and the deployed routing/authentication
   stack; verify real profile workflows and mobile UI. Review privacy, backup
   retention and recovery before release.
6. Before enabling financial writes, block writes and drain older API versions
   that bypass the deletion guard. Deploy and verify the compatible API and UI;
   do not roll back to an unguarded API once financial records exist.

## Last verification

- Frontend production build and mocked browser checks for profile and
  investment navigation, pagination, list retry, value availability and mobile
  layout (2026-11-27); no live Cosmos verification.
- 83 unit tests passed; the .NET solution and frontend builds passed.
- Added investment/gig persistence and API surfaces, profile routing and the
  initial profile balance/investment/gig page.
- Mocked browser checks covered pending deletion and recovery after an
  interrupted deletion.
- The new balance and deletion implementation has **not** been verified
  against live Cosmos or deployed as part of this feature. The previously
  deployed managed-profiles release is separate.
