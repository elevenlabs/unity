// Entry point for the ElevenLabsConnection.jslib bundle. Aggregates the
// connection-side modules into a single library object that
// build/bundle-jslib.ts emits as the second argument to mergeInto.
//
// Task 2.1 ships the scaffold with no actual jslib entries — factory
// registrations land in task 2.2 (factories.ts) and 2.3 (audio-glue.ts). The
// empty library still produces a valid .jslib so `pnpm run verify:connection`
// can be wired up and exercised in CI before any real entries land.

const library = {};

export default library;
