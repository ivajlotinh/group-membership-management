@description('Storage account alphanumeric name.')
@minLength(1)
@maxLength(24)
param name string

@description('Key vault name.')
param keyVaultName string

@allowed([
  'Standard_LRS'
  'Standard_GRS'
  'Standard_ZRS'
  'Premium_LRS'
])
param sku string = 'Standard_LRS'

@description('Key vault name.')
param add30DayDeletionPolicy bool = false

resource storageAccount 'Microsoft.Storage/storageAccounts@2019-04-01' = {
  name: name
  location: resourceGroup().location
  kind: 'StorageV2'
  sku: {
    name: sku
  }
  properties: {
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
  identity: {
    type: 'SystemAssigned'
  }
}

resource storageAccountPolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2022-05-01' = if (add30DayDeletionPolicy) {
  name: 'default'
  parent: storageAccount
  properties: {
    policy: {
      rules: [
        {
          definition: {
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: 30
                }
              }
            }
            filters: {
              blobTypes: [
                'blockBlob'
              ]
            }
          }
          enabled: true
          name: '30-Day Blob Deletion Policy'
          type: 'Lifecycle'
        }
      ]
    }
  }
}

module secureSecretsTemplate 'keyVaultSecretsSecure.bicep' = {
  name: 'secureSecretsTemplate${name}'
  params: {
    keyVaultName: keyVaultName
    keyVaultSecrets: {
      secrets: [
        {
          name: startsWith(name, 'jobs') ? 'jobsStorageAccountConnectionString' : 'storageAccountConnectionString'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
      ]
    }
  }
}


