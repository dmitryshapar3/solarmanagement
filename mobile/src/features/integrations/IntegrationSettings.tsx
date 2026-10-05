import { useScopedAction } from "../../application/useScopedAction";
import { useLanguage } from "../../application/LanguageContext";
import { useCallback, useEffect, useRef, useState } from "react";
import { useFocusEffect } from "@react-navigation/native";
import { Keyboard, StyleSheet, Text, View } from "react-native";
import { AppButton, Card, EmptyState, ErrorBanner, SectionTitle, StatusPill, TextField } from "../../core/components";
import { ApiError } from "../../core/api/ApiClient";
import type {
  IntegrationApi, IntegrationDeviceBinding, IntegrationDiscovery, IntegrationInstance,
  IntegrationProvider, IntegrationSourceInverter, IntegrationTest, SecretOperation
} from "../../core/api/IntegrationApi";
import { colors, spacing, typography } from "../../core/theme";
import {
  applyIntegrationOAuth, createIntegrationDraft, integrationChange, integrationDraftChanged,
  unsupportedProvider, type IntegrationDraft
} from "./integrationDraft";
import { IntegrationFields } from "./IntegrationFields";
import { authorizeIntegration } from "./integrationOAuth";
import { IntegrationSelect } from "./IntegrationSelect";
import { supportsDeviceKind } from "./providerKinds";
import { useScreenRefresh } from "../../core/ScreenRefreshContext";

