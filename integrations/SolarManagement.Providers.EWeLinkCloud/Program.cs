using SolarManagement.Integrations.WorkerSdk;
using SolarManagement.Providers.EWeLinkCloud;

await IntegrationWorkerHost.RunAsync(EWeLinkCloudProvider.ProviderId, EWeLinkCloudProvider.Operations, configuration => new EWeLinkCloudProvider(configuration));
