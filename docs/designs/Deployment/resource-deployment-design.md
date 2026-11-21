# Azure resource deployment design

**Status: implementation in progress.** A Bicep template exists but has not
been verified in Azure. See [code deployment](./code-deployment-design.md) and
[operations](./resource-management-design.md).

## Goal and topology

Provision one Azure development environment that the deployed app and local
debugger can both use, without storing long-lived application credentials.
The portal is for parents managing their children's ledgers.

```text
Browser -> Front Door -> /api/* -> Functions -> Cosmos DB
                      -> other  -> public static website
```

Use Functions v4 on Linux Flex Consumption, Cosmos DB for NoSQL, a static
website storage account, Functions host/deployment storage, Front Door, and
Application Insights. Keep website and host storage separate. The static
website is public and contains no user data or secrets. Front Door serves one
HTTPS origin, rewrites `/portal` to the SPA entry point, and never caches API
responses. Restrict the API origin to the specific Front Door using its
service tag and Front Door ID; protect the deployment endpoint separately.

## Infrastructure and identities

Use version-controlled Bicep with development parameters for names, region,
resource IDs and non-secret settings. An authorized operator creates the
resource group, previews changes with ARM what-if, reviews them, then deploys
in incremental mode. Resource deployment is separate from application
deployment. Bicep is declarative; no separate Terraform state is needed.

1. Create a **user-assigned managed identity** for the Functions runtime.
   Attach it to the Function App and configure the Cosmos SDK, Functions host,
   deployment storage and telemetry to select it.
2. Grant it Cosmos DB's **data-plane** Data Contributor role at the needed
   container scope, including SDK metadata access; normal Azure management
   roles alone do not grant item access. Configure Cosmos with one write
   region, Strong consistency, `/id` partitioning, and per-item TTL enabled.
3. Give the runtime identity the Functions host's required storage data
   permissions, including Storage Blob Data Owner for host storage. Give it
   Storage Blob Data Contributor on Flex deployment storage and Monitoring
   Metrics Publisher on Application Insights.
4. Bootstrap a **separate** deployment identity and GitHub OIDC trust for the
   intended repository and environment. Grant only the deployment permissions
   described in the code deployment design. Infrastructure changes remain
   operator-led initially.

The operator needs permission to create resources, attach the identity, and
create both Azure and Cosmos data-plane role assignments. Use resource IDs,
principal IDs and client IDs as non-secret configuration; a recreated identity
gets a new principal ID and requires new assignments. Allow for identity and
RBAC propagation before starting or verifying the app.

## Credential transition and safeguards

The API uses Entra-based Cosmos access and Google Identity Services as
described in [operations](./resource-management-design.md). The template
disables key-based access and configures identity-based Functions storage and
telemetry; these paths must be verified in Azure. Never publish local settings
or keys. Short-lived tokens and browser session cookies still exist and must
be protected.

## Verification and open decisions

Preview Bicep changes; deploy twice and check for unintended changes. Verify
role scopes, keyless access, denied access without roles, Front Door routing,
uncached APIs, and blocked direct API traffic. Configure budget alerts and
review region, cost, backup policy and custom domain before provisioning.
Deployed resources and production separation are not yet established.

References: [Functions identity-based connections](https://learn.microsoft.com/azure/azure-functions/manage-connections),
[Cosmos data-plane RBAC](https://learn.microsoft.com/azure/cosmos-db/how-to-connect-role-based-access-control).
