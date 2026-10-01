import * as Crypto from "expo-crypto";
import * as WebBrowser from "expo-web-browser";
import { GOOGLE_CALLBACK, googleCallbackCode, googleStartUrl } from "./googleFlow";

export async function googleSignIn(baseUrl: string,
  linkStart?: (codeChallenge: string, state: string) => Promise<{ authorizationUrl: string }>
): Promise<{ code: string; codeVerifier: string } | null> {
  const random = async () => Array.from(await Crypto.getRandomBytesAsync(32), byte => byte.toString(16).padStart(2, "0")).join("");
  const codeVerifier = await random();
  const state = await random();
  const codeChallenge = (await Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, codeVerifier,
    { encoding: Crypto.CryptoEncoding.BASE64 })).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  let url = googleStartUrl(baseUrl, codeChallenge, state);
  if (linkStart) {
    const start = await linkStart(codeChallenge, state);
    const authorized = new URL(start.authorizationUrl);
    const expected = new URL(baseUrl);
    if (authorized.protocol !== "https:" || authorized.origin !== expected.origin || authorized.username || authorized.password
      || !authorized.pathname.endsWith("/auth/google")) throw new Error("The account linking URL could not be verified.");
    url = authorized.toString();
  }
  const result = await WebBrowser.openAuthSessionAsync(url, GOOGLE_CALLBACK, { preferEphemeralSession: true });
  if (result.type !== "success") return null;
  return { code: googleCallbackCode(result.url, state), codeVerifier };
}
