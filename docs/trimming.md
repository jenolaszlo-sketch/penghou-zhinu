# Trimming and NativeAOT friendliness

Zhinu does not target NativeAOT yet, but this audit records what would block it
so future work (especially the P3 catalogue/compiled-workflow phase) does not
introduce unnecessary obstacles.

## Current state

- **JSON** uses `System.Text.Json` with a centralized `ZhinuJsonDefaults`
  (`Web` + `JsonStringEnumConverter` + `DefaultJsonTypeInfoResolver`). The
  reflection-based resolver is trimming-compatible at runtime but not NativeAOT
  trim-safe by itself; the shared, frozen `JsonSerializerOptions` means a future
  switch to a source-generated context is a single-point change.
- **Reflection** is confined to `AddZhinuWorkflowsFromAssembly` (assembly
  scanning). It is an ergonomic convenience, not the only registration path;
  explicit `AddZhinuWorkflow<T>` remains. Scanning requires reflection but the
  rest of the runtime does not.
- **No** `Activator.CreateInstance`, dynamic code generation, or runtime generic
  construction of user types outside the scanner.

## Constraints for P3

The compiler/catalogue phase must not introduce new reflection-heavy machinery as
a requirement:

- Activity identities resolve from a typed catalogue, not by reflecting over
  assemblies at runtime.
- Compiled workflow definitions are plain data (serializable, versioned) with
  deterministic validation; execution binds to activities through a typed
  contract, not reflection.
- Keep `AddZhinuWorkflowsFromAssembly` as an opt-in convenience; never make it
  the only way to register.

This keeps Zhinu friendly to trimmed apps, containers, and small services, with
NativeAOT as a future possibility rather than a promise.
