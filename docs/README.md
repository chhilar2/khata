# Khata

Khata is a starter for a kids' banking application. Balances, investments,
gig jobs, authentication, and Cosmos DB persistence are **not implemented**.
The only API endpoint is an anonymous `GET /api/health` liveness check; it does
not check a database.

## Structure

- `src/Khata.Api` - Azure Functions v4 isolated .NET worker and HTTP endpoints.
- `src/Khata.Application` - application services and use cases.
- `src/Khata.Domain` - domain model.
- `src/Khata.Infrastructure` - future external services and Cosmos DB access.
- `src/Khata.Web` - React frontend built with Vite.
- `tests/Khata.UnitTests` - isolated .NET unit tests.
- `tests/Khata.EndToEndTests` - HTTP tests against a running Functions host.
- `scripts` - Bash build, test, and publish entry points.

The application, domain, and infrastructure projects are intentionally empty
until the corresponding features and data model are designed.

## Prerequisites

- .NET 10 SDK (10.0.100 or later in the 10.0 feature bands).
- Node.js 20.19+ or 22.12+ and npm.
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
  for running the API locally.
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
  for local Azure Storage emulation when needed by the Functions host.
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
`start` option; the isolated worker is configured in `local.settings.json`.
You can omit that flag. See the Core Tools link above for other installation
methods.

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

Copy `src/Khata.Api/local.settings.example.json` to
`src/Khata.Api/local.settings.json`. Start Azurite if the host requires local
storage, then use separate terminals:

```bash
cd src/Khata.Api
func start
```

```bash
cd src/Khata.Web
npm ci
npm run dev
```

Open the URL printed by Vite. Its development server proxies `/api` requests
to `http://localhost:7071`. Check the API at
`http://localhost:7071/api/health`.

For .NET debugging in VS Code, install the recommended C# extension, start
the host with `func start --dotnet-isolated-debug`, then run **Attach to
Functions .NET worker** and select the `Khata.Api` process. The host waits
for the debugger before executing worker code. For frontend debugging, use
the browser's developer tools and Vite's source maps.

With the host running, execute the HTTP end-to-end test from the root:

```bash
KHATA_API_BASE_URL=http://localhost:7071 ./scripts/test-e2e.sh
```

The current end-to-end test covers the API health endpoint, not browser flows.

## Deployment

`./scripts/publish.sh` produces the Functions app in `artifacts/api` and
static frontend files in `src/Khata.Web/dist`. Deploy them separately to an
Azure Function App and a static web host or App Service, respectively. Provision
the Function App, storage, Cosmos DB, networking, secrets, and deployment
pipeline separately; none are provisioned by this repository yet. Configure
the Function App for the .NET isolated worker and a supported .NET 10 hosting
plan; .NET 10 is not supported on Linux Consumption, so use Flex Consumption
or another supported plan. For production API calls, configure routing or CORS
and authentication explicitly; the Vite proxy applies only in development.
