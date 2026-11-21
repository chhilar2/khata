# Development environment management design

**Status: implementation in progress.** This complements
[resource deployment](./resource-deployment-design.md) and
[code deployment](./code-deployment-design.md).

## Local debugging against Azure

The local Functions host and deployed Function App use the same development
Cosmos DB and host storage, but **different identities**. A developer signs
in interactively with Azure CLI or VS Code; grant that Entra user the required
Cosmos data-plane and host-storage roles separately from the runtime managed
identity. Select the intended tenant and subscription, and ensure the
credential source is unambiguous. Use a distinct local Functions host ID to
avoid host-storage collisions.

Keep only non-secret endpoints, names, client IDs and localhost origin in
local configuration. The Vite proxy sends local `/api` requests to the local
host. Register both the local and deployed origins with Google as appropriate.
Development credentials and token caches are controlled by the developer's
sign-in tools; managed identities themselves are available only in Azure.
Local changes affect the shared development data: use clearly identified test
records, avoid destructive tests, and coordinate schema changes.

## Google sign-in and session changes

Replace the existing backend authorization-code exchange and client secret
with Google Identity Services. The browser briefly receives a Google ID token
and sends it to the API over HTTPS (or loopback HTTP for local development);
do not persist or log it. The API validates Google's signature, issuer,
audience, expiry and a one-time browser-bound nonce, and applies CSRF and
origin protections before issuing a secure, HttpOnly session cookie. Keep
session identifiers hashed server-side and remove the current raw CSRF-token
storage as part of the implementation. Test replay, invalid tokens,
cross-origin requests, logout and expiration.

This changes the original authorization-code approach described in the
[login design](../Auth/user-login-design.md). The app does not use or store a
Google client secret; verify how to handle any unused secret issued by Google.
Short-lived identity
tokens, session cookies and CSRF material still exist during authentication.
Google sign-in does not verify that a user is a parent.

## Operations and lifecycle

- Review resource and access changes with what-if and least-privilege
  assignments; separate privileged infrastructure administration from routine
  code deployment. Review access and remove it when no longer needed.
- Monitor failures, availability, spending and Cosmos capacity. Do not log
  session cookies, tokens, callback payloads or children's ledger data.
- Choose backup, retention and restore objectives before storing real data;
  test restoration without overwriting the shared development database.
  Treat schema migrations and data recovery separately from code rollback.
- Record deployed commit versions and configuration; diagnose identity and
  role propagation failures without falling back to keys. Recreating a
  managed identity changes its principal and requires role reassignment.

## Verification and open decisions

Verify local and deployed sign-in with their respective identities, denied
access when roles are removed, isolated host IDs, backup restore, and a
reproducible rollback. Decide acceptable costs, backup objectives and the
boundary for test versus real family data before deployment.
