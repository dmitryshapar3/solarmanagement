import { ReactNode } from "react";
import { AuthProvider } from "./AuthContext";
import { LanguageProvider } from "./LanguageContext";

export function AppProviders({ children }: { children: ReactNode }) {
  return <AuthProvider><LanguageProvider>{children}</LanguageProvider></AuthProvider>;
}
