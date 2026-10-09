import { useEffect, useState } from "react";
import { Pressable, ScrollView, View } from "react-native";
import { useLanguage } from "../../application/LanguageContext";
import { AppButton, Card, TextField, ThemedText as Text } from "../../core/components";
import { useTheme } from "../../ui/theme/ThemeProvider";
import { addDays } from "./chartPolicy";
import { PeriodNavigation } from "./EnergyControls";
import { canMoveEnergySelection, chooseEnergyPeriod, energyCaption, energyPeriodChoices, energySelectionError, energySelectionKey, energyWindow, moveEnergySelection, type EnergySelection } from "./energySelection";

export function EnergyPeriodFilters({ selection, today, onChange }: {
  selection: EnergySelection; today: string; onChange: (selection: EnergySelection) => void;
}) {
  const { t } = useLanguage(); const { colors } = useTheme();
  const current = energyWindow(selection, today);
  const [date, setDate] = useState(selection.period === "CalendarMonth" ? current.date.slice(0, 7) : current.date); const [from, setFrom] = useState(current.from); const [through, setThrough] = useState(current.through);
  const [error, setError] = useState<string | null>(null);
  const key = energySelectionKey(selection);
  useEffect(() => { setDate(selection.period === "CalendarMonth" ? current.date.slice(0, 7) : current.date); setFrom(current.from); setThrough(current.through); setError(null); }, [key, today]);
  const choose = (value: EnergySelection) => {
    const validation = energySelectionError(value, today); setError(validation);
    if (!validation) onChange(value);
  };
  const apply = () => choose(selection.period === "Custom" ? { period: "Custom", date: from, from, through }
    : { period: selection.period, date: selection.period === "CalendarMonth" ? `${date}-01` : date });
  return <View testID="energy-period-filters" style={{ gap: 12, minWidth: 0 }}>
    <ScrollView horizontal showsHorizontalScrollIndicator={false} accessibilityLabel={t("Chart period")}
      style={{ flexGrow: 0 }} contentContainerStyle={{ gap: 4, padding: 4, backgroundColor: colors.fill, borderRadius: 12 }}>
      {energyPeriodChoices.map(choice => <Pressable key={choice.period} accessibilityRole="button" accessibilityLabel={t(choice.label)}
        accessibilityState={{ selected: choice.period === selection.period }} onPress={() => choose(chooseEnergyPeriod(selection, choice.period, today))}
        style={{ minHeight: 44, paddingHorizontal: 14, borderRadius: 9, alignItems: "center", justifyContent: "center", backgroundColor: choice.period === selection.period ? colors.surface : "transparent" }}>
        <Text style={{ fontSize: 15, fontWeight: "600", color: choice.period === selection.period ? colors.ink : colors.ink2 }}>{t(choice.label)}</Text>
      </Pressable>)}
    </ScrollView>
    <PeriodNavigation caption={energyCaption(selection, today)} previous={canMoveEnergySelection(selection, today, -1)} next={canMoveEnergySelection(selection, today, 1)}
      onPrevious={() => choose(moveEnergySelection(selection, today, -1))} onNext={() => choose(moveEnergySelection(selection, today, 1))} onToday={() => choose({ period: "Day", date: today })} />
    <Card style={{ gap: 12, padding: 16 }}>
      {selection.period === "Custom" ? <>
        <TextField label="From" value={from} onChangeText={setFrom} placeholder={today} maxLength={10} error={error} />
        <TextField label="To (inclusive)" value={through} onChangeText={setThrough} placeholder={today} maxLength={10} helper="Inclusive · up to 366 days" />
      </> : <TextField label={selection.period === "CalendarMonth" ? "Month" : "Date"}
        value={date} onChangeText={setDate}
        placeholder={selection.period === "CalendarMonth" ? today.slice(0, 7) : today} maxLength={selection.period === "CalendarMonth" ? 7 : 10} error={error}
        returnKeyType="done" onSubmitEditing={apply} />}
      <AppButton label="Apply" variant="secondary" compact onPress={apply} />
    </Card>
    <View style={{ flexDirection: "row", flexWrap: "wrap", gap: 16 }}>
      {[{ label: "Next 7 days", days: 6, period: "Week" }, { label: "Next 30 days", days: 29, period: "RollingMonth" }].map(shortcut =>
        <Pressable key={shortcut.period} accessibilityRole="button" accessibilityLabel={t(shortcut.label)}
          onPress={() => choose({ period: shortcut.period as EnergySelection["period"], date: addDays(today, shortcut.days) })}
          style={{ minHeight: 44, justifyContent: "center" }}><Text style={{ fontSize: 13, fontWeight: "600", color: colors.ink }}>{t(shortcut.label)}</Text></Pressable>)}
    </View>
  </View>;
}
