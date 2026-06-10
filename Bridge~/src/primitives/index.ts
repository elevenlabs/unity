// Aggregates the primitive jslib library into a single object that
// build/bundle-jslib.mjs emits as the second argument to mergeInto.
//
// Each module under this directory uses named exports for its $EL_* / EL_*
// entries plus their __deps companions. Namespace imports collect those into
// per-module objects, which are then spread into the final library. Helpers
// that should not ship in the jslib stay non-exported inside their module.
//
// The library is exported as default so the bundle script can use Rolldown's
// IIFE output with `exports: "default"` to produce `var library = (function(){
// ... return {...} })();` — a clean single assignment that the appended
// `mergeInto(LibraryManager.library, library);` footer then consumes.

import * as bridgeName from "./bridge-name";
import * as log from "./log";

const library = {
  ...bridgeName,
  ...log,
};

export default library;
