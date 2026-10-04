import { useAuth } from "../../application/AuthContext";
import { useLanguage } from "../../application/LanguageContext";

const fixtureNames = new Set(["Demo water heater", "Demo garden lights", "Demo solar surplus", "Demo battery reserve", "Demo manual override", "Demo rooftop", "Demo workshop", "Fictional installation"]);

/** Fixture names stay canonical in memory and follow the language only in demo screens. */
export function useDemoDisplayName() {
  const { isDemo } = useAuth();
  const { t } = useLanguage();
  return (name: string) => isDemo && fixtureNames.has(name) ? t(name) : name;
}
