using System;
using System.Collections.Generic;
using DLS.Description;

namespace DLS.Simulation
{
    // A per-instance, allocation-free register program. Wires and custom-chip
    // boundaries become register aliases, rather than per-tick pin propagation.
    // Compile only single-driver DAGs: feedback and side effects must retain the
    // simulator's original scheduling semantics.
    public sealed class CompiledCircuit
    {
        enum Op { Nand, TriState, Extract, Merge }

        struct Instruction
        {
            public Op op;
            public int a, b, destination, shift;
            public uint mask;
        }

        readonly Instruction[] instructions;
        readonly uint[] registers;
        readonly SimPin[] inputs, outputs;
        readonly int[] outputRegisters;
        readonly (uint[] state, int register)[] constants;

        CompiledCircuit(Builder builder, SimChip chip)
        {
            instructions = builder.instructions.ToArray();
            registers = new uint[builder.nextRegister];
            registers[0] = 0xFFFF0000; // The simulator's disconnected-pin value.
            inputs = chip.InputPins;
            outputs = chip.OutputPins;
            outputRegisters = new int[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
                outputRegisters[i] = builder.Register(outputs[i]);
            constants = builder.constants.ToArray();
        }

        public int OperationCount => instructions.Length;

        public void Evaluate()
        {
            for (int i = 0; i < inputs.Length; i++) registers[i + 1] = inputs[i].State.a;
            // Read live values so editing a constant needs no recompilation.
            for (int i = 0; i < constants.Length; i++)
                registers[constants[i].register] = (ushort)constants[i].state[0];

            for (int i = 0; i < instructions.Length; i++)
            {
                Instruction instruction = instructions[i];
                uint a = registers[instruction.a];
                uint b = registers[instruction.b];
                uint result;
                switch (instruction.op)
                {
                    case Op.Nand: result = (1u ^ (a & b)) & 1u; break;
                    case Op.TriState: result = (b & 1u) != 0 ? a : 1u << 16; break;
                    case Op.Extract: result = (a >> instruction.shift) & instruction.mask; break;
                    default: result = a | ((b & instruction.mask) << instruction.shift); break;
                }
                registers[instruction.destination] = result;
            }

            for (int i = 0; i < outputs.Length; i++) outputs[i].State.a = registers[outputRegisters[i]];
        }

        public static bool TryCompile(SimChip chip, out CompiledCircuit circuit)
        {
            circuit = null;
            if (chip.ChipType != ChipType.Custom) return false;
            Builder builder = new();
            foreach (SimPin input in chip.InputPins) builder.pins.Add(input, builder.nextRegister++);
            if (!builder.Compile(chip, 0)) return false;
            circuit = new CompiledCircuit(builder, chip);
            return true;
        }

        sealed class Builder
        {
            public readonly List<Instruction> instructions = new();
            public readonly List<(uint[] state, int register)> constants = new();
            public readonly Dictionary<SimPin, int> pins = new();
            public int nextRegister = 1;

            public int Register(SimPin pin) => pins.TryGetValue(pin, out int register) ? register : 0;

            int Emit(Op op, int a, int b = 0, int shift = 0, uint mask = 0)
            {
                int destination = nextRegister++;
                instructions.Add(new Instruction { op = op, a = a, b = b,
                    destination = destination, shift = shift, mask = mask });
                return destination;
            }

            static uint Mask(int width)
            {
                uint bits = (1u << width) - 1;
                return bits | (bits << 16);
            }

            static bool ValidPins(SimPin[] pins)
            {
                foreach (SimPin pin in pins)
                    if (pin.State.size < 1 || pin.State.size > 16) return false;
                return true;
            }

            public bool Compile(SimChip chip, int depth)
            {
                // Bound recursive compilation even for unusually deep/malformed hierarchies.
                if (depth > 256 || !ValidPins(chip.InputPins) || !ValidPins(chip.OutputPins)) return false;
                if (chip.ChipType == ChipType.Custom) return CompileCustom(chip, depth);
                SimPin[] input = chip.InputPins;
                SimPin[] output = chip.OutputPins;
                switch (chip.ChipType)
                {
                    case ChipType.Nand:
                        if (input.Length != 2 || output.Length != 1 || input[0].State.size != 1 ||
                            input[1].State.size != 1 || output[0].State.size != 1) return false;
                        pins[output[0]] = Emit(Op.Nand, Register(input[0]), Register(input[1]));
                        return true;
                    case ChipType.TriStateBuffer:
                        if (input.Length != 2 || output.Length != 1 || input[0].State.size != 1 ||
                            input[1].State.size != 1 || output[0].State.size != 1) return false;
                        pins[output[0]] = Emit(Op.TriState, Register(input[0]), Register(input[1]));
                        return true;
                    case ChipType.Constant_8Bit:
                        if (input.Length != 0 || output.Length != 1 || chip.InternalState.Length < 1) return false;
                        pins[output[0]] = nextRegister;
                        constants.Add((chip.InternalState, nextRegister++));
                        return true;
                    case ChipType.Split_Pin:
                        if (input.Length != 1 || output.Length == 0) return false;
                        int splitWidth = output[0].State.size;
                        if (splitWidth * output.Length != input[0].State.size) return false;
                        for (int i = 0; i < output.Length; i++)
                        {
                            if (output[i].State.size != splitWidth) return false;
                            pins[output[i]] = Emit(Op.Extract, Register(input[0]), shift:
                                (output.Length - 1 - i) * splitWidth, mask: Mask(splitWidth));
                        }
                        return true;
                    case ChipType.Merge_Pin:
                        if (output.Length != 1 || input.Length == 0) return false;
                        int mergeWidth = input[0].State.size;
                        if (mergeWidth * input.Length != output[0].State.size) return false;
                        foreach (SimPin pin in input)
                            if (pin.State.size != mergeWidth) return false;
                        int result = Emit(Op.Extract, Register(input[input.Length - 1]), mask: Mask(mergeWidth));
                        for (int i = 1; i < input.Length; i++)
                        {
                            result = Emit(Op.Merge, result, Register(input[input.Length - 1 - i]),
                                i * mergeWidth, Mask(mergeWidth));
                        }
                        pins[output[0]] = result;
                        return true;
                    default: return false; // Clocks, memory, buses, displays, audio, etc.
                }
            }

            bool CompileCustom(SimChip chip, int depth)
            {
                // Sort at every custom-chip boundary, just as the ordinary simulator
                // does. A pin-level sort alone could incorrectly accept a feedback
                // loop between otherwise independent outputs of custom chips.
                Dictionary<SimChip, int> pending = new();
                Dictionary<SimPin, SimPin> drivers = new();
                HashSet<SimPin> targets = new(chip.OutputPins);
                foreach (SimChip child in chip.SubChips)
                {
                    if (pending.ContainsKey(child)) return false;
                    pending.Add(child, 0);
                    foreach (SimPin pin in child.InputPins) targets.Add(pin);
                }

                bool AddWires(SimPin[] sources, bool fromChild)
                {
                    foreach (SimPin source in sources)
                    foreach (SimPin target in source.ConnectedTargetPins)
                    {
                        if (!targets.Contains(target) || source.State.size != target.State.size ||
                            drivers.ContainsKey(target)) return false;
                        drivers.Add(target, source);
                        if (fromChild && target.parentChip != chip) pending[target.parentChip]++;
                    }
                    return true;
                }

                if (!AddWires(chip.InputPins, false)) return false;
                foreach (SimChip child in chip.SubChips)
                    if (!AddWires(child.OutputPins, true)) return false;
                foreach (SimPin target in targets)
                    if (target.numInputConnections != (drivers.ContainsKey(target) ? 1 : 0)) return false;

                void Bind(SimPin[] targetPins)
                {
                    foreach (SimPin target in targetPins)
                        pins[target] = drivers.TryGetValue(target, out SimPin source) ? Register(source) : 0;
                }

                Queue<SimChip> ready = new();
                foreach (SimChip child in chip.SubChips)
                    if (pending[child] == 0) ready.Enqueue(child);
                int processed = 0;
                while (ready.Count > 0)
                {
                    SimChip child = ready.Dequeue();
                    Bind(child.InputPins);
                    if (!Compile(child, depth + 1)) return false;
                    processed++;
                    foreach (SimPin output in child.OutputPins)
                    foreach (SimPin target in output.ConnectedTargetPins)
                        if (target.parentChip != chip && --pending[target.parentChip] == 0)
                            ready.Enqueue(target.parentChip);
                }
                if (processed != chip.SubChips.Length) return false;
                Bind(chip.OutputPins);
                return true;
            }
        }
    }
}
