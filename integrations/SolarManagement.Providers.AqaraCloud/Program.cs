using SolarManagement.Integrations.WorkerSdk;
using SolarManagement.Providers.AqaraCloud;

await IntegrationWorkerHost.RunAsync(AqaraCloudProvider.ProviderId, AqaraCloudProvider.Operations, configuration => new AqaraCloudProvider(configuration));
