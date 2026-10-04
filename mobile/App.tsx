import { AppErrorBoundary } from "./src/application/AppErrorBoundary";
import { StatusBar } from "react-native";
import { SafeAreaProvider } from "react-native-safe-area-context";
import { AppProviders } from "./src/application/AppProviders";
import { AppNavigator } from "./src/application/AppNavigator";

export default function App() {
  return (
    <AppErrorBoundary>
    <SafeAreaProvider>
      <AppProviders>
        <StatusBar barStyle="light-content" />
        <AppNavigator />
      </AppProviders>
    </SafeAreaProvider>
    </AppErrorBoundary>
  );
}
