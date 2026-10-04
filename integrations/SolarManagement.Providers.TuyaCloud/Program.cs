using SolarManagement.Integrations.WorkerSdk;
using SolarManagement.Providers.TuyaCloud;

await IntegrationWorkerHost.RunAsync(TuyaCloudProvider.ProviderId, TuyaCloudProvider.Operations, configuration => new TuyaCloudProvider(configuration, CloudProviderTransport.Create(configuration)));
