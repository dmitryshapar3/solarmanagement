import AsyncStorage from "@react-native-async-storage/async-storage";
import { createContext, type ReactNode, useContext, useEffect, useMemo, useRef, useState } from "react";
import { Appearance as NativeAppearance, useColorScheme } from "react-native";
import { colors, designTokens, type Appearance, type ThemeColors } from "./tokens";

export const APPEARANCE_STORAGE_KEY = "solar.appearance.v1";
type ThemeValue = { colors: ThemeColors; scheme: "light" | "dark"; appearance: Appearance; setAppearance(value: Appearance): Promise<void> };
const ThemeContext = createContext<ThemeValue>({ colors: colors.light, scheme: "light", appearance: "light", setAppearance: async () => {} });

export function resolveAppearance(value: Appearance, system: string | null | undefined): "light" | "dark" {
  return value === "system" ? system === "dark" ? "dark" : "light" : value;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const system = useColorScheme();
  const preferenceRevision = useRef(0);
  const [appearance, setValue] = useState<Appearance>("light");
  useEffect(() => {
    let current = true; const startedAt = preferenceRevision.current;
    void AsyncStorage.getItem(APPEARANCE_STORAGE_KEY).then(value => {
      if (current && preferenceRevision.current === startedAt && (value === "light" || value === "dark" || value === "system")) setValue(value);
    }).catch(() => {});
    return () => { current = false; };
  }, []);
  useEffect(() => { NativeAppearance.setColorScheme(appearance === "system" ? "unspecified" : appearance); }, [appearance]);
  const scheme = resolveAppearance(appearance, system);
  const value = useMemo<ThemeValue>(() => ({ colors: colors[scheme], scheme, appearance,
    setAppearance: async next => { ++preferenceRevision.current; setValue(next); await AsyncStorage.setItem(APPEARANCE_STORAGE_KEY, next); }
  }), [appearance, scheme]);
  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme() { return useContext(ThemeContext); }

// Transitional aliases let proven policy views adopt the new theme while their
// presentation is moved to the shared SmartSolar vocabulary.
export function legacyColors(palette: ThemeColors) {
  return { ...palette, background: palette.bg, surface: palette.surface, surfaceRaised: palette.fill,
    border: palette.line, text: palette.ink, muted: palette.ink2, subtle: palette.ink3,
    primary: palette.ink, primaryDark: palette.switchOnTrack, blue: palette.grid,
    amber: palette.solar, red: palette.criticalText, off: palette.switchOffTrack,
    white: palette.surface, solar: palette.solar, battery: palette.battery, sun: palette.sun,
    onPrimary: palette.bg };
}
export function useLegacyTheme() {
  const theme = useTheme();
  return useMemo(() => ({ ...theme, colors: legacyColors(theme.colors), spacing: { xs: 4, sm: 8, md: 12, lg: 16, xl: 24, xxl: 32 },
    radius: { sm: designTokens.radius.chip, md: designTokens.radius.control, lg: designTokens.radius.card },
    typography: { title: 34, section: 19, body: 17, caption: 13, metric: 30 } }), [theme]);
}
