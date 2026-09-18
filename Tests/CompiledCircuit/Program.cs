using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using DLS.Description;
using DLS.Simulation;

static class Program
{
    static int assertions;
    static readonly uint[] states = { 0, 1, 0x10000, 0xFFFF0000, 0x10001 };
    static readonly MethodInfo reorder = typeof(Simulator).GetMethod("StepChipReorder", BindingFlags.NonPublic | BindingFlags.Static);
    static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }
    static SimChip Chip(ChipType type, int id, int[] inputs, int[] outputs, params SimChip[] children)
    {
        return new SimChip(new ChipDescription
        {
            Name = "Test" + Guid.NewGuid(), ChipType = type,
            CanBeCached = type == ChipType.Nand,
            InputPins = inputs.Select((width, i) => new PinDescription { ID = 1000000 + i, BitCount = new PinBitCount { BitCount = width } }).ToArray(),
            OutputPins = outputs.Select((width, i) => new PinDescription { ID = 2000000 + i, BitCount = new PinBitCount { BitCount = width } }).ToArray()
        }, id, type == ChipType.Constant_8Bit ? new uint[] { 42 } : null, children);
    }
    static SimChip Nand(int id) => Chip(ChipType.Nand, id, new[] { 1, 1 }, new[] { 1 });
    static PinAddress Address(SimChip scope, SimPin pin) => new(pin.parentChip == scope ? pin.ID : pin.parentChip.ID, pin.ID);
    static void Wire(SimChip scope, SimPin source, SimPin target) => scope.AddConnection(Address(scope, source), Address(scope, target));
    static void Unwire(SimChip scope, SimPin source, SimPin target) => scope.RemoveConnection(Address(scope, source), Address(scope, target));
    static void Order(SimChip chip)
    {
        Simulator.simulationFrame++;
        reorder.Invoke(null, new object[] { chip });
    }
    static void Compare(SimChip chip, int vectors, int seed = 11)
    {
        Assert(CompiledCircuit.TryCompile(chip, out var program), "Expected compilable circuit");
        Order(chip);
        var random = new Random(seed);
        for (int i = 0; i < vectors; i++)
        {
            foreach (var input in chip.InputPins)
                input.State.a = input.State.size == 1 ? states[random.Next(states.Length)] : (uint)random.NextInt64(0, 1L << 32);
            Simulator.useCaching = false;
            Simulator.simulationFrame++;
            Simulator.StepChip(chip);
            var expected = chip.OutputPins.Select(p => p.State.a).ToArray();
            foreach (var output in chip.OutputPins) output.State.a = 0xDEADBEEF;
            program.Evaluate();
            Assert(expected.SequenceEqual(chip.OutputPins.Select(p => p.State.a)), "Compiled/interpreted output differs");
        }
    }
    static SimChip Network(int count, int seed)
    {
        var random = new Random(seed);
        var gates = Enumerable.Range(1, count).Select(Nand).ToArray();
        var root = Chip(ChipType.Custom, -1, Enumerable.Repeat(1, 20).ToArray(), new[] { 1, 1, 1 }, gates);
        var sources = root.InputPins.ToList();
        foreach (var gate in gates)
        {
            foreach (var input in gate.InputPins) Wire(root, sources[random.Next(sources.Count)], input);
            sources.Add(gate.OutputPins[0]);
        }
        for (int i = 0; i < root.OutputPins.Length; i++) Wire(root, gates[count - 1 - i].OutputPins[0], root.OutputPins[i]);
        root.SubChips = gates.OrderBy(_ => random.Next()).ToArray();
        return root;
    }
    static void Main()
    {
        Simulator.canDynamicReorderThisFrame = false;
        // NAND truth table including disconnected values/flags; direct and unconnected outputs.
        var nand = Nand(1);
        var basic = Chip(ChipType.Custom, -1, new[] { 1, 1 }, new[] { 1, 1, 1 }, nand);
        Wire(basic, basic.InputPins[0], nand.InputPins[0]);
        Wire(basic, basic.InputPins[1], nand.InputPins[1]);
        Wire(basic, nand.OutputPins[0], basic.OutputPins[0]);
        Wire(basic, basic.InputPins[0], basic.OutputPins[1]);
        Assert(CompiledCircuit.TryCompile(basic, out var direct), "NAND compiles");
        foreach (uint a in states) foreach (uint b in states)
        {
            basic.InputPins[0].State.a = a; basic.InputPins[1].State.a = b;
            direct.Evaluate();
            Assert(basic.OutputPins[0].State.a == ((1u ^ (a & b)) & 1u), "NAND truth table");
            Assert(basic.OutputPins[1].State.a == a && basic.OutputPins[2].State.a == 0xFFFF0000, "Pass-through/disconnected flags");
        }
        Compare(basic, 100);
        // An exhaustive XOR truth table checks reconvergent multi-level paths.
        var x0 = Nand(1); var x1 = Nand(2); var x2 = Nand(3); var x3 = Nand(4);
        var xor = Chip(ChipType.Custom, -1, new[] { 1, 1 }, new[] { 1 }, x3, x2, x1, x0);
        Wire(xor, xor.InputPins[0], x0.InputPins[0]); Wire(xor, xor.InputPins[1], x0.InputPins[1]);
        Wire(xor, xor.InputPins[0], x1.InputPins[0]); Wire(xor, x0.OutputPins[0], x1.InputPins[1]);
        Wire(xor, xor.InputPins[1], x2.InputPins[0]); Wire(xor, x0.OutputPins[0], x2.InputPins[1]);
        Wire(xor, x1.OutputPins[0], x3.InputPins[0]); Wire(xor, x2.OutputPins[0], x3.InputPins[1]);
        Wire(xor, x3.OutputPins[0], xor.OutputPins[0]);
        Assert(CompiledCircuit.TryCompile(xor, out var xorProgram), "XOR compiles");
        for (uint a = 0; a < 2; a++) for (uint b = 0; b < 2; b++)
        {
            xor.InputPins[0].State.a = a; xor.InputPins[1].State.a = b;
            xorProgram.Evaluate();
            Assert(xor.OutputPins[0].State.a == (a ^ b), "XOR truth table");
        }
        Compare(xor, 100);

        for (int seed = 0; seed < 20; seed++) Compare(Network(100, seed), 100, seed);

        // Nested custom chips reuse local IDs. Each nested circuit has an independent register binding.
        var left = Network(40, 5); var right = Network(40, 6);
        // Wrap the original graph to preserve its pin identity.
        left = Wrap(left, 1); right = Wrap(right, 2);
        var nested = Chip(ChipType.Custom, -1, new[] { 1 }, new[] { 1 }, right, left);
        foreach (var pin in left.InputPins) Wire(nested, nested.InputPins[0], pin);
        foreach (var pin in right.InputPins) Wire(nested, left.OutputPins[0], pin);
        Wire(nested, right.OutputPins[0], nested.OutputPins[0]);
        Compare(nested, 100);

        var tri = Chip(ChipType.TriStateBuffer, 1, new[] { 1, 1 }, new[] { 1 });
        Compare(Wrap(tri, -1), 200);
        foreach (int width in new[] { 4, 8, 16 })
        {
            var split = Chip(ChipType.Split_Pin, 1, new[] { width }, Enumerable.Repeat(1, width).ToArray());
            var merge = Chip(ChipType.Merge_Pin, 2, Enumerable.Repeat(1, width).ToArray(), new[] { width });
            var bus = Chip(ChipType.Custom, -1, new[] { width }, new[] { width }, merge, split);
            Wire(bus, bus.InputPins[0], split.InputPins[0]);
            for (int i = 0; i < width; i++) Wire(bus, split.OutputPins[i], merge.InputPins[i]);
            Wire(bus, merge.OutputPins[0], bus.OutputPins[0]);
            Compare(bus, 200);
        }
        var constant = Chip(ChipType.Constant_8Bit, 1, Array.Empty<int>(), new[] { 8 });
        var constantRoot = Wrap(constant, -1);
        Assert(constantRoot.TryProcessingCompiled() && constantRoot.OutputPins[0].State.a == 42, "Constant");
        constant.UpdateInternalState(new uint[] { 123 });
        Assert(constantRoot.TryProcessingCompiled() && constantRoot.OutputPins[0].State.a == 123, "Live constant edit");

        // Self-feedback, two-chip loops and conflicting output drivers must be rejected.
        var feedbackGate = Nand(1); var feedback = Wrap(feedbackGate, -1);
        Unwire(feedback, feedback.InputPins[0], feedbackGate.InputPins[0]);
        Wire(feedback, feedbackGate.OutputPins[0], feedbackGate.InputPins[0]);
        Assert(!CompiledCircuit.TryCompile(feedback, out _), "Reject feedback");
        var g1 = Nand(1); var g2 = Nand(2);
        var loop = Chip(ChipType.Custom, -1, Array.Empty<int>(), new[] { 1 }, g1, g2);
        Wire(loop, g1.OutputPins[0], g2.InputPins[0]); Wire(loop, g2.OutputPins[0], g1.InputPins[0]);
        Assert(!CompiledCircuit.TryCompile(loop, out _), "Reject dead two-chip loop");
        Wire(basic, basic.InputPins[1], basic.OutputPins[1]);
        Assert(!CompiledCircuit.TryCompile(basic, out _), "Reject output conflicts");
        Unwire(basic, basic.InputPins[1], basic.OutputPins[1]);
        Wire(basic, basic.InputPins[1], nand.InputPins[0]);
        Assert(!CompiledCircuit.TryCompile(basic, out _), "Reject input conflicts");
        Unwire(basic, basic.InputPins[1], nand.InputPins[0]);
        foreach (var type in new[] { ChipType.Clock, ChipType.Pulse, ChipType.dev_Ram_8Bit, ChipType.Buzzer, ChipType.Bus })
            Assert(!CompiledCircuit.TryCompile(Wrap(Chip(type, 1, Array.Empty<int>(), new[] { 1 }), -1), out _), "Reject " + type);
        Assert(!CompiledCircuit.TryCompile(Chip(ChipType.Custom, -1, new[] { 32 }, new[] { 32 }), out _), "Wide pin fallback");

        // Edits invalidate already successful and already rejected attempts, including ancestors.
        Assert(basic.TryProcessingCompiled(), "Initial compilation");
        Unwire(basic, basic.InputPins[0], nand.InputPins[0]);
        Wire(basic, nand.OutputPins[0], nand.InputPins[0]);
        Assert(!basic.TryProcessingCompiled(), "Successful program invalidated");
        Unwire(basic, nand.OutputPins[0], nand.InputPins[0]);
        Wire(basic, basic.InputPins[0], nand.InputPins[0]);
        Assert(basic.TryProcessingCompiled(), "Failed compilation retried after edit");
        var ancestor = Wrap(basic, -2);
        Assert(ancestor.TryProcessingCompiled(), "Ancestor compiles");
        Wire(basic, basic.InputPins[1], nand.InputPins[0]);
        Assert(!ancestor.TryProcessingCompiled(), "Nested edit invalidates ancestor");
        Unwire(basic, basic.InputPins[1], nand.InputPins[0]);
        Assert(ancestor.TryProcessingCompiled(), "Nested repair recompiles ancestor");

        // Custom-chip cycles remain cycles even when individual pin paths are independent.
        var pass1 = Chip(ChipType.Custom, 1, new[] { 1, 1 }, new[] { 1, 1 });
        var pass2 = Chip(ChipType.Custom, 2, new[] { 1, 1 }, new[] { 1, 1 });
        foreach (var pass in new[] { pass1, pass2 })
            for (int i = 0; i < 2; i++) Wire(pass, pass.InputPins[i], pass.OutputPins[i]);
        var hierarchyLoop = Chip(ChipType.Custom, -1, new[] { 1 }, new[] { 1 }, pass1, pass2);
        Wire(hierarchyLoop, hierarchyLoop.InputPins[0], pass1.InputPins[0]);
        Wire(hierarchyLoop, pass1.OutputPins[0], pass2.InputPins[0]);
        Wire(hierarchyLoop, pass2.OutputPins[1], pass1.InputPins[1]);
        Wire(hierarchyLoop, pass2.OutputPins[0], hierarchyLoop.OutputPins[0]);
        Assert(!CompiledCircuit.TryCompile(hierarchyLoop, out _), "Reject hierarchy-level scheduling cycle");

        var editing = Chip(ChipType.Custom, -1, Array.Empty<int>(), Array.Empty<int>());
        Assert(editing.TryProcessingCompiled(), "Empty circuit compiles");
        editing.AddSubChip(Chip(ChipType.Clock, 1, Array.Empty<int>(), new[] { 1 }));
        Assert(!editing.TryProcessingCompiled(), "Adding stateful subchip invalidates program");
        editing.RemoveSubChip(1);
        Assert(editing.TryProcessingCompiled(), "Removing stateful subchip recompiles");
        editing.AddPin(new SimPin(3000000, true, editing, new PinBitCount { BitCount = 32 }), true);
        Assert(!editing.TryProcessingCompiled(), "Adding wide pin invalidates program");
        editing.RemovePin(3000000);
        Assert(editing.TryProcessingCompiled(), "Removing wide pin recompiles");

        var integration = Network(300, 13);
        var parent = Wrap(integration, -2);
        Order(parent);
        Simulator.useCaching = true;
        Simulator.useCompiledCircuits = true;
        Simulator.simulationFrame++; Simulator.StepChip(parent);
        Assert(integration.LUT == null, "Compiled evaluation avoids LUT generation");
        integration.CalculateLUT();
        Assert(integration.LUT.Length == 0 && integration.SubChips.All(c => c.LUT == null), "LUT request skips compiled descendants");
        Simulator.useCaching = false;
        parent.InputPins[0].State.a = 1;
        Simulator.simulationFrame++; Simulator.StepChip(parent);
        Assert(integration.InputPins[0].State.a == 1 && integration.SubChips.Any(c => c.InputPins.Any(p => p.lastUpdatedFrameIndex == Simulator.simulationFrame)), "Inspection updates internal pins");
        // Exercise the actual StepChip dispatch with both accelerated and reference paths.
        var xorParent = Wrap(xor, -2);
        Order(xorParent);
        for (int i = 0; i < 100; i++)
        {
            xorParent.InputPins[0].State.a = states[i % states.Length];
            xorParent.InputPins[1].State.a = states[(i / states.Length) % states.Length];
            Simulator.useCaching = false;
            Simulator.simulationFrame++; Simulator.StepChip(xorParent);
            uint expected = xorParent.OutputPins[0].State.a;
            xorParent.OutputPins[0].State.a = 99;
            Simulator.useCaching = true;
            Simulator.simulationFrame++; Simulator.StepChip(xorParent);
            Assert(xorParent.OutputPins[0].State.a == expected, "StepChip accelerated dispatch");
        }
        Console.WriteLine($"PASS: {assertions} assertions against production simulation code.");
        Benchmark();
    }
    static SimChip Wrap(SimChip inner, int id)
    {
        var wrapper = Chip(ChipType.Custom, id, inner.InputPins.Select(p => p.State.size).ToArray(), inner.OutputPins.Select(p => p.State.size).ToArray(), inner);
        for (int i = 0; i < inner.InputPins.Length; i++) Wire(wrapper, wrapper.InputPins[i], inner.InputPins[i]);
        for (int i = 0; i < inner.OutputPins.Length; i++) Wire(wrapper, inner.OutputPins[i], wrapper.OutputPins[i]);
        return wrapper;
    }
    static void Benchmark()
    {
        var network = Network(5000, 123);
        var parent = Wrap(network, -2);
        Order(parent);
        var timer = Stopwatch.StartNew();
        Assert(network.TryProcessingCompiled(), "Benchmark compiles");
        double compilation = timer.Elapsed.TotalMilliseconds;
        double Measure(bool accelerated)
        {
            Simulator.useCaching = accelerated;
            for (int i = 0; i < 500; i++) { Simulator.simulationFrame++; Simulator.StepChip(parent); }
            timer.Restart();
            for (int i = 0; i < 10000; i++)
            {
                parent.InputPins[0].State.a = (uint)(i & 1);
                Simulator.simulationFrame++;
                Simulator.StepChip(parent);
            }
            return timer.Elapsed.TotalMilliseconds;
        }
        double interpreted = Measure(false), compiled = Measure(true);
        Console.WriteLine($"5,000 NANDs, 20 input bits, 10,000 ticks: interpreted {interpreted:F1} ms; compiled {compiled:F1} ms; speedup {interpreted / compiled:F2}x; compilation {compilation:F1} ms.");
    }
}
