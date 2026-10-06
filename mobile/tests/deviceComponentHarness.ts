import { createRequire } from "node:module";
import path from "node:path";
import { build } from "esbuild";

export async function deviceComponents() {
  const bundle = await build({ stdin: { contents: 'export { DeviceSheetScreen } from "./src/features/devices/DeviceSheetScreen"; export { DeviceRow } from "./src/features/devices/DeviceRow";', resolveDir: process.cwd(), loader: "ts" }, bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime", "expo-crypto"], plugins: [{ name: "device-native-boundaries", setup(builder) {
    builder.onResolve({ filter: /^(react-native|lucide-react-native|@react-navigation\/native|expo-clipboard)$|(?:^|\/)(AuthContext|LanguageContext|ThemeProvider|core\/components|ScreenRefreshContext)$/ }, args => ({ path: args.path, namespace: "device-ui" }));
    builder.onLoad({ filter: /.*/, namespace: "device-ui" }, args => ({ loader: "js", contents:
      args.path === "react-native" ? 'export const View="View",Pressable="Pressable",Modal="Modal"; export const AppState={currentState:"active",addEventListener:()=>({remove(){}})};'
      : args.path === "@react-navigation/native" ? 'import React from "react"; export const useFocusEffect=callback=>React.useEffect(callback,[callback]);export const useNavigation=()=>({navigate(){},goBack(){}});'
      : args.path.endsWith("AuthContext") ? 'export const useAuth=()=>globalThis.__deviceUiAuth;'
      : args.path.endsWith("LanguageContext") ? 'export const useLanguage=()=>({language:"en",t:(key,...args)=>args.reduce((text,value,i)=>text.split("{"+i+"}").join(value),key)});'
      : args.path.endsWith("ThemeProvider") ? 'export const useTheme=()=>({colors:new Proxy({}, {get:()=>"#111"})});'
      : args.path.endsWith("ScreenRefreshContext") ? 'export const useScreenRefresh=()=>{};'
      : args.path === "expo-clipboard" ? 'export const setStringAsync=async()=>{};'
      : args.path === "lucide-react-native" ? 'export const PlugZap=()=>null,Copy=()=>null,X=()=>null;'
      : 'import React from "react";export const AppButton=props=>React.createElement("button",props,props.label); export const ThemedText="Text",NativeSwitch="NativeSwitch",TextField="TextField",Card="Card",Banner="Banner",DataRow="DataRow",ErrorBanner="ErrorBanner",Group="Group",Header="Header",IconButton="IconButton",LoadingState="LoadingState",NavigationRow="NavigationRow",Screen="Screen",SectionTitle="SectionTitle",SegmentedControl="SegmentedControl",StatusPill="StatusPill";'
    }));
  } }] });
  const module = { exports: {} as any };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports;
}
