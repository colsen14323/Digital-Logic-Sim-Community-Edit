# Compiled combinational circuits

Custom chips now automatically use a flat bitwise register program when their
entire contents are supported and acyclic. This is enabled by default, before
lookup-table evaluation. No chip file migration or manual cache creation is needed.
The chip being edited still uses ordinary propagation so its visible pins update;
its custom subchips can use compiled evaluation. The existing chip inspection
mode disables both acceleration paths so internal signals remain visible.

## Supported circuits

- NAND gates and custom chips composed of supported gates, at any input count.
- One-bit tri-state buffers, including the existing disconnected signal behavior.
- Constants (values are read live, including after edits).
- Split and merge operations on pins from 1 to 16 bits wide.
- Direct input/output wiring, fan-out, and unconnected pins.

Each destination must have at most one driver. All custom-chip levels must be
acyclic, including unused internal logic. Clocks, memory, displays, audio, buses,
other unsupported primitives, and pins wider than 16 bits retain the existing
simulation/cache path. Compilation is bounded to 256 nested levels. Eligible
custom subchips inside a larger unsupported or sequential design can still be
accelerated individually.

## Implementation

`CompiledCircuit` topologically sorts each custom-chip scope, flattens supported
nested chips, and aliases wires to integer registers. Each tick copies boundary
inputs, executes a compact array of bitwise instructions, and writes boundary
outputs. It performs no dictionary lookup, recursive chip traversal, wire
propagation, or allocation during evaluation. The program preserves the existing
packed value/disconnected-flag representation; it does not assume all inputs are
binary or convert disconnected signals to arbitrary Boolean values.

Program construction and storage scale with the circuit graph, rather than the
exponential number of possible input combinations. It does not enumerate truth
tables, generate dynamic IL, or require a JIT code-generation API. Programs and
register storage are per simulation instance; nothing is serialized in chip files.

Successful and failed compilation attempts are memoized. Structural mutation
methods on `SimChip` increment a simulation-thread revision, invalidating programs
in ancestors as well as the edited chip. This conservatively invalidates unrelated
programs too; they are rebuilt lazily on their next use. Constant edits do not
require recompilation. New topology code should use these mutation methods rather
than assigning pin/child/connection arrays directly.

The existing reorder pass still runs after edits. The compiled evaluator bypasses
internal pin state updates like the existing LUT path, and obeys `useCaching`
when inspection needs those states. `Simulator.useCompiledCircuits` can be set
false before running a fresh simulation for profiling or regression comparison.
Eligible programs also bypass recursive LUT generation when `CalculateLUT` is
requested, avoiding redundant exponential work.

## Regression tests and benchmark

From the repository root with the .NET 8 SDK installed:

```sh
dotnet run --project Tests/CompiledCircuit -c Release
```

The standalone harness compiles the actual production compiler, chip, pin, packed
state, and simulator source files. Small adapters replace Unity math, UI, project,
audio, and keyboard dependencies only. It tests NAND and XOR truth tables,
randomized DAGs, reconvergent paths, nested chips with reused local IDs, packed
tri-state flags, constants, split/merge, conflicts, feedback, unsupported gates,
structural invalidation, inspection behavior, LUT avoidance, and the real
`StepChip` accelerated dispatch.

Validation on Linux/.NET 8 Release passed **3,310 assertions**. One synthetic
benchmark with **5,000 NAND gates, 20 input bits and 10,000 changing-input ticks**
measured:

| Path | Time |
| --- | ---: |
| Ordinary propagation, LUTs disabled | 2,570.2 ms |
| Compiled register evaluation | 96.6 ms |
| One-time compilation | 11.6 ms |

This is a **26.59x** evaluator speedup for this test circuit, not a Unity FPS claim
or a guarantee for other circuits. The harness uses 20 input bits so the baseline
is outside the existing automatic-LUT range. Existing small LUTs may be faster
than a register program in steady state; this implementation prioritizes bounded
preparation and memory use. Compilation time is reported separately from ticks.

The full Unity editor/player build was not run in this environment. The repository
specifies Unity **6000.3.7f1**. Before merging, run the project in that editor and
check chip inspection, wire editing, save/reload, and a NAND-based combinational
chip inside a clocked design. The headless build currently reports the pre-existing
duplicate `using DLS.Description` warning in `SimPin.cs`.
