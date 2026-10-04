import { Component, type ReactNode } from "react";
import { Pressable, Text, View } from "react-native";
import { translate as t } from "../core/i18n";
import { colors, spacing } from "../core/theme";
// A malformed view cannot blank the whole application or expose technical exception text.
export class AppErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() {
    if (!this.state.failed) return this.props.children;
    return <View style={{ flex: 1, justifyContent: "center", padding: spacing.xl, gap: spacing.lg, backgroundColor: colors.background }}>
      <Text accessibilityRole="alert" style={{ color: colors.text }}>{t("Action failed.")}</Text>
      <Pressable accessibilityRole="button" accessibilityLabel={t("Retry")} onPress={() => this.setState({ failed: false })}
        style={{ padding: spacing.lg, backgroundColor: colors.surface }}><Text style={{ color: colors.primary }}>{t("Retry")}</Text></Pressable>
    </View>;
  }
}
