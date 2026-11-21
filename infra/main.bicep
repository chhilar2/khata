targetScope = 'resourceGroup'

@description('Azure region supporting Functions Flex Consumption.')
param location string = resourceGroup().location

@description('Google web client ID; register the Front Door and localhost origins.')
@minLength(1)
param googleClientId string

@description('Object ID of the developer permitted to debug against the development resources.')
@minLength(1)
param developerPrincipalId string

@description('GitHub repository in immutable owner@ownerId/name@repositoryId subject format.')
param githubRepository string = 'chhilar2@307834270/khata@1466709897'

@description('Protected GitHub environment used for code deployment.')
param githubEnvironment string = 'development'

var suffix = uniqueString(resourceGroup().id)
var blobOwnerRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var blobContributorRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var monitoringRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
var websiteContributorRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'de139f84-1756-47ae-9be6-808fbbe84772')

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'khata-runtime-${suffix}'
  location: location
}

resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'khata-deploy-${suffix}'
  location: location
}

resource githubFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployIdentity
  name: 'github-development'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:environment:${githubEnvironment}'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'khatahost${suffix}'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource hostBlobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: hostStorage
  name: 'default'
}

resource packages 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: hostBlobs
  name: 'packages'
  properties: {
    publicAccess: 'None'
  }
}

resource webStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'khataweb${suffix}'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource webBlobs 'Microsoft.Storage/storageAccounts/blobServices@2026-04-01' = {
  parent: webStorage
  name: 'default'
  properties: {
    staticWebsite: {
      enabled: true
      defaultIndexDocumentPath: 'index.html'
      errorDocument404Path: '404.html'
    }
  }
}

resource webContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: webBlobs
  name: '$web'
}

resource hostRuntimeRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, runtimeIdentity.id, blobOwnerRole)
  scope: hostStorage
  properties: {
    roleDefinitionId: blobOwnerRole
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource hostDeveloperRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, developerPrincipalId, blobOwnerRole)
  scope: hostStorage
  properties: {
    roleDefinitionId: blobOwnerRole
    principalId: developerPrincipalId
    principalType: 'User'
  }
}

resource deployStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webContainer.id, deployIdentity.id, blobContributorRole)
  scope: webContainer
  properties: {
    roleDefinitionId: blobContributorRole
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: 'khata-cosmos-${suffix}'
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true
    consistencyPolicy: {
      defaultConsistencyLevel: 'Strong'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
      }
    ]
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: 'khata'
  properties: {
    resource: {
      id: 'khata'
    }
  }
}

resource authContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'authentication'
  properties: {
    resource: {
      id: 'authentication'
      partitionKey: {
        paths: [
          '/id'
        ]
        kind: 'Hash'
      }
      defaultTtl: -1
    }
  }
}

var cosmosDataContributor = '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
var authScope = '${cosmos.id}/dbs/${database.name}/colls/${authContainer.name}'

resource runtimeCosmosRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, runtimeIdentity.id, authScope)
  properties: {
    principalId: runtimeIdentity.properties.principalId
    roleDefinitionId: cosmosDataContributor
    scope: authScope
  }
}

resource developerCosmosRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, developerPrincipalId, authScope)
  properties: {
    principalId: developerPrincipalId
    roleDefinitionId: cosmosDataContributor
    scope: authScope
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'khata-logs-${suffix}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'khata-insights-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: true
  }
}

resource telemetryRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, runtimeIdentity.id, monitoringRole)
  scope: insights
  properties: {
    roleDefinitionId: monitoringRole
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource frontDoor 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: 'khata-edge-${suffix}'
  location: 'global'
  sku: {
    name: 'Standard_AzureFrontDoor'
  }
}

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: frontDoor
  name: 'khata-${suffix}'
  location: 'global'
  properties: {
    enabledState: 'Enabled'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'khata-plan-${suffix}'
  location: location
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource api 'Microsoft.Web/sites@2024-04-01' = {
  name: 'khata-api-${suffix}'
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ipSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictionsUseMain: false
      ipSecurityRestrictions: [
        {
          name: 'front-door'
          action: 'Allow'
          priority: 100
          tag: 'ServiceTag'
          ipAddress: 'AzureFrontDoor.Backend'
          headers: {
            'x-azure-fdid': [
              frontDoor.properties.frontDoorId
            ]
          }
        }
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${hostStorage.properties.primaryEndpoints.blob}${packages.name}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: runtimeIdentity.id
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        instanceMemoryMB: 2048
        maximumInstanceCount: 40
      }
    }
  }
  dependsOn: [
    hostRuntimeRole
    runtimeCosmosRole
    telemetryRole
  ]
}

