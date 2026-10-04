using SolarManagement.Integrations.Contracts;

namespace DeyeSolar.Web.Integrations;

public static class IntegrationProviderKinds
{
    // Existing UI layouts identify the device selector without referencing provider assemblies.
    public static bool Supports(IntegrationProviderDescriptor descriptor, string kind)
        => descriptor.UiLayout?.Steps.Any(step => step.Id == kind) == true
            || kind == "inverter" && descriptor.ProviderId == "deye.cloud"
            || kind == "socket" && descriptor.ProviderId == "shelly.cloud";
}
