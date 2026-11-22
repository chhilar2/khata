# Parent account page design proposal

**Status:** Implemented; live Google and Cosmos verification is still needed.

## Goal and scope

Give a signed-in parent a simple account page showing their sign-in details
and allowing them to provide a display name, contact email, phone, country,
and preferred currency. Create a corresponding Cosmos DB profile at first
successful sign-in. These fields are optional; the page is not for children.
Account deletion, email or phone verification, and financial accounts are
outside this proposal.

Previously sign-in stored an internal user ID and a Google identity mapping,
but not name or email. See the [login design](./user-login-design.md) and
[database requirements](../Database/database-models-spec.md).

## User flow

1. A parent signs in and opens **Account** from the portal. Show their sign-in
   provider and email as read-only, separately from their editable contact email.
   Show an empty state for information not yet supplied.
2. The parent edits or clears optional profile fields and saves. Validate
   input, show field-specific errors and confirm success; preserve edits if
   saving fails. An expired session returns the parent to sign-in.
3. Use a portal view such as `/portal?view=account`, so the existing Front
   Door `/portal` rewrite continues to serve the app.

## Data and components

Keep the existing identity mapping and session model in the `authentication`
container and add a separate profile document in the same container. The
container retains its existing `/id` partition-key path. Set both the
profile's `id` and `partitionKey` property to `profile-{userId}`; the latter
is descriptive and is not the Cosmos partition-key path. A profile is read
directly using that value for both the document ID and partition key. The
illustrative logical shape is:

```json
{
  "id": "profile-{userId}",
  "partitionKey": "profile-{userId}",
  "kind": "profile",
  "userId": "{userId}",
  "displayName": null,
  "provider": "google",
  "email": null,
  "contactEmail": null,
  "phone": null,
  "country": null,
  "currency": null,
  "createdAt": "2026-01-01T00:00:00Z",
  "updatedAt": "2026-01-01T00:00:00Z"
}
```

The timestamps represent profile creation and last update, not necessarily
the original sign-up time. Profiles do not expire. Country and currency use
ISO 3166-1 alpha-2 and ISO 4217 codes; choose them independently rather than
assuming a currency from country. Apply bounded lengths and format validation
to all editable fields. Contact email and phone are unverified and must not
be used for authentication or represented as verified.

- **Sign-in:** after validating the Google token, extract available name and
  email claims. Store the email only if Google marks it verified; otherwise
  show it as unavailable. Set `provider` to `google` to identify the source
  of the read-only sign-in email. Initialize the display name from Google
  only when creating the profile, never overwrite a parent's edits. Refresh
  `email` on subsequent validated sign-ins, clearing it if no verified email
  is present. Never use email to identify or link users.
- **API/application:** add authenticated `GET` and `PUT /api/account` for
  the current session's user only. Return only page data and a version;
  accept only editable fields. Use Cosmos ETags and `If-Match` to detect
  concurrent edits, returning a conflict and prompting a reload rather than
  silently overwriting changes. Reuse session validation and the existing
  origin and CSRF checks for writes.
- **React:** add portal navigation and a responsive account form with
  loading, empty, validation, conflict, and error states.

Identity and profile documents have different `/id` partition-key values, so
their writes are not atomic together. Make profile creation idempotent by
user ID and ensure it succeeds before issuing a session. On a retry or existing user's
next sign-in, create any missing profile using available verified claims;
do not reset an existing profile. If an existing session opens the account
page before that sign-in, prompt the parent to sign in again to initialize
the profile. Surface storage failures rather than silently creating a
success-shaped empty profile. Do not invent historical sign-up timestamps.

## Security and verification

Use the existing container and its runtime and developer Cosmos data-plane
permissions. Keep tokens, cookies, raw Google subject, and unnecessary
personal data out of responses and logs. Return account data with
`Cache-Control: no-store`; never trust a user ID supplied by the browser.
Review retention, deletion, and privacy notice requirements before collecting
this information.

Test first and repeat sign-in, missing, changed and unverified Google claims,
concurrent creation and recovery after partial failure, and existing-user
backfill. Test read and update, optional-field clearing, validation, ETag
conflicts, session expiry, CSRF/origin rejection, and cross-user isolation.
Verify the account page and refresh through Front Door, including keyboard
and mobile use.
