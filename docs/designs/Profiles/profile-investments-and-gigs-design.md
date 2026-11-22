# Profile investments and gigs design proposal

## Goal and scope

Give each managed profile one virtual balance and a page showing its cash,
net worth, investments, and gigs. Only the creating account holder can use it; profiles
do not sign in. These are virtual records, not real bank accounts, brokerage
accounts, transfers, or payments. Investments, gigs, and a financial ledger
are not implemented today. See the [managed profiles design](./managed-profiles-design.md)
and [database requirements](../Database/database-models-spec.md).

## Profile experience

Opening a profile from the list leads to `/portal?view=profile&id=<profileId>`,
using the existing `/portal` rewrite. Show the profile name, cash balance,
net worth, separate investment and gig lists, and access to profile editing.
Provide empty, loading, and error states independently for each list; paginate
both. The account holder can:

- Use USD for the balance and record manual deposits and withdrawals. Other
  currencies are out of scope initially; never silently convert currency.
- Create an investment and use a balance transition to fund it from cash or
  another investment. View its value and growth terms, and transfer its full
  value to cash or another investment to close it. Fixed annual growth is
  available first. Market-linked investments are designed here but cannot be
  funded until market pricing is integrated; explain this in the UI.
- Create and edit a gig with a name, optional description, start date, payout,
  and state: planned, in progress, completed, or cancelled. Marking it completed
  credits its payout to cash. Reopening or cancelling a completed gig reverses
  that credit, even if the cash balance becomes negative. A later completion
  makes a new payout.

Display net worth as cash plus the current values of all open investments, in
the profile currency. A market-linked investment has no current value until
pricing is integrated; show its original amount and **net worth unavailable**
rather than presenting an incomplete total as complete. Do not treat the
original amount as a current market value. Block closing an investment when
its current value is unavailable.

## Financial model and API

Keep financial records in the existing Cosmos `managed-profiles` container,
partitioned by `/ownerId`. Each profile references exactly one balance with
its own ID, currency, and available cash. Store investments, gigs, and
immutable ledger entries as balance children; the cash child is represented
by the balance's cash field, not a second balance. Cash is the sum of cash
ledger movements; the stored amount is a transactionally updated projection.
Use decimal amounts in the balance currency, not floating point; validate
precision, bounds and dates. API responses represent USD amounts as decimal
strings; cash adjustments accept decimal strings or JSON numbers. Persist
integer cents within the exact IEEE-754 integer range (maximum absolute cash
or adjustment: 90,071,992,547,409.91 USD). Record timestamps, actor, currency, and source
references on ledger entries. Normal withdrawals and investment funding
cannot exceed available cash; reversing a gig payout is the explicit exception.

Each investment has one funding transfer and only a full withdrawal; an
investment-to-investment transfer closes the source and funds an unfunded
destination. No partial withdrawals or additional contributions are supported.
For fixed-rate investments, let the account holder choose daily, monthly,
quarterly, annual, or simple interest. The annual percentage rate is 0–100%
with at most two decimal places. Compound only completed periods, using 365
days per year for daily and simple interest and calendar periods anchored to
the funding date for the other models. Simple interest accrues for each
completed day. Round the final valuation to USD cents, with halfway values
rounded away from zero.
Persist the terms needed to reproduce historical valuations. A market-linked investment
stores its reference and original amount, not a fabricated price. Provider,
symbol selection, currency conversion, refresh cadence, and stale-price
policy remain open before market valuation is delivered.

Resolve the balance from the profile, then use these owner-scoped routes:

