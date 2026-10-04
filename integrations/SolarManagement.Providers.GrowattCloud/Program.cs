using SolarManagement.Integrations.WorkerSdk;
using SolarManagement.Providers.GrowattCloud;

await IntegrationWorkerHost.RunAsync(GrowattCloudProvider.ProviderId, GrowattCloudProvider.Operations,
    configuration => new GrowattCloudProvider(configuration, CloudProviderTransport.Create(configuration)));
