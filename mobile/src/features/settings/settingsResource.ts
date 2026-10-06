import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "../../application/AuthContext";
import { useScopedAction } from "../../application/useScopedAction";
import type { ScopedActionContext } from "../../application/ScopedActionScope";
import type { SolarSiteSettings } from "../../core/api/types";
import type { IntegrationSourceInverter } from "../../core/api/IntegrationApi";

export type InstallationSettings = { site: SolarSiteSettings; polling: { intervalSeconds: number }; display: { timeZoneId: string };
  primaryInverterId: string | null; inverters: IntegrationSourceInverter[]; version: string; integrationVersions: Record<string, { expectedRevision: number; packageVersion: string; packageDigest: string; descriptorDigest: string }> };
export function useInstallationSettings() {
  const { api, apiBaseUrl, isDemo } = useAuth(); const [saved, setSaved] = useState<InstallationSettings | null>(null); const [draft, setDraft] = useState<InstallationSettings | null>(null);
  const [error, setError] = useState<string | null>(null); const [loading, setLoading] = useState(true); const [notice, setNotice] = useState<string | null>(null); const [canEdit, setCanEdit] = useState(false);
  const actions = useScopedAction(api, apiBaseUrl, () => { setSaved(null); setDraft(null); setError(null); setLoading(true); setNotice(null); setCanEdit(false); });
  const load = useCallback(() => actions.run("load", async context => {
    const [value, permissions] = await Promise.all([api.request<InstallationSettings>("/api/settings/installation", { signal: context.signal }), api.accountSecurity.getPermissions(context.signal)]);
    context.publish(() => { setSaved(value); setDraft(value); setLoading(false); setCanEdit(permissions.permissions.includes("ManageSettings")); });
  }, { started: () => setError(null), failed: e => { setError(e instanceof Error ? e.message : "Settings could not be loaded."); setLoading(false); } }), [api, actions.run]);
  useEffect(() => { void load(); }, [load]);
  const dirty = useMemo(() => saved !== null && JSON.stringify(saved) !== JSON.stringify(draft), [saved, draft]);
  const run = useCallback((label: string, action: (context: ScopedActionContext) => Promise<void>) => actions.run(label, action, { started: () => { setError(null); setNotice(null); }, failed: e => setError(e instanceof Error ? e.message : "Settings could not be saved.") }), [actions.run]);
  const save = () => run("save", async context => { if (!draft || !saved || !canEdit) return;
    const value = await api.request<InstallationSettings>("/api/settings/installation", { method: "PUT", signal: context.signal, body: { site: draft.site, polling: draft.polling, display: draft.display, primaryInverterId: draft.primaryInverterId, expectedVersion: saved.version, expectedIntegrationVersions: saved.integrationVersions } });
    context.publish(() => { setSaved(value); setDraft(value); setNotice("Settings saved"); });
  });
  return { api, isDemo, saved, draft, setDraft, error, setError, loading, notice, setNotice, canEdit, dirty, busy: actions.busy, load, run, save };
}
