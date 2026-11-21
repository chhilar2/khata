# Khata

Khata is a starter portal for parents to manage their children's ledgers;
children do not access the portal directly. Google sign-in and server-side
sessions use Cosmos DB. Balances, investments, and gig jobs are **not
implemented**. Anonymous `GET /api/health` is a liveness check and does not
check authentication or the database.

## Structure

- `src/Khata.Api` - Azure Functions v4 isolated .NET worker and HTTP endpoints.
- `src/Khata.Application` - authentication service and use cases.
- `src/Khata.Domain` - domain model.
- `src/Khata.Infrastructure` - Google identity and Cosmos DB access.
- `src/Khata.Web` - React frontend built with Vite.
- `tests/Khata.UnitTests` - isolated .NET unit tests.
- `tests/Khata.EndToEndTests` - HTTP tests against a running Functions host.
- `scripts` - Bash build, test, and publish entry points.

Banking domain models and features remain to be designed and implemented.

## Feature designs

The repository includes a [feature-design skill](../.github/skills/feature-design/SKILL.md)
for creating concise proposals under `docs/designs/`. In Copilot Chat, select
`/feature-design` and provide the feature requirements or a link to their
file. For example: `/feature-design revise docs/designs/Auth/user-login-design.md`.
Copilot may also load the skill automatically for a matching request.

Edit the skill's `SKILL.md` to change the workflow or proposal format for
future designs. In VS Code, use **Chat: Open Customizations** and select
**Skills** to inspect workspace skills.

The [deployment designs](./designs/Deployment/resource-deployment-design.md)
describe the keyless Azure development setup. The template and workflow have
not yet been verified against a deployed Azure environment.

## Prerequisites

- .NET 10 SDK (10.0.100 or later in the 10.0 feature bands).
- Node.js 20.19+ or 22.12+ and npm.
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
  for running the API locally.
- Azure CLI and an Azure subscription with permission to deploy resources and
  assign roles. Local debugging uses the same Azure development resources.
- Bash for the scripts below (on Windows, use a Bash environment).

