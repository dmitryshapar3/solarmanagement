import * as AppleAuthentication from "expo-apple-authentication";
import * as Crypto from "expo-crypto";
import { Platform } from "react-native";

export async function appleAvailable(): Promise<boolean> {
  return Platform.OS === "ios" && await AppleAuthentication.isAvailableAsync();
}
export async function appleIdentity(rawNonce?: string) {
  if (!await appleAvailable()) throw new Error("Apple sign-in is unavailable on this device.");
  const nonce = rawNonce ?? Array.from(await Crypto.getRandomBytesAsync(32), byte => byte.toString(16).padStart(2, "0")).join("");
  const hashedNonce = await Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, nonce);
  try {
    const credential = await AppleAuthentication.signInAsync({ requestedScopes: [AppleAuthentication.AppleAuthenticationScope.FULL_NAME, AppleAuthentication.AppleAuthenticationScope.EMAIL], nonce: hashedNonce });
    if (!credential.identityToken || !credential.authorizationCode) throw new Error("Apple did not return a complete authorization.");
    return { identityToken: credential.identityToken, authorizationCode: credential.authorizationCode, rawNonce: nonce,
      name: credential.fullName ? [credential.fullName.givenName, credential.fullName.familyName].filter(Boolean).join(" ") : undefined };
  } catch (error) {
    if ((error as { code?: string }).code === "ERR_REQUEST_CANCELED") return null;
    throw error;
  }
}
