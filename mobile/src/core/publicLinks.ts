import { Linking } from "react-native";

export const PUBLIC_PRIVACY_URL = "https://solar.dshapar.com/privacy";
export const PUBLIC_SUPPORT_URL = "https://solar.dshapar.com/support";
export const PUBLIC_TERMS_URL = "https://www.apple.com/legal/internet-services/itunes/dev/stdeula/";

export async function openPublicLink(value: string): Promise<void> {
  try {
    const url = new URL(value);
    if (url.protocol !== "https:" || url.username || url.password) throw new Error("Invalid public URL");
    await Linking.openURL(url.toString());
  } catch {
    throw new Error("The link could not be opened. Please try again or contact support.");
  }
}
