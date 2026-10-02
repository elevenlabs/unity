# Security policy

## Reporting a vulnerability

Please do not report security vulnerabilities through public GitHub issues, discussions, or pull requests.

Report them privately through either channel:

- **GitHub:** [Report a vulnerability](https://github.com/elevenlabs/unity/security/advisories/new) on this repository (Security tab → _Report a vulnerability_).
- **ElevenLabs Vulnerability Disclosure Program:** [elevenlabs.io/security/report](https://elevenlabs.io/security/report) or [vulnerability_disclosure_program@elevenlabs.io](mailto:vulnerability_disclosure_program@elevenlabs.io).

Include the affected package version (or commit), the Unity version and build target, and steps to reproduce. We will acknowledge your report, investigate, and keep you updated on the fix.

ElevenLabs does not offer cash rewards for vulnerability reports at this time.

## Supported versions

The SDK is pre-1.0. Security fixes land on `main` and ship in the next release; older releases are not patched.

## Scope

This policy covers the code in this repository: the Unity package (`Runtime/`, `Editor/`, `Plugins/`, `Samples~/`) and its build tooling. Vulnerabilities in the ElevenLabs platform or API itself should go to the ElevenLabs Vulnerability Disclosure Program above.

Never commit or share an ElevenLabs API key in a Unity project. Players can extract anything shipped in a build, so use a public agent or have your own backend mint signed URLs or conversation tokens.
