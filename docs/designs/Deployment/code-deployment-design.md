# Application code deployment design

**Status: implementation in progress.** A deployment workflow exists but has
not been verified in Azure. It depends on the
[resource deployment design](./resource-deployment-design.md).

## Goal and approach

Build, test and deploy the API and React frontend independently of Azure
resource provisioning. Deploy both to the same development environment used
for local debugging. The existing build, unit-test and publish scripts are the
starting point; the current E2E test only checks health.

## Delivery flow

1. Pull requests run restore, build and unit tests without Azure credentials.
   A protected development deployment runs from an approved commit after the
   infrastructure and required settings are available.
2. Build immutable API and frontend artifacts from the same commit, recording
   the commit and artifact versions. Exclude local settings, credentials and
   test data. Keep prior artifacts available for rollback.
3. GitHub Actions exchanges its short-lived OIDC token for an Entra token
   using a **deployment-only user-assigned identity**, separate from the
   Functions runtime identity. Restrict federation to this repository and the
   development environment; protect that environment and deployment workflow.
   Tenant, subscription and client IDs are identifiers, not credentials.
4. Publish the API package to the Flex Consumption Function App using Azure
   CLI with Entra authentication and SCM basic publishing disabled. Upload
   the frontend build to the website's `$web` container with Entra data
   access. Grant the deployment identity scoped Function deployment
   permissions and Storage Blob Data Contributor on the website container;
   it does not need runtime Cosmos data access or permission to assign roles.
5. Check health through Front Door, anonymous session behavior, static assets
   and SPA routing. Run the existing HTTP E2E test against the deployed URL,
   then verify a Google sign-in and logout with a designated test account.
   Report a failed check as a failed deployment, not a successful release.

API and frontend deployments are separate and not atomic. Keep adjacent
versions compatible; deploy additive API changes before frontend consumers and
coordinate breaking changes explicitly. Keep previous hashed frontend assets
available during transitions and configure the HTML entry point to revalidate.
Roll back by redeploying both prior artifacts when necessary. Rollback does not
undo Cosmos data changes; use separately reviewed, compatible migrations.

Financial writes remain disabled by default while the balance feature is
incomplete. Before enabling them, block profile and financial writes, drain
all API versions that do not honor the profile-deletion guard, deploy and
verify the compatible API, then enable the feature and restore writes.
Do not roll back to an API that bypasses the guard after financial records exist.

## Security and verification

Disable API-route caching and verify redirects and multiple session cookies
pass through Front Door. Restrict the API origin and secure SCM separately
without blocking the deployment runner. Do not log tokens, cookies or
authentication payloads. Validate that unauthorized repositories cannot
obtain deployment access and that basic publishing credentials are disabled.

The Google sign-in and keyless Azure changes must be tested with real services
before relying on this design.

References: [GitHub OIDC for Azure](https://learn.microsoft.com/azure/developer/github/connect-from-azure-openid-connect),
[Functions package deployment](https://learn.microsoft.com/azure/azure-functions/deployment-zip-push).
