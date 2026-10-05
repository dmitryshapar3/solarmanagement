import { StyleSheet } from "react-native";
import { spacing } from "./theme";

export const listCardHeaderStyles = StyleSheet.create({
  topRow: { flexDirection: "row", alignItems: "flex-start", justifyContent: "space-between", gap: spacing.md },
  titleGroup: { flex: 1, gap: spacing.xs }
});
