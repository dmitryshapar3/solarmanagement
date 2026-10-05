import { ActivityIndicator, Pressable, StyleSheet } from "react-native";
import Svg, { Path } from "react-native-svg";
import { useLanguage } from "../../application/LanguageContext";

export function GoogleSignInButton({ onPress, loading, disabled }: {
  onPress: () => void; loading?: boolean; disabled?: boolean;
}) {
  const { t } = useLanguage();
  return <Pressable accessibilityRole="button" accessibilityLabel={t("Continue with Google")}
    accessibilityState={{ disabled: Boolean(disabled || loading), busy: Boolean(loading) }}
    disabled={disabled || loading} onPress={onPress}
    style={({ pressed }) => [styles.button, (pressed || disabled || loading) && styles.disabled]}>
    {loading ? <ActivityIndicator color="#4285F4" /> : <Svg width={24} height={24} viewBox="0 0 24 24" accessibilityElementsHidden>
      <Path fill="#4285F4" d="M21.6 12.23c0-.71-.06-1.39-.18-2.05H12v3.88h5.38a4.6 4.6 0 0 1-2 3.02v2.51h3.24c1.9-1.75 2.98-4.33 2.98-7.36Z" />
      <Path fill="#34A853" d="M12 22c2.7 0 4.96-.9 6.62-2.41l-3.24-2.51c-.9.6-2.04.96-3.38.96-2.61 0-4.83-1.76-5.62-4.12H3.03v2.59A10 10 0 0 0 12 22Z" />
      <Path fill="#FBBC05" d="M6.38 13.92a6 6 0 0 1 0-3.84V7.49H3.03a10 10 0 0 0 0 9.02l3.35-2.59Z" />
      <Path fill="#EA4335" d="M12 5.96c1.47 0 2.79.51 3.83 1.51l2.87-2.87A9.62 9.62 0 0 0 12 2a10 10 0 0 0-8.97 5.49l3.35 2.59C7.17 7.72 9.39 5.96 12 5.96Z" />
    </Svg>}
  </Pressable>;
}

const styles = StyleSheet.create({
  button: { width: 52, height: 52, borderRadius: 26, alignSelf: "center", alignItems: "center", justifyContent: "center",
    backgroundColor: "#FFFFFF", borderWidth: 1, borderColor: "#DADCE0" },
  disabled: { opacity: 0.5 }
});
