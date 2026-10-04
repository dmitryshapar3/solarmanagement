using SolarManagement.Integrations.WorkerSdk;
using SolarManagement.Providers.SungrowCloud;

await IntegrationWorkerHost.RunAsync(SungrowCloudProvider.ProviderId, SungrowCloudProvider.Operations,
    configuration => new SungrowCloudProvider(configuration, CloudProviderTransport.Create(configuration)));
