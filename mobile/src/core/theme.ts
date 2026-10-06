import { colors as palettes, designTokens } from "../ui/theme/tokens";
// Legacy modules are migrated to useLegacyTheme as they move to the new views.
const light = palettes.light;
export const colors = { background: light.bg, surface: light.surface, surfaceRaised: light.fill,
  border: light.line, text: light.ink, muted: light.ink2, subtle: light.ink3,
  primary: light.ink, primaryDark: light.switchOnTrack, blue: light.grid, amber: light.solar,
  red: light.criticalText, off: light.switchOffTrack, white: light.surface };
export const spacing = { xs: 4, sm: 8, md: 12, lg: 16, xl: 24, xxl: 32 } as const;
export const radius = { sm: designTokens.radius.chip, md: designTokens.radius.control, lg: designTokens.radius.card };
export const typography = { title: 34, section: 19, body: 17, caption: 13, metric: 30 };
