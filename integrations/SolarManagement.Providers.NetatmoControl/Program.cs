using SolarManagement.Integrations.WorkerSdk;
using SolarManagement.Providers.NetatmoControl;

await IntegrationWorkerHost.RunAsync(NetatmoControlProvider.ProviderId, NetatmoControlProvider.Operations, configuration => new NetatmoControlProvider(configuration, CloudProviderTransport.Create(configuration)));
