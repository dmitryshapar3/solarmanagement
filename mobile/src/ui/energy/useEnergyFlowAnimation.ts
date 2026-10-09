import { useCallback, useEffect, useRef, useState } from "react";
import { AccessibilityInfo, Animated, AppState, Easing } from "react-native";
import { useFocusEffect } from "@react-navigation/native";

export function useEnergyFlowAnimation(enabled: boolean) {
  const progress = useRef(new Animated.Value(0)).current;
  const [foreground, setForeground] = useState(AppState.currentState === "active");
  const [focused, setFocused] = useState(false);
  const [reducedMotion, setReducedMotion] = useState(true);

  useFocusEffect(useCallback(() => {
    setFocused(true);
    return () => setFocused(false);
  }, []));

  useEffect(() => {
    let mounted = true;
    let changed = false;
    const foregroundListener = AppState.addEventListener("change", state => setForeground(state === "active"));
    const motionListener = AccessibilityInfo.addEventListener("reduceMotionChanged", value => {
      changed = true;
      setReducedMotion(value);
    });
    void AccessibilityInfo.isReduceMotionEnabled().then(value => {
      if (mounted && !changed) setReducedMotion(value);
    }).catch(() => {});
    return () => { mounted = false; foregroundListener.remove(); motionListener.remove(); };
  }, []);

  const moving = enabled && foreground && focused && !reducedMotion;
  useEffect(() => {
    if (!moving) return;
    progress.setValue(0);
    const animation = Animated.loop(Animated.timing(progress, {
      toValue: 1, duration: 1600, easing: Easing.linear,
      // SVG coordinates are animated through react-native-svg's native props.
      useNativeDriver: false, isInteraction: false
    }));
    animation.start();
    return () => { animation.stop(); progress.stopAnimation(); };
  }, [moving, progress]);

  return { progress, moving };
}
