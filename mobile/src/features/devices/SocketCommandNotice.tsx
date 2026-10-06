import { type StyleProp, type TextStyle } from "react-native";
import { useLanguage } from "../../application/LanguageContext";
import { AppButton, ThemedText as Text } from "../../core/components";
import { commandUnresolved, socketCommandMessage, type SocketCommandState } from "../../core/api/SocketCommandCoordinator";

export function SocketCommandNotice({ command, disabled, onCheck, onRelease, textStyle }: {
  command: SocketCommandState | null; disabled: boolean; onCheck: () => void; onRelease: () => void; textStyle?: StyleProp<TextStyle>
}) {
  const { t } = useLanguage();
  if (!command) return null;
  return <>
    <Text style={textStyle}>{t(socketCommandMessage(command))}</Text>
    {commandUnresolved(command) ? <AppButton label={t("Check command result")} variant="secondary" onPress={onCheck} disabled={disabled} /> : null}
    {command.status === "uncertain" ? <>
      <Text style={textStyle}>{t("The earlier operation may still finish; allowing another command does not cancel it. Its result remains unknown. The server must obtain an online device observation first.")}</Text>
      <AppButton label={t("Allow another command")} variant="secondary" onPress={onRelease} disabled={disabled} />
    </> : null}
  </>;
}
