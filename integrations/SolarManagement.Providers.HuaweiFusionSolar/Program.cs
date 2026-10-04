using SolarManagement.Integrations.WorkerSdk;
using SolarManagement.Providers.HuaweiFusionSolar;

await IntegrationWorkerHost.RunAsync(HuaweiFusionSolarProvider.ProviderId, HuaweiFusionSolarProvider.Operations,
    configuration => new HuaweiFusionSolarProvider(configuration));
