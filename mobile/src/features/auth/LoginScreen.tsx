import { useEffect, useRef, useState } from "react";
import { StyleSheet, Text, View } from "react-native";
import { LogIn, Server } from "lucide-react-native";
import { AppButton, Card, ErrorBanner, Screen, TextField } from "../../core/components";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";

export function LoginScreen() {
  const { apiBaseUrl, authError, login } = useAuth();
  const [baseUrl, setBaseUrl] = useState(apiBaseUrl);
  const [username, setUsername] = useState("admin");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const mounted = useRef(true);
  const submitting = useRef(false);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  async function handleLogin() {
    if (submitting.current) return;
    submitting.current = true;
    setError(null);
    setLoading(true);
    try {
      await login({ baseUrl, username, password });
    } catch (ex) {
      if (mounted.current) setError(ex instanceof Error ? ex.message : "Unable to sign in.");
    } finally {
      submitting.current = false;
      if (mounted.current) {
        setPassword("");
        setLoading(false);
      }
    }
  }

  return (
    <Screen>
      <View style={styles.brand}>
        <View style={styles.brandIcon}>
          <Server color={colors.primary} size={28} strokeWidth={2.2} />
        </View>
        <Text style={styles.title}>DeyeSolar</Text>
        <Text style={styles.subtitle}>Solar Energy Management</Text>
      </View>

      <Card style={styles.form}>
        <TextField label="API URL" value={baseUrl} onChangeText={setBaseUrl} placeholder="https://solar.dshapar.com" />
        {baseUrl.trim().toLowerCase().startsWith("http:") && (
          <Text style={styles.warning}>HTTP is unencrypted. Use it only for a trusted local development server.</Text>
        )}
        <TextField label="Username" value={username} onChangeText={setUsername} placeholder="admin" />
        <TextField
          label="Password"
          value={password}
          onChangeText={setPassword}
          secureTextEntry
          placeholder="Password"
        />
        <ErrorBanner message={error ?? authError} />
        <AppButton label="Sign in" icon={LogIn} onPress={handleLogin} loading={loading} />
      </Card>
    </Screen>
  );
}

const styles = StyleSheet.create({
  brand: {
    paddingTop: spacing.xxl,
    gap: spacing.md
  },
  brandIcon: {
    width: 54,
    height: 54,
    borderRadius: 8,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: colors.surfaceRaised,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: colors.border
  },
  title: {
    color: colors.text,
    fontSize: 34,
    fontWeight: "800"
  },
  subtitle: {
    color: colors.muted,
    fontSize: typography.body,
    lineHeight: 22
  },
  form: {
    gap: spacing.lg
  },
  warning: {
    color: colors.muted,
    fontSize: typography.caption,
    lineHeight: 20
  }
});
