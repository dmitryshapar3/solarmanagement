import { ReactNode } from "react";
import { AuthProvider } from "./AuthContext";
import { LanguageProvider } from "./LanguageContext";
import { ThemeProvider } from "../ui/theme/ThemeProvider";
import { FontBootstrap } from "../ui/theme/FontBootstrap";

export function AppProviders({ children }: { children: ReactNode }) {
  return <ThemeProvider><FontBootstrap><AuthProvider><LanguageProvider>{children}</LanguageProvider></AuthProvider></FontBootstrap></ThemeProvider>;
}
