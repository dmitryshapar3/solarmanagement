import { useLanguage } from "../../application/LanguageContext";
import type { Device } from "../../core/api/types";
import { StatusPill } from "../../core/components";
import { deviceStatePresentation } from "./deviceStatePresentation";

export function DeviceStatusPill({ device }: { device: Device }) {
  const { t } = useLanguage();
  const state = deviceStatePresentation(device);
  return <StatusPill label={t(state.label)} tone={state.tone} />;
}
