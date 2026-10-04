using SolarManagement.Integrations.Contracts;
using SolarManagement.Integrations.WorkerSdk;
using SolarManagement.Providers.ShellyCloud;

await IntegrationWorkerHost.RunAsync(ShellyCloudProvider.ProviderId, ShellyCloudProvider.Operations,
    configuration => new ShellyCloudProvider(configuration));

// Existing endpoint tests reference this legacy internal name.
internal sealed class ShellyProvider(WorkerConfiguration configuration) : ShellyCloudProvider(configuration);
