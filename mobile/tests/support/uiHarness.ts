import { build } from "esbuild";
import { createRequire } from "node:module";
import path from "node:path";
import type React from "react";
export const globals = globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean; __smartUi?: { auth: any; resources?: Record<string, any>; route?: any; prevention?: any } };
export const translateSource = 'export const translate=(phrase,...args)=>(phrase??"").replace(/\\{(\\d+)\\}/g,(token,index)=>args[Number(index)]===undefined?token:String(args[Number(index)]??""));';
export async function uiHarness(entries: string, options: { stubComponents?: boolean; resources?: boolean; stubs?: Record<string, string> } = {}) {
  const bundle = await build({ stdin: { contents: entries, resolveDir: process.cwd(), loader: "ts" }, bundle: true, write: false, platform: "node", format: "cjs", external: ["react", "react/jsx-runtime"], define: { __DEV__: "false" },
    plugins: [{ name: "smartsolar-native-boundaries", setup(builder) {
      const modules: Record<string, string> = {
        "react-native": `export const Appearance={setColorScheme(){} };export const AppState={currentState:"active",addEventListener:()=>({remove(){}})},Platform={OS:"ios"};export const useColorScheme=()=>"light";export const StyleSheet={create:value=>value,flatten:value=>Array.isArray(value)?Object.assign({},...value.filter(Boolean)):value,hairlineWidth:1};export const Text="Text",View="View",Pressable="Pressable",ScrollView="ScrollView",FlatList="FlatList",RefreshControl="RefreshControl",Modal="Modal",Image="Image",ActivityIndicator="ActivityIndicator",Switch="Switch",TextInput="TextInput";export const Linking={openURL:async()=>{}},Keyboard={dismiss(){}},Alert={alert(){}};`,
        "react-native-svg": 'export default "Svg";export const Svg="Svg",Text="SvgText",Circle="Circle",G="G",Line="Line",Path="Path",Rect="Rect",Defs="Defs",ClipPath="ClipPath";',
        "react-native-safe-area-context": 'export const SafeAreaView="SafeAreaView",useSafeAreaInsets=()=>({top:0,bottom:0});',
        "@react-navigation/elements": 'import React from "react"; export const HeaderHeightContext=React.createContext(0);',
        "@react-navigation/native": 'import React from "react";export const useFocusEffect=callback=>React.useEffect(callback,[callback]);export const useNavigation=()=>({navigate(){},dispatch(){}});export const useRoute=()=>globalThis.__smartUi.route??({params:{}});export const usePreventRemove=(enabled,callback)=>{globalThis.__smartUi.prevention={enabled,callback}};',
        "@react-native-async-storage/async-storage": 'export default {getItem:async()=>null,setItem:async()=>{},removeItem:async()=>{}};',
        "expo-crypto": 'export const randomUUID=()=>"00000000-0000-0000-0000-000000000099";',
        "expo-clipboard": 'export const setStringAsync=async()=>{};',
        "expo-blur": 'export const BlurView="BlurView";'
      };
      builder.onResolve({ filter: /^expo-(?:location|file-system|sharing)$/ }, args => options.stubs?.[args.path] ? { path: args.path, namespace: "smart-stub" } : undefined);
      builder.onResolve({ filter: /^(react-native(?:-svg|-safe-area-context)?|@react-navigation\/(?:native|elements)|@react-native-async-storage\/async-storage|expo-(?:crypto|clipboard|blur))$/ }, args => ({ path: args.path, namespace: "smart-native" }));
      builder.onResolve({ filter: /^lucide-react-native$/ }, () => ({ path: "icons", namespace: "smart-native" }));
      builder.onResolve({ filter: /(?:^|\/)application\/(AuthContext|LanguageContext)$/ }, args => ({ path: args.path.endsWith("AuthContext") ? "auth" : "language", namespace: "smart-native" }));
      builder.onResolve({ filter: /(?:^|\/)i18n$/ }, () => ({ path: "i18n", namespace: "smart-native" }));
      builder.onResolve({ filter: /(?:^|\/)([A-Za-z]+)$/ }, args => {
        const name = args.path.split("/").at(-1)!;
        if (options.stubs?.[name]) return { path: name, namespace: "smart-stub" };
        if (name === "components" && options.stubComponents) return { path: "components", namespace: "smart-stub" };
        if (name === "useFocusedResource" && options.resources) return { path: "resource", namespace: "smart-stub" };
        if (name === "SubscriptionContext") return { path: "subscription", namespace: "smart-stub" };
      });
      builder.onLoad({ filter: /.*/, namespace: "smart-stub" }, args => ({ loader: "js", contents: options.stubs?.[args.path] ?? (args.path === "subscription" ? 'export const useOptionalSubscription=()=>null;' : args.path === "resource" ? 'export const useFocusedResource=(key)=>globalThis.__smartUi.resources[key]??{data:null,loading:false,error:null,refresh:async()=>{},invalidate(){}};' : 'import React from "react";export const ThemedText="Text",Group="Group",Screen="Screen",Header="Header",NavigationRow="NavigationRow",IconButton="IconButton",Card="Card",TextField="TextField",SectionTitle="SectionTitle",ErrorBanner="ErrorBanner",EmptyState="EmptyState",LoadingState="LoadingState",StatusPill="StatusPill",SwitchRow="SwitchRow",NativeSwitch="NativeSwitch",ProgressBar="ProgressBar",DataRow="DataRow",Banner="Banner",SegmentedControl="SegmentedControl";export const AppButton=props=>React.createElement("button",props,props.label);') }));
      builder.onLoad({ filter: /.*/, namespace: "smart-native" }, args => ({ loader: "js", contents: options.stubs?.[args.path] ?? (args.path === "auth" ? 'export const useAuth=()=>globalThis.__smartUi.auth;' : args.path === "language" ? `import { translate } from "i18n";export const useLanguage=()=>({language:"en",t:translate,languages:[{code:"en",name:"English"}]});` : args.path === "i18n" ? `export const currentLocale=()=>"en",formattingLocale=()=>"en-GB";${translateSource}` : args.path === "icons" ? 'export const ChevronRight=()=>null,ChevronDown=()=>null,ChevronLeft=()=>null,Circle=()=>null,Check=()=>null,Clock3=()=>null,Pause=()=>null,Power=()=>null,TriangleAlert=()=>null,Plus=()=>null,PlugZap=()=>null,ArrowRight=()=>null,ArrowLeft=()=>null,ArrowUpRight=()=>null,ArrowDownRight=()=>null,ArrowDown=()=>null,ArrowUp=()=>null,Sun=()=>null,Moon=()=>null,CloudSun=()=>null,Table2=()=>null,Info=()=>null,X=()=>null,Trash2=()=>null,Save=()=>null,Copy=()=>null,Pencil=()=>null,Edit3=()=>null,LogOut=()=>null,User=()=>null,Plug=()=>null,Palette=()=>null,Globe=()=>null,MapPin=()=>null,CircleHelp=()=>null,KeyRound=()=>null,Settings2=()=>null,ShieldCheck=()=>null,Shield=()=>null,Clock=()=>null,LocateFixed=()=>null,RefreshCw=()=>null,Settings=()=>null,CirclePower=()=>null;' : modules[args.path]!) }));
    } }]
  });
  const module = { exports: {} as Record<string, React.ComponentType<any>> };
  new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(path.join(process.cwd(), "package.json")), module, module.exports);
  return module.exports;
}