On macOS, install Core Tools with [Homebrew](https://brew.sh/):

```bash
brew tap azure/functions
brew install azure-functions-core-tools@4
func --version
```

Alternatively, if npm is available, run the following in `src/Khata.Api`:

```bash
npx --yes --package azure-functions-core-tools@4 func start --dotnet-isolated
```

This downloads the tools into npm's cache and does not install `func`
globally. Core Tools v4 does not list `--dotnet-isolated` as a supported
`start` option; use the published-host command below for local debugging.
The isolated worker is configured in `local.settings.json`.

## Build and test

From the repository root:

```bash
./scripts/build.sh       # restore and build the .NET solution and React app
./scripts/test.sh        # run unit tests
./scripts/publish.sh     # publish API and build frontend
```

The build requires access to NuGet and npm. The end-to-end project requires a
running host; `dotnet test Khata.sln` therefore also requires
`KHATA_API_BASE_URL`. To run only unit tests, use `./scripts/test.sh`.

## Run and debug locally

First provision and configure the development environment below. Sign in with
Azure CLI, select the correct subscription, and ensure the signed-in user has
the template's Cosmos and host-storage roles. Copy the
`src/Khata.Api/local.settings.example.json` template to the ignored
`src/Khata.Api/local.settings.json` if it does not already exist. Fill in the
Google client ID, Cosmos endpoint and host-storage account name. Keep
`Auth__CredentialSource` set to `AzureCli` and use a unique local host ID
per developer. Remove any old `AzureWebJobsStorage` connection string,
`Auth__GoogleClientSecret`, or `Cosmos__Key` values from local settings.

Build the published Functions host, then copy the non-secret local settings
into its output. Use separate terminals:

```bash
dotnet publish src/Khata.Api/Khata.Api.csproj -c Debug -o artifacts/api
cp src/Khata.Api/local.settings.json artifacts/api/local.settings.json
cd artifacts/api
npx --yes --package azure-functions-core-tools@4 func start
```

```bash
cd src/Khata.Web
npm ci
npm run dev -- --port 5173 --strictPort
```

Open `http://localhost:5173`. Vite proxies `/api` requests to
`http://localhost:7071`. Check the API at `http://localhost:7071/api/health`.
Local actions use the shared Azure development data; use test records and
avoid destructive tests. If the copied local settings previously contained
secrets, remove them from both copies and rotate any exposed credentials.
The Functions host uses its local developer credential chain for host storage;
the application uses the Azure CLI identity for Cosmos DB.

### Configure Google sign-in

Create a Google OAuth **web application** client and configure its consent
screen. Register `http://localhost:5173` and the deployed Front Door origin as
authorized JavaScript origins. Add your Google account as a test user while
the consent screen is in testing mode. The app uses the public client ID; it
does not use or store the Google client secret.

Google Identity Services sends an ID token through the browser to the API;
the token is validated and not persisted. Sessions expire after eight hours;
logout invalidates the Khata session, not the Google session. Identity
mappings are keyed by provider, issuer and subject, never email. Login flows
expire after ten minutes. Cookies are `Secure`; use a browser that treats
HTTP localhost as a secure context, or use HTTPS locally.
The portal is intended for parents, but sign-in permits any Google account
that Google allows for the OAuth client; parent status is not verified.
Assess privacy and security requirements for children's ledger data before
launch.

For .NET debugging in VS Code, install the recommended C# extension, start
the published host with `func start --dotnet-isolated-debug`, then run
**Attach to Functions .NET worker** and select the `Khata.Api` process.
For frontend debugging, use browser developer tools and Vite source maps.

With the host running, execute the HTTP end-to-end test from the root:

```bash
KHATA_API_BASE_URL=http://localhost:7071 ./scripts/test-e2e.sh
```

The current end-to-end test covers the API health endpoint, not a live Google
browser flow. Unit tests cover session and token-validation behavior; a real
Google account and Cosmos DB are needed to verify the complete sign-in flow.

## Deployment

`infra/main.bicep` defines the development resources, runtime identity,
developer access and a separate GitHub deployment identity. It creates
billable resources, disables Cosmos and storage key authentication, and is
**not yet verified by an Azure deployment**. Review the
[resource deployment design](./designs/Deployment/resource-deployment-design.md),
pricing, backup needs and what-if output before applying it. An administrator
needs permission to create resources, attach identities and assign Azure and
Cosmos data-plane roles. Never apply the development template to production.

After signing in with `az login`, selecting the subscription and creating a
development resource group in a Flex Consumption-supported region, use the
Google client ID and your Entra user object ID:

```bash
az deployment group what-if --resource-group '<dev-resource-group>' \
  --template-file infra/main.bicep \
  --parameters googleClientId='<google-client-id>' developerPrincipalId='<user-object-id>'
az deployment group create --resource-group '<dev-resource-group>' \
  --template-file infra/main.bicep \
  --parameters googleClientId='<google-client-id>' developerPrincipalId='<user-object-id>'
```

Replace the quoted placeholders with your values.
Obtain the user object ID with `az ad signed-in-user show --query id -o tsv`.
The deployment outputs the Front Door origin, Functions name, storage names,
Cosmos endpoint and deployment identity client ID. Add the Front Door origin
to the Google client. Role assignments can take time to propagate. The
template grants the deployment identity Website Contributor on the Function
App, which allows app configuration changes as well as deployment; restrict
the GitHub environment and review its access before use.

Create a protected GitHub environment named `development`, and set these
environment **variables** (not secrets): `AZURE_CLIENT_ID` from the deployment
identity output, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
`AZURE_RESOURCE_GROUP`, `AZURE_FUNCTION_APP`, `AZURE_WEBSITE_STORAGE`, and
`KHATA_ORIGIN`. The template's GitHub OIDC subject is bound to this
repository's immutable owner and repository IDs; if using another repository,
set `githubRepository` to its `owner@ownerId/name@repositoryId` subject
format. Protect the main branch and require environment approval.
Run the **Deploy development** workflow manually from `main`. It builds,
tests, uploads both artifacts with federated authentication, and tests health
and anonymous access. Verify Google sign-in and logout manually afterward;
the workflow does not test live sign-in. Release artifacts are retained for
30 days; rollback requires redeploying a compatible prior API and frontend.
The workflow retains old hashed assets but does not deploy arbitrary Vite
`public/` files; add handling if the frontend gains any.

The static website is public. Do not put user data, tokens, local settings or
keys in frontend assets. Configure proxy and telemetry logging to omit
credentials and personal data, and apply suitable login rate limits before
launch. Parent status is not verified by Google sign-in.
If an earlier version stored raw CSRF tokens in Cosmos session documents,
those documents can remain until expiry or cleanup; review existing data
before claiming that all historical tokens have been removed.