export function IntegrationSettings({ api, isDemo = false, onSelectionChanged }: {
  api: IntegrationApi;
  isDemo?: boolean;
  onSelectionChanged?: () => Promise<void>;
}) {
  const { t } = useLanguage();
  const [providers, setProviders] = useState<IntegrationProvider[]>([]);
  const [instances, setInstances] = useState<IntegrationInstance[]>([]);
  const [providerId, setProviderId] = useState("");
  const [name, setName] = useState("");
  const [draft, setDraft] = useState<IntegrationDraft | null>(null);
  const [bindings, setBindings] = useState<IntegrationDeviceBinding[]>([]);
  const [sourceInverters, setSourceInverters] = useState<IntegrationSourceInverter[]>([]);
  const [sourceDrafts, setSourceDrafts] = useState<Record<string, string>>({});
  const [phaseDrafts, setPhaseDrafts] = useState<Record<string, "1" | "3">>({});
  const [sourceError, setSourceError] = useState<string | null>(null);
  const [versions, setVersions] = useState<IntegrationProvider[]>([]);
  const [targetVersion, setTargetVersion] = useState("");
  const [versionError, setVersionError] = useState<string | null>(null);
  const [discovery, setDiscovery] = useState<IntegrationDiscovery | null>(null);
  const [testResult, setTestResult] = useState<IntegrationTest | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [catalogError, setCatalogError] = useState<string | null>(null);
  const [oauthNotice, setOAuthNotice] = useState<string | null>(null);
  const [sessionRevision, setSessionRevision] = useState(0);
  const actions = useScopedAction(api);
  const busy = actions.busy;
  const activeApi = useRef(api);
  const mounted = useRef(true);
  const savedOAuthFlow = useRef<string | null>(null);
  activeApi.current = api;

  useEffect(() => api.onSessionChange(() => {
    setSessionRevision(current => current + 1);
  }), [api]);

  useEffect(() => {
    const flowId = draft?.oauth?.flowId;
    if (!flowId) return;
    const instanceId = draft.configuration.instance.id;
    const epoch = api.sessionEpoch;
    return () => {
      if (savedOAuthFlow.current === flowId) { savedOAuthFlow.current = null; return; }
      if (api.sessionEpoch === epoch) void api.cancelOAuth(instanceId, flowId).catch(() => {});
    };
  }, [api, draft?.oauth?.flowId]);

  useEffect(() => {
    mounted.current = true;
    setLoading(true);
    setProviders([]);
    setInstances([]);
    setProviderId("");
    setName("");
    setError(null);
    setCatalogError(null);
    setDraft(null);
    setBindings([]);
    setSourceInverters([]);
    setSourceDrafts({});
    setPhaseDrafts({});
    setSourceError(null);
    setVersions([]);
    setTargetVersion("");
    setVersionError(null);
    setDiscovery(null);
    setTestResult(null);
    setOAuthNotice(null);
    return () => { mounted.current = false;  };
  }, [api, sessionRevision]);

  const refreshCatalog = useCallback(async (signal?: AbortSignal) => {
    if (isDemo) { setLoading(false); return; }
    try {
      const [catalog, list, sources] = await Promise.allSettled([api.getCatalog(signal), api.getInstances(signal), api.getSocketSources(signal)]);
      if (signal?.aborted || !mounted.current || activeApi.current !== api) return;
      if (catalog.status === "fulfilled") setProviders(catalog.value.providers);
      if (list.status === "fulfilled") setInstances(list.value);
      if (sources.status === "fulfilled" && Array.isArray(sources.value)) { setSourceInverters(sources.value); setSourceError(null); }
      else setSourceError("Inverter links could not be loaded. Refresh the integration catalog before changing links.");
      const failed = catalog.status === "rejected" ? catalog.reason : list.status === "rejected" ? list.reason : null;
      setCatalogError(failed ? failed instanceof ApiError && failed.status === 404
        ? "This server does not support dynamic integrations. Update the server to manage integrations here."
        : failed instanceof Error ? failed.message : "The integration catalog is unavailable." : null);
    } catch (exception) {
      if (!signal?.aborted && mounted.current && activeApi.current === api) {
        setCatalogError(exception instanceof ApiError && exception.status === 404
          ? "This server does not support dynamic integrations. Update the server to manage integrations here."
          : exception instanceof Error ? exception.message : "The integration catalog is unavailable.");
      }
    } finally {
      if (!signal?.aborted && mounted.current && activeApi.current === api) setLoading(false);
    }
  }, [api, isDemo, sessionRevision]);

  async function refreshSources(signal: AbortSignal) {
    try {
      const sources = await api.getSocketSources(signal);
      if (signal.aborted || !mounted.current || activeApi.current !== api) return;
      if (!Array.isArray(sources)) throw new Error("Invalid inverter sources.");
      setSourceInverters(sources); setSourceError(null);
    } catch {
      if (!signal.aborted && mounted.current && activeApi.current === api)
        setSourceError("Inverter links could not be loaded. Refresh the integration catalog before changing links.");
    }
  }

  useFocusEffect(useCallback(() => {
    const controller = new AbortController();
    void refreshCatalog(controller.signal);
    return () => controller.abort();
  }, [refreshCatalog]));

  const run = (label: string, action: (signal: AbortSignal) => Promise<void>) => actions.run(label, context => action(context.signal), {
    started: () => { Keyboard.dismiss(); setError(null); },
    failed: exception => setError(exception instanceof ApiError && exception.status === 409
      ? t("{0} Your draft is preserved.", t(exception.message))
      : exception instanceof Error ? exception.message : "The integration action failed.")
  });
  const refreshFromPull = useCallback(() => actions.run("catalog", context => refreshCatalog(context.signal)), [actions.run, refreshCatalog]);
  useScreenRefresh(refreshFromPull, loading || Boolean(busy));

  async function open(instance: IntegrationInstance, signal: AbortSignal) {
    const configuration = await api.getConfiguration(instance.id, signal);
    if (signal.aborted || !mounted.current || activeApi.current !== api) return;
    const provider = providers.find(item => samePackage(item, configuration.instance))
      ?? await api.getProvider(configuration.instance.providerId, configuration.instance.packageVersion, signal);
    if (signal.aborted || !mounted.current || activeApi.current !== api) return;
    if (!samePackage(provider, configuration.instance)) throw new Error(t("The integration's installed settings changed. Refresh the catalog before editing it."));
    setDraft(createIntegrationDraft(provider, configuration));
    setBindings([]);
    setSourceDrafts({});
    setPhaseDrafts({});
    setDiscovery(null);
    setTestResult(null);
    setOAuthNotice(null);
    setVersions([]);
    setTargetVersion(configuration.instance.packageVersion);
    setVersionError(null);
    const [devices, packages, sources] = await Promise.allSettled([api.getDevices(instance.id, signal), api.getProviderVersions(instance.providerId, signal), api.getSocketSources(signal)]);
    if (signal.aborted || !mounted.current || activeApi.current !== api) return;
    if (devices.status === "fulfilled") setBindings(devices.value);
    if (packages.status === "fulfilled") setVersions(packages.value.filter(item => item.providerId === instance.providerId));
    else setVersionError("Package versions could not be loaded. Current settings can still be edited.");
    if (sources.status === "fulfilled" && Array.isArray(sources.value)) { setSourceInverters(sources.value); setSourceError(null); }
    else setSourceError("Inverter links could not be loaded. Refresh the integration catalog before changing links.");
    if (devices.status === "rejected") throw devices.reason;
  }

  function patchValue(key: string, value: string) {
    setDraft(current => current && { ...current, oauth: undefined, values: { ...current.values, [key]: value } });
    setOAuthNotice(null);
    setDiscovery(null);
    setTestResult(null);
  }

  function patchSecret(key: string, value: SecretOperation) {
    setDraft(current => current && { ...current, oauth: undefined, secrets: { ...current.secrets, [key]: value } });
    setOAuthNotice(null);
    setDiscovery(null);
    setTestResult(null);
  }

  const selectedProvider = providers.find(provider => provider.providerId === providerId);
  const unsupported = draft ? unsupportedProvider(draft.provider) : null;
  const packageAvailable = !draft || samePackage(draft.provider, draft.configuration.instance);
  const formDisabled = Boolean(busy || unsupported || !packageAvailable);
  const changed = draft ? integrationDraftChanged(draft) : false;
  const providerOptions = (kind: "inverter" | "socket" | "other") => providers
    .filter(provider => kind === "other" ? !supportsDeviceKind(provider, "inverter") && !supportsDeviceKind(provider, "socket") : supportsDeviceKind(provider, kind))
    .map(provider => ({ value: provider.providerId, label: t(provider.displayName), disabled: Boolean(unsupportedProvider(provider)) }));
  function chooseProvider(id: string) {
    setProviderId(id);
    setName(providers.find(provider => provider.providerId === id)?.displayName ?? "");
  }

  function renderAction(action: string) {
    if (!draft) return null;
    if (action === "oauth") return <AppButton key={action} label={t("Authorize provider")} variant="secondary" disabled={formDisabled}
      loading={busy === "oauth"} onPress={() => void run("oauth", async signal => {
        const startingDraft = { ...draft, oauth: undefined };
        const change = integrationChange(startingDraft, { allowMissingOAuthSecrets: true });
        setDraft(startingDraft);
        setOAuthNotice(null);
        setDiscovery(null);
        setTestResult(null);
        const result = await authorizeIntegration(api, startingDraft.configuration.instance.id, change, signal);
        if (signal.aborted || !mounted.current || activeApi.current !== api) return;
        const authorized = result ? applyIntegrationOAuth(startingDraft, result) : startingDraft;
        setDraft(current => current === startingDraft ? authorized : current);
        setOAuthNotice(result ? "Authorization is ready in this draft. Save settings to retain it." : "Authorization was canceled. Settings were not saved.");
      })} />;
    if (action === "test") return <AppButton key={action} label={t("Test draft connection")} variant="secondary" disabled={formDisabled}
      loading={busy === "test"} onPress={() => void run("test", async signal => {
        const result = await api.test(draft.configuration.instance.id, integrationChange(draft), signal);
        if (!signal.aborted && mounted.current && activeApi.current === api) setTestResult(result);
      })} />;
    if (action === "discover") return <View key={action} style={styles.form}>
      <AppButton label={t("Discover draft devices")} variant="secondary" disabled={formDisabled}
        loading={busy === "discover"} onPress={() => void run("discover", async signal => {
          const result = await api.discover(draft.configuration.instance.id, integrationChange(draft), signal);
          if (!signal.aborted && mounted.current && activeApi.current === api) setDiscovery(result);
        })} />
      {discovery && !discovery.devices.length ? <Text style={styles.detail}>{t("Discovery returned no devices.")}</Text> : null}
      {discovery?.devices.map(device => <View key={device.selectionToken} style={styles.form}>
        <Text style={styles.title}>{device.name}</Text>
        <Text style={styles.detail}>{t(device.kind)} · {device.remoteId}{device.channel ? ` · ${device.channel}` : ""}</Text>
        <AppButton label={t("Use {0}", device.name)} variant="secondary"
          disabled={formDisabled || changed || !Number.isFinite(Date.parse(discovery.expiresAt)) || Date.parse(discovery.expiresAt) <= Date.now()}
          onPress={() => void run("select", async signal => {
            await api.selectDevice(draft.configuration.instance.id, integrationChange(draft), device.selectionToken, signal);
            await open(draft.configuration.instance, signal);
            if (!signal.aborted) await onSelectionChanged?.();
          })} />
      </View>)}
    </View>;
    return null;
  }

  return <>
    <SectionTitle title={t("Integrations")} />
    {isDemo ? <Card><Text style={styles.detail}>{t("Integration setup is available after connecting to your server. Demo mode uses sample devices.")}</Text></Card> : <>
      <ErrorBanner message={catalogError} />
      <ErrorBanner message={error} />
      {loading ? <Text style={styles.detail}>{t("Loading integrations...")}</Text> : null}
      {instances.map(instance => <Card key={instance.id} style={styles.form}>
        <Text style={styles.title}>{instance.name}</Text>
        <StatusPill label={instance.status === "enabled" ? t("Enabled") : instance.status === "disabled" ? t("Disabled") : t(instance.status)} tone={instance.status === "enabled" ? "success" : "neutral"} />
        <AppButton label={t("Configure {0}", instance.name)} variant="secondary" disabled={Boolean(busy)}
          onPress={() => void run("open", signal => open(instance, signal))} />
        {instance.status === "enabled" ? <AppButton label={t("Disable {0}", instance.name)} variant="secondary" disabled={Boolean(busy)}
          onPress={() => void run("disable", async signal => {
            const disabled = await api.setEnabled(instance.id, false, { expectedRevision: instance.revision,
              packageVersion: instance.packageVersion, packageDigest: instance.packageDigest, descriptorDigest: instance.descriptorDigest }, signal);
            if (signal.aborted || !mounted.current || activeApi.current !== api) return;
            setInstances(current => current.map(item => item.id === disabled.id ? disabled : item));
            setDraft(current => current?.configuration.instance.id === disabled.id
              ? { ...current, configuration: { ...current.configuration, instance: disabled } } : current);
            setDiscovery(null);
            await refreshSources(signal);
          })} /> : null}
      </Card>)}
      {!loading && !catalogError && !instances.length ? <EmptyState title={t("Add your first integration.")} /> : null}
      {providers.length ? <Card style={styles.form}>
        <Text style={styles.title}>{t("Add integration")}</Text>
        <IntegrationSelect label={t("Inverter manufacturer")} value={selectedProvider && supportsDeviceKind(selectedProvider, "inverter") ? providerId : ""}
          options={providerOptions("inverter")} disabled={Boolean(busy)} onChange={chooseProvider} />
        <IntegrationSelect label={t("Socket manufacturer")} value={selectedProvider && supportsDeviceKind(selectedProvider, "socket") ? providerId : ""}
          options={providerOptions("socket")} disabled={Boolean(busy)} onChange={chooseProvider} />
        {providerOptions("other").length ? <IntegrationSelect label={t("Other provider")} value={providerId}
          options={providerOptions("other")} disabled={Boolean(busy)} onChange={chooseProvider} /> : null}
        <Text style={styles.detail}>{t("Add another socket integration to connect devices from a different manufacturer.")}</Text>
        {selectedProvider ? <>
          <TextField label={t("Integration name")} value={name} onChangeText={setName} editable={!busy} />
          <AppButton label={t("Add integration")} disabled={Boolean(busy) || !name.trim()}
            onPress={() => void run("create", async signal => {
              const instance = await api.create(selectedProvider.providerId, name.trim(), signal);
              if (signal.aborted || !mounted.current || activeApi.current !== api) return;
              setInstances(current => [...current, instance]);
              await open(instance, signal);
            })} />
        </> : null}
      </Card> : null}
      {draft ? <Card style={styles.form}>
        <Text style={styles.title}>{draft.configuration.instance.name}</Text>
        <Text style={styles.detail}>{t(draft.provider.displayName)}</Text>
        <Text style={styles.detail}>{t("Installed package version: {0}", draft.configuration.instance.packageVersion)}</Text>
        <ErrorBanner message={versionError} />
        {versions.length > 1 ? <View style={styles.form}>
          <Text style={styles.title}>{t("Package version")}</Text>
          {versions.map(version => <AppButton key={`${version.packageVersion}:${version.packageDigest}`} label={t("Version {0}", version.packageVersion)}
            variant={targetVersion === version.packageVersion ? "primary" : "secondary"} disabled={Boolean(busy) || Boolean(unsupportedProvider(version))}
            onPress={() => setTargetVersion(version.packageVersion)} />)}
          <Text style={styles.detail}>{t("Switching replaces this form and discards its unsaved draft. Saved configuration is validated against the selected version; incompatible changes require a new integration.")}</Text>
          <AppButton label={t("Switch package version (discard draft)")} variant="secondary"
            disabled={Boolean(busy) || Boolean(unsupported) || !packageAvailable || targetVersion === draft.configuration.instance.packageVersion
              || !versions.some(version => version.packageVersion === targetVersion && !unsupportedProvider(version))}
            onPress={() => void run("package", async signal => {
              const saved = draft.configuration.instance;
              const instance = await api.switchPackage(saved.id, { expectedRevision: saved.revision,
                packageVersion: saved.packageVersion, packageDigest: saved.packageDigest, descriptorDigest: saved.descriptorDigest }, targetVersion, signal);
              if (signal.aborted || !mounted.current || activeApi.current !== api) return;
              setDraft(null);
              setBindings([]);
              setDiscovery(null);
              setTestResult(null);
              setInstances(current => current.map(item => item.id === instance.id ? instance : item));
              await open(instance, signal);
              if (!signal.aborted && mounted.current && activeApi.current === api) await onSelectionChanged?.();
            })} />
        </View> : null}
        <ErrorBanner message={unsupported || (!packageAvailable ? t("This integration version is no longer available. Your draft is preserved; refresh the catalog before continuing.") : null)} />
        <IntegrationFields draft={draft} disabled={formDisabled} onValue={patchValue} onSecret={patchSecret} renderAction={renderAction} />
        <AppButton label={t("Save integration settings")} disabled={formDisabled} loading={busy === "save"}
          onPress={() => void run("save", async signal => {
            const saved = await api.saveConfiguration(draft.configuration.instance.id, integrationChange(draft), signal);
            if (signal.aborted || !mounted.current || activeApi.current !== api) return;
            savedOAuthFlow.current = draft.oauth?.flowId ?? null;
            setDraft(createIntegrationDraft(draft.provider, saved));
            setOAuthNotice(null);
            setDiscovery(null);
            setTestResult(null);
            setInstances(current => current.map(instance => instance.id === saved.instance.id ? saved.instance : instance));
            await onSelectionChanged?.();
          })} />
        {testResult ? <Text style={styles.detail}>{t(testResult.message)}</Text> : null}
        {oauthNotice ? <Text style={styles.detail}>{t(oauthNotice)}</Text> : null}
        {busy === "oauth" ? <AppButton label={t("Cancel authorization")} variant="secondary" onPress={() => {
          actions.cancel();
          setOAuthNotice("Authorization was canceled. Settings were not saved.");
        }} /> : null}
        <Text style={styles.detail}>{t("Authorization, testing and discovery do not save settings or switch devices. Save settings before selecting a discovered device.")}</Text>
        <ErrorBanner message={sourceError} />
        {bindings.map(device => <View key={device.id} style={styles.form}>
          <Text style={styles.detail}>{device.name}{device.isDefault ? t(" · Selected") : ""}</Text>
          {device.kind === "socket" ? <>
            <IntegrationSelect label={t("Linked inverter for {0}", device.name)} value={sourceDrafts[device.id] ?? device.sourceInverterId ?? ""}
              options={[{ value: "", label: t("Installation default inverter") },
                ...(device.sourceInverterId && !sourceInverters.some(source => source.id === device.sourceInverterId)
                  ? [{ value: device.sourceInverterId, label: t("Unavailable inverter · {0}", device.sourceInverterId), disabled: true }] : []),
                ...sourceInverters.map(source => ({ value: source.id, label: source.name }))]}
              disabled={Boolean(busy || sourceError)} onChange={value => setSourceDrafts(current => ({ ...current, [device.id]: value }))} />
            <IntegrationSelect label={t("Circuit type for {0}", device.name)} value={phaseDrafts[device.id] ?? String(device.phaseCount ?? 1)}
              options={[{ value: "1", label: t("Single-phase") }, { value: "3", label: t("Three-phase") }]}
              disabled={Boolean(busy)} onChange={value => setPhaseDrafts(current => ({ ...current, [device.id]: value as "1" | "3" }))} />
            <Text style={styles.detail}>{t("Rules without an explicit source use this inverter. Circuit type describes the installed load.")}</Text>
            <AppButton translateLabel={false} label={t("Save link for {0}", device.name)} variant="secondary" disabled={Boolean(busy || sourceError)}
              onPress={() => void run("socket-source", async signal => {
                const saved = draft.configuration.instance;
                const updated = await api.setSocketSource(saved.id, device.id, {
                  guard: { expectedRevision: saved.revision, packageVersion: saved.packageVersion, packageDigest: saved.packageDigest, descriptorDigest: saved.descriptorDigest },
                  sourceInverterId: (sourceDrafts[device.id] ?? device.sourceInverterId) || null,
                  phaseCount: Number(phaseDrafts[device.id] ?? device.phaseCount ?? 1) as 1 | 3,
                  expectedSourceInverterId: device.sourceInverterId ?? null, expectedPhaseCount: device.phaseCount ?? 1
                }, signal);
                if (signal.aborted || !mounted.current || activeApi.current !== api) return;
                setBindings(current => current.map(binding => binding.id === updated.id ? updated : binding));
                const configuration = await api.getConfiguration(saved.id, signal);
                if (signal.aborted || !mounted.current || activeApi.current !== api) return;
                setDraft(current => current?.configuration.instance.id === saved.id ? { ...current,
                  configuration: { ...current.configuration, instance: configuration.instance } } : current);
                setInstances(current => current.map(instance => instance.id === saved.id ? configuration.instance : instance));
                await onSelectionChanged?.();
              })} />
          </> : null}
        </View>)}
        <AppButton label={draft.configuration.instance.status === "enabled" ? t("Disable integration") : t("Enable integration")}
          variant="secondary" disabled={Boolean(busy) || draft.configuration.instance.status !== "enabled" && (changed || Boolean(unsupported) || !packageAvailable)}
          onPress={() => void run("enabled", async signal => {
            const saved = draft.configuration.instance;
            const instance = await api.setEnabled(saved.id, saved.status !== "enabled",
              { expectedRevision: saved.revision, packageVersion: saved.packageVersion, packageDigest: saved.packageDigest,
                descriptorDigest: saved.descriptorDigest }, signal);
            if (signal.aborted || !mounted.current || activeApi.current !== api) return;
            setDraft(current => current && { ...current, configuration: { ...current.configuration, instance } });
            setInstances(current => current.map(item => item.id === instance.id ? instance : item));
            setDiscovery(null);
            await refreshSources(signal);
          })} />
        <AppButton label={t("Reload saved settings (discard draft)")} variant="secondary" disabled={Boolean(busy)}
          onPress={() => void run("reload", signal => open(draft.configuration.instance, signal))} />
      </Card> : null}
    </>}
  </>;
}

function samePackage(provider: IntegrationProvider, instance: IntegrationInstance): boolean {
  return provider.providerId === instance.providerId && provider.packageVersion === instance.packageVersion
    && provider.packageDigest === instance.packageDigest && provider.descriptorDigest === instance.descriptorDigest;
}

const styles = StyleSheet.create({
  form: { gap: spacing.md },
  row: { flexDirection: "row", flexWrap: "wrap", gap: spacing.sm },
  title: { color: colors.text, fontSize: typography.body, fontWeight: "700" },
  detail: { color: colors.muted, fontSize: typography.caption }
});
