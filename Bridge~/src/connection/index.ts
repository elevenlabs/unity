// Entry point for the ElevenLabsConnection.jslib bundle. Aggregates the
// connection-side modules into a single library object that
// build/bundle-jslib.ts emits as the second argument to mergeInto.

import * as factories from "./factories.js";

const library = {
  ...factories,
};

export default library;