resource appSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: api
  name: 'appsettings'
  properties: {
    AzureWebJobsStorage__accountName: hostStorage.name
    AzureWebJobsStorage__credential: 'managedidentity'
    AzureWebJobsStorage__clientId: runtimeIdentity.properties.clientId
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
    APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD;ClientId=${runtimeIdentity.properties.clientId}'
    Auth__Origin: 'https://${endpoint.properties.hostName}'
    Auth__GoogleClientId: googleClientId
    Auth__CredentialSource: 'ManagedIdentity'
    Auth__ManagedIdentityClientId: runtimeIdentity.properties.clientId
    Cosmos__Endpoint: cosmos.properties.documentEndpoint
    Cosmos__Database: database.name
    Cosmos__Container: authContainer.name
  }
}

resource disableScmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: api
  name: 'scm'
  properties: {
    allow: false
  }
}

resource disableFtpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: api
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource deployApiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(api.id, deployIdentity.id, websiteContributorRole)
  scope: api
  properties: {
    roleDefinitionId: websiteContributorRole
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource apiGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: frontDoor
  name: 'api'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
    }
    healthProbeSettings: {
      probePath: '/api/health'
      probeRequestType: 'GET'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 100
    }
  }
}

resource apiOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: apiGroup
  name: 'functions'
  properties: {
    hostName: api.properties.defaultHostName
    originHostHeader: api.properties.defaultHostName
    httpsPort: 443
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
  }
}

resource siteGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: frontDoor
  name: 'site'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
    }
    healthProbeSettings: {
      probePath: '/'
      probeRequestType: 'GET'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 100
    }
  }
}

resource siteOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: siteGroup
  name: 'website'
  properties: {
    hostName: replace(replace(webStorage.properties.primaryEndpoints.web, 'https://', ''), '/', '')
    originHostHeader: replace(replace(webStorage.properties.primaryEndpoints.web, 'https://', ''), '/', '')
    httpsPort: 443
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
  }
  dependsOn: [
    webBlobs
  ]
}

resource siteRules 'Microsoft.Cdn/profiles/ruleSets@2024-02-01' = {
  parent: frontDoor
  name: 'siteRules'
}

resource portalRule 'Microsoft.Cdn/profiles/ruleSets/rules@2024-02-01' = {
  parent: siteRules
  name: 'portal'
  properties: {
    order: 1
    conditions: [
      {
        name: 'UrlPath'
        parameters: {
          typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
          operator: 'Equal'
          matchValues: [
            'portal'
          ]
          transforms: []
          negateCondition: false
        }
      }
    ]
    actions: [
      {
        name: 'UrlRewrite'
        parameters: {
          typeName: 'DeliveryRuleUrlRewriteActionParameters'
          sourcePattern: '/'
          destination: '/index.html'
          preserveUnmatchedPath: false
        }
      }
    ]
  }
}

resource apiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: 'api'
  properties: {
    originGroup: {
      id: apiGroup.id
    }
    supportedProtocols: [
      'Http'
      'Https'
    ]
    patternsToMatch: [
      '/api/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    httpsRedirect: 'Enabled'
    enabledState: 'Enabled'
  }
  dependsOn: [
    apiOrigin
  ]
}

resource siteRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: 'website'
  properties: {
    originGroup: {
      id: siteGroup.id
    }
    ruleSets: [
      {
        id: siteRules.id
      }
    ]
    supportedProtocols: [
      'Http'
      'Https'
    ]
    patternsToMatch: [
      '/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Enabled'
    httpsRedirect: 'Enabled'
    enabledState: 'Enabled'
  }
  dependsOn: [
    siteOrigin
    portalRule
  ]
}

output origin string = 'https://${endpoint.properties.hostName}'
output functionAppName string = api.name
output hostStorageName string = hostStorage.name
output websiteStorageName string = webStorage.name
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output runtimeIdentityClientId string = runtimeIdentity.properties.clientId
output deploymentIdentityClientId string = deployIdentity.properties.clientId
