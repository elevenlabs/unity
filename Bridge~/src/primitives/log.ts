// Internal logging helper. Never called from C# ($ prefix keeps it out of
// DllImport resolution). Functions that use it declare `$EL_Log` as a dep
// and call it as `_EL_Log(level, scope, message)`.
//
// level: "info" | "warn" | "error"
export function $EL_Log(
  level: "info" | "warn" | "error" | string,
  scope: string,
  msg: string,
): void {
  const line = "[ElevenLabs Bridge] " + scope + ": " + msg;
  if (level === "error") console.error(line);
  else if (level === "warn") console.warn(line);
  else console.log(line);
}
