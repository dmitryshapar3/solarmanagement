import { AppErrorBoundary } from "./src/application/AppErrorBoundary";
import { StatusBar } from "react-native";
import { SafeAreaProvider } from "react-native-safe-area-context";
import { AppProviders } from "./src/application/AppProviders";
import { AppNavigator } from "./src/application/AppNavigator";
import { useTheme } from "./src/ui/theme/ThemeProvider";

function ThemedStatusBar() {
  const { colors, scheme } = useTheme();
  return <StatusBar barStyle={scheme === "dark" ? "light-content" : "dark-content"} backgroundColor={colors.bg} />;
}

export default function App() {
  return (
    <AppErrorBoundary>
    <SafeAreaProvider>
      <AppProviders>
        <ThemedStatusBar />
        <AppNavigator />
      </AppProviders>
    </SafeAreaProvider>
    </AppErrorBoundary>
  );
}
