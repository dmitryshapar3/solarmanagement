using SolarManagement.Integrations.WorkerSdk;
using SolarManagement.Providers.SolisCloud;

await IntegrationWorkerHost.RunAsync(SolisCloudProvider.ProviderId, SolisCloudProvider.Operations,
    configuration => new SolisCloudProvider(configuration, CloudProviderTransport.Create(configuration)));
