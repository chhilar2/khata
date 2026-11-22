# Managed profiles design proposal

## Goal and scope

Let a signed-in account holder create and manage one or more profiles for
themselves or people they manage, including dependents. Each profile belongs
to the account holder who created it; only that person can view, edit, or
permanently delete it. A managed profile is not a login and grants no
additional portal access; children do not sign in or use the portal.

A profile is the future home for savings, goals, and gigs, but those features,
real-money accounts, payments, sharing or invitations, and account deletion
are outside this proposal. They are not implemented today. See the
[account design](../Auth/account-page-design.md) and
[database requirements](../Database/database-models-spec.md).

## Account holder experience

From the portal, the account holder sees their managed profiles or an empty
state and can add one with a **required display name** and an **optional
date of birth**. They can open and edit a profile, or confirm permanent
deletion. Make clear that
deletion removes the profile and, once supported, its associated records.
On validation, concurrency, or service errors, show an actionable message
without losing unsaved input. A portal view under `/portal` preserves the
existing Front Door rewrite.

## API

All endpoints use the current account holder's session; the server derives
the owner ID and ignores client-supplied ownership. Responses containing
profile data use `Cache-Control: no-store`. IDs are opaque, randomly
generated, and never an authorization mechanism.

| Method and route | Behavior |
| --- | --- |
| `POST /api/managed-profiles` | Create from `{displayName, dateOfBirth}`; return `201`, `Location`, profile and version. Require an `Idempotency-Key` header. |
| `GET /api/managed-profiles` | Return `{items, continuationToken}` for the current owner; pass the token back to fetch the next page. |
| `GET /api/managed-profiles/{profileId}` | Return one owned profile and its version. |
| `PUT /api/managed-profiles/{profileId}` | Replace editable fields; require `If-Match` with the version. Return the updated profile and version. |
| `DELETE /api/managed-profiles/{profileId}` | Permanently delete after confirmation; require `If-Match`. Return `204` when complete. |

Use field-level `400` errors for invalid input, `401` for missing or expired
sessions, `403` for failed origin/CSRF checks, `404` for missing or non-owned
profiles, and `409` for stale versions or an idempotency key reused with
different input. Treat date of birth as a date, not a timestamp; reject
invalid or future dates. Trim and bound display names. Apply the existing
origin and CSRF protections to all writes. Represent date of birth as
`YYYY-MM-DD` or `null`. Repeating a completed delete returns `404`.

## Data and integration

Create a separate Cosmos `managed-profiles` container partitioned by `/ownerId`.
Store one document per managed profile: `id` (profile ID), `ownerId` (session
user ID), `kind`, `displayName`, optional `dateOfBirth`, `createdAt`, and
`updatedAt`.
Do not expire profiles. List queries are scoped to the owner's partition;
individual reads use `profileId` and that same partition key. Use ETags for
updates and deletes. The existing authentication container remains unchanged.
Provision the container and scoped runtime/developer data-plane permissions
in Bicep; local debugging uses the same development resources.

Create the profile and an idempotency receipt atomically within the owner's
partition. Scope the key to the owner, retain the receipt for 24 hours, and
store only the profile ID and a request fingerprint. A retry with the same input
returns `200` with the current profile and version; different input returns
`409`. If the profile was deleted, return `410` rather than recreate it. The
receipt survives deletion only until its 24-hour expiry to recognize retries;
it contains no profile fields, but its fingerprint is potentially personal
data. Enable per-item TTL for receipts.

Initially there are no associated financial records, so deletion removes the
profile synchronously. Before any later feature attaches records, it must
implement reliable, retryable deletion of **all** of that profile's records,
block new writes during deletion, and report completion only after cleanup.
The profile API must not silently orphan or retain those records. The future
instrument designs must specify their own data model, deletion coordination,
and recovery; do not imply those flows already exist.

## Security and verification

Collect only the details needed; date of birth is optional and is not proof
of age or authority to manage another person's profile. Review retention,
deletion, backups, consent, and privacy obligations before launch, especially
when a profile describes a child. Avoid profile data in logs, URLs beyond
opaque IDs, telemetry, and error messages. Review throughput and partition
limits as usage grows.

Test multiple profiles, empty lists and pagination, validation, duplicate
create retries, editing and stale ETags, permanent deletion and retry behavior.
Verify anonymous access, CSRF, expired sessions, and that another account holder
cannot read, modify, list, or delete a profile even with its ID. Test
provisioning and direct partition-scoped operations against development
Cosmos, and verify portal navigation and refresh through Front Door.
