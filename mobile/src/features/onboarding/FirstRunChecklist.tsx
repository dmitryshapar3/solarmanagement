import { Check, PlugZap, Sun } from "lucide-react-native";
import { View } from "react-native";
import { useLanguage } from "../../application/LanguageContext";
import { AppButton, Card, SectionTitle, ThemedText as Text } from "../../core/components";
import { useTheme } from "../../ui/theme/ThemeProvider";

export function FirstRunChecklist({ inverter, site, plug, onInverter, onSite, onPlug, onExplore }: { inverter: boolean; site: boolean; plug: boolean; onInverter: () => void; onSite: () => void; onPlug: () => void; onExplore: () => void }) {
  const { colors } = useTheme(); const { t } = useLanguage();
  return <><Card style={{ gap: 14, backgroundColor: colors.sun }}><Text style={{ color: colors.onSun, fontSize: 28, lineHeight: 34, fontWeight: "700" }}>{t("Let’s connect your solar")}</Text><Text style={{ color: colors.onSun, fontSize: 15 }}>{t("Add your inverter to see live power and weather estimates for your roof.")}</Text></Card>
    <SectionTitle title="Your setup" />
    {[{ label: "Connect an inverter", detail: "Read your solar and battery measurements.", done: inverter, action: onInverter }, { label: "Set up your solar site", detail: "Location, roof direction and installed capacity.", done: site, action: onSite }, { label: "Connect a smart plug", detail: "Optional · use your solar automatically.", done: plug, action: onPlug }].map((step, i) => <Card key={step.label} style={{ gap: 12 }}><View style={{ flexDirection: "row", alignItems: "center", gap: 12 }}><View style={{ width: 40, height: 40, borderRadius: 20, alignItems: "center", justifyContent: "center", backgroundColor: step.done ? colors.sun : colors.fill }}>{step.done ? <Check size={20} color={colors.onSun} /> : <Text style={{ fontWeight: "700" }}>{i + 1}</Text>}</View><View style={{ flex: 1 }}><Text style={{ fontWeight: "600" }}>{t(step.label)}</Text><Text style={{ color: colors.ink2, fontSize: 13 }}>{t(step.detail)}</Text></View></View>{!step.done ? <AppButton label={step.label} variant={i === 0 ? "primary" : "secondary"} icon={i === 2 ? PlugZap : Sun} onPress={step.action} /> : null}</Card>)}
    <AppButton label="Explore sample data" variant="ghost" onPress={onExplore} />
  </>;
}
