export function uiFont(weight: string | number = "500", language?: string) {
  if (language && ["zh", "ja", "ko"].includes(language.split("-")[0] ?? "")) return undefined;
  return Number(weight) >= 700 ? "Onest-Bold" : Number(weight) >= 600 ? "Onest-SemiBold" : Number(weight) >= 500 ? "Onest-Medium" : "Onest-Regular";
}
