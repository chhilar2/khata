# User login design proposal

## Goal and scope

Provide a public landing page with attractive static information and no
user-specific data. The portal is intended for parents managing ledgers for
their children; children do not access it directly. Visitors can sign in with
Google to enter the portal. Any Google account may sign in; the first
successful login creates a Khata user. Parent status is not verified. Keep
the design extensible to other identity providers, without implementing those
providers now.

## Approach

Use Google Identity Services to obtain a Google ID token in the browser. The
React frontend sends it to the Azure Functions API, which validates it and
issues an opaque, server-side Khata session. The token is not persisted.
The public page remains accessible without a session; portal pages and
user-data APIs require one.

```text
Browser -> API login start -> Google sign-in -> API token verification
        -> Khata session cookie -> authenticated portal
```

### Sign-in and session

1. The landing page offers **Sign in with Google**. A logged-in user can
   proceed to the portal; unauthenticated portal navigation returns to the
   landing page.
2. The API starts a flow with a random state, nonce and browser binding.
   Store hashes of these briefly server-side.
3. The browser sends the Google ID token to the API. Validate the origin,
   flow binding, signature, issuer, audience, expiry and nonce. Reject failed
   or replayed flows and show a useful, non-sensitive error.
4. Resolve the validated provider issuer and subject to a Khata user, creating
   one on first login. Issue a new random session cookie and redirect to the
   portal. The portal loads the current user from the API.
5. On sign-out, invalidate the Khata session and clear its cookie. Expired
   sessions require sign-in again; signing out does not sign the user out of
   Google.

## Components and data

- **React:** public landing page, sign-in/error states, authenticated portal
  shell and sign-out. The UI checks session status with the API; it is not an
  authorization boundary.
- **API:** login, callback, current-session and logout endpoints. Validate
  sessions on all user-data endpoints and return `401` when absent or expired.
  Keep the existing health endpoint anonymous.
- **Application and infrastructure:** isolate OIDC provider configuration and
  identity verification behind a provider boundary. Add a Google adapter;
  future providers must supply a validated issuer and subject through the
  same boundary. Do not automatically link accounts by email.
- **Cosmos DB:** store a minimal Khata user identity mapping and short-lived
  login-flow and session records with expiry/TTL. Identify an external
  identity by provider, issuer and subject, not email; enforce uniqueness
  atomically across concurrent first logins, including the partitioning
  strategy. Do not persist Google access or refresh tokens.

## Security and operations

Use HTTPS, a host-only `Secure`, `HttpOnly`, `SameSite=Lax` session cookie,
server-side expiration and session rotation at login. Protect state-changing
requests, including logout, against CSRF; check origin and use a CSRF token.
Use authenticated user IDs for authorization and enforce resource ownership
in future banking APIs, not just in the frontend.

Route the frontend and API under one origin in production; Vite's existing
proxy provides that arrangement locally. Configure the Google client ID and
Cosmos DB through environment-specific settings; do not use or store a Google
client secret. Avoid logging tokens, cookies or unnecessary personal
information. Register the local and deployed origins with Google.

## Delivery and verification

1. Add identity/session persistence and Google sign-in; configure production
   routing and identity-based Azure access.
2. Add the public page, sign-in, portal session check and sign-out.
3. Test first and repeat login, concurrent first logins, logout and expiry;
   reject invalid state, nonce, token and replayed login requests. Verify anonymous
   access to the landing page and health endpoint, `401` for protected APIs,
   CSRF protection, and an end-to-end Google sign-in in a test environment.

**Before launch:** Assess privacy and security requirements for children's
ledger data. Google sign-in does not verify that a user is a parent, and this
design relies on guidance rather than restricting sign-up to parents.
