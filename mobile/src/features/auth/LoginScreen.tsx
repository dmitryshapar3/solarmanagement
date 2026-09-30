import { useEffect, useRef, useState } from "react";
import { Keyboard, StyleSheet, Text, View } from "react-native";
import { LogIn, Server } from "lucide-react-native";
import { AppButton, Card, ErrorBanner, Screen, TextField } from "../../core/components";
import { colors, spacing, typography } from "../../core/theme";
import { useAuth } from "../../application/AuthContext";
import { openPublicLink, PUBLIC_PRIVACY_URL, PUBLIC_SUPPORT_URL } from "../../core/publicLinks";

export function LoginScreen() {
  const { apiBaseUrl, authError, login, enterDemo } = useAuth();
  const [baseUrl, setBaseUrl] = useState(apiBaseUrl);
  const [username, setUsername] = useState("admin");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState<"login" | "demo" | null>(null);
  const mounted = useRef(true);
  const submitting = useRef(false);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  async function handleLogin() {
    if (submitting.current) return;
    Keyboard.dismiss();
    submitting.current = true;
    setError(null);
    setPending("login");
    try {
      await login({ baseUrl, username, password });
    } catch (ex) {
      if (mounted.current) setError(ex instanceof Error ? ex.message : "Unable to sign in.");
    } finally {
      submitting.current = false;
      if (mounted.current) {
        setPassword("");
        setPending(null);
      }
    }
  }

  async function handleDemo() {
    if (submitting.current) return;
    Keyboard.dismiss();
    submitting.current = true;
    setError(null);
    setPending("demo");
    try {
      await enterDemo();
    } catch (ex) {
      if (mounted.current) setError(ex instanceof Error ? ex.message : "Unable to open the demo.");
    } finally {
      submitting.current = false;
      if (mounted.current) {
        setPassword("");
        setPending(null);
      }
    }
  }

  async function openLink(url: string) {
    try {
      await openPublicLink(url);
    } catch (ex) {
      if (mounted.current) setError(ex instanceof Error ? ex.message : "The link could not be opened.");
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
          returnKeyType="go"
          onSubmitEditing={handleLogin}
        />
        <ErrorBanner message={error ?? authError} />
        <AppButton label="Sign in" icon={LogIn} onPress={handleLogin} loading={pending === "login"} disabled={Boolean(pending)} />
        <AppButton label="Try demo" onPress={handleDemo} variant="secondary" loading={pending === "demo"} disabled={Boolean(pending)} />
        <Text style={styles.warning}>Explore sample data. Demo changes stay in this session and never affect real devices.</Text>
      </Card>
      <View style={styles.links}>
        <AppButton label="Privacy policy" variant="ghost" compact onPress={() => void openLink(PUBLIC_PRIVACY_URL)} />
        <AppButton label="Support" variant="ghost" compact onPress={() => void openLink(PUBLIC_SUPPORT_URL)} />
      </View>
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
  links: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: spacing.md
  },
  warning: {
    color: colors.muted,
    fontSize: typography.caption,
    lineHeight: 20
  }
});
