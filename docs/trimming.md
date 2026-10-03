# Trimming and NativeAOT friendliness

Zhinu does not claim NativeAOT or trimming safety. This page records current
constraints for hosts and future improvements, including the implemented
declarative and authorization paths.

## Current state

- **JSON** uses reflection-based `System.Text.Json` contract resolution.
  `ZhinuJsonDefaults` covers ordinary runtime payloads. Declarative activity
  conversion uses default JSON options with runtime CLR types; authorization
  codecs use separate fixed, versioned options. A source-generated replacement
  therefore requires reviewing each path, not changing one options instance.
- **Reflection** includes assembly scanning and validation of Type-based step
  registrations through `MakeGenericType`; the scanner also uses
  `MakeGenericMethod`. Explicit generic registrations avoid assembly discovery
  but do not prove the entire application is trim-safe.
- Hosts must retain metadata for their workflow payloads, registered activity
  types and DI construction. No trimmed or NativeAOT qualification is recorded.

## Constraints for future work

The compiler/catalogue boundary should continue to avoid requiring discovery of
arbitrary executable types:

- Activity identities resolve from a typed catalogue, not by reflecting over
  assemblies at runtime.
- Compiled workflow definitions are plain data (serializable, versioned) with
  deterministic validation. Contracts persist stable string identities rather
  than CLR `Type` objects; runtime CLR types remain inside the typed activity
  catalogue and are checked when an artifact is compiled or registered.
- Keep `AddZhinuWorkflowsFromAssembly` as an opt-in convenience; never make it
  the only way to register.

NativeAOT remains future work requiring explicit serialization/registration
profiles and real publish-and-run checks. Ordinary container deployment does
not itself establish trimming or NativeAOT support.