| Method and full path | Purpose |
| --- | --- |
| `GET /api/managed-profiles/{profileId}/balance` | Find the profile's balance. |
| `GET /api/balances/{balanceId}` | Currency, cash, net worth and valuation date. |
| `PUT /api/balances/{balanceId}` | Set currency before financial activity. |
| `GET /api/balances/{balanceId}/cash` | Get the cash child resource. |
| `GET /api/balances/{balanceId}/investments` | List investments. |
| `POST /api/balances/{balanceId}/investments` | Create an unfunded investment. |
| `GET /api/balances/{balanceId}/investments/{investmentId}` | Get an investment. |
| `GET /api/balances/{balanceId}/gigs` | List gigs. |
| `POST /api/balances/{balanceId}/gigs` | Create a gig. |
| `GET /api/balances/{balanceId}/gigs/{gigId}` | Get a gig. |
| `PUT /api/balances/{balanceId}/gigs/{gigId}` | Edit details or change state, including payout or reversal. |
| `GET /api/balances/{balanceId}/ledger` | List ledger entries. |
| `GET /api/balances/{balanceId}/ledger/{entryId}` | Get a ledger entry. |
| `POST /api/balances/{balanceId}/ledger` | Record a manual cash deposit or withdrawal. |
| `POST /api/balances/{balanceId}/transition` | Move value between two eligible children of this balance. |

The transition request identifies source and destination as cash or an
investment ID, plus an amount and relevant versions. Both must belong to the
same balance and be distinct. Cash-to-investment funds an unfunded investment;
investment-to-cash or investment-to-investment moves the source's full
current value and closes it; the server computes that value and rejects an
amount that no longer matches. Reject unavailable valuations, insufficient cash,
already funded destinations, and cross-balance transfers. Record both sides
atomically. Gig completion is **not** a balance transition: changing gig state
credits cash from the virtual gig payout; reversing completion debits cash.

List responses use continuation tokens. Writes require the existing session,
origin and CSRF checks; conditional updates use `If-Match`. Require an
`Idempotency-Key` for financial mutations: same-key retries return the
original outcome, while a different payload conflicts. Retain financial
receipts for the lifetime of the profile so a delayed retry cannot post again;
identify returned balances as snapshots of the original outcome. Atomically update the
balance, affected gig or investments, ledger entries, and retry receipt in one
owner-partition transaction. Creating a completed gig also credits its payout
atomically. A gig state change creates one payout per
completion cycle; reversal references that payout. Concurrent or stale state
changes fail with a conflict instead of posting twice. Completed payouts and
their history cannot be edited or deleted; correct them through a reversal.
The payout and completion details are locked while a gig is completed.
Validate allowed transitions server-side and confirm reversals in the UI.

## Deletion, delivery, and verification

Before enabling financial writes, replace immediate profile deletion with a
durable, retryable deletion workflow. Atomically mark the profile deleting;
every financial write must conditionally update the active profile in the
same transaction, so concurrent deletion cannot leave new records behind.
Block further writes, remove all associated records and receipts in bounded
batches, and report completion only after cleanup. Expose pending/failure
status and retry recovery; update the existing delete API and UI accordingly.
Drain old API versions that bypass this guard before enabling the feature.
Remove the profile-creation receipt too. Consequently, retrying an old profile
creation request after deletion can create a new profile rather than return
`410`; this changes the earlier profile-design behavior.
While the financial feature is incomplete, financial writes are disabled
unless `FinancialFeatures__Enabled` is explicitly set to `true`. Do not
enable it or expose these actions in an environment until deletion guards,
all financial operations, and rollout safety are verified.

Create the balance atomically with each new profile. For existing profiles,
initialize the missing balance idempotently; prompt for its currency before
financial activity and initialize an empty ledger and zero cash safely.
Deploy schema-compatible backend and deletion coordination before exposing
financial actions. Review backup retention and privacy obligations for
children's data; avoid names and financial details in logs.

Test owner isolation, pagination, date and amount validation, compound-growth
rounding, missing market prices, deposits and investment funding/closure.
Test duplicate requests, stale versions, concurrent completion and reversal,
negative balances, and deletion races and recovery. Verify the page and APIs
against development Cosmos, including upgrades of existing profiles.
