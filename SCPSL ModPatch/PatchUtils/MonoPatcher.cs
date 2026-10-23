using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Reflection;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>Plans symbolic managed edits against the original image and validates a staged rewrite.</summary>
    internal sealed class MonoPatcher
    {
        static readonly Dictionary<string, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(op => op.Name, StringComparer.OrdinalIgnoreCase);
        readonly byte[] _image;
        readonly VersionRangeInfo _target;
        readonly string? _assemblyDirectory;
        readonly List<PatchOutcome> _outcomes = new();
        public IReadOnlyList<PatchOutcome> PatchOutcomes => _outcomes;

        public MonoPatcher(byte[] image, VersionRangeInfo target, string? assemblyDirectory = null)
        {
            _image = (byte[])image.Clone();
            _target = target;
            _assemblyDirectory = assemblyDirectory == null ? null : Path.GetFullPath(assemblyDirectory);
        }

        public byte[] Rewrite()
        {
            _outcomes.Clear();
            PatchTargetResolver.Validate(new PatchInfo { versionRanges = new[] { _target } });
            if (PatchTargetResolver.GetBackend(_target) != "mono")
                throw Error("Managed rewriting requires a Mono patch profile.");
            if (_target.assemblySha256 != null && !string.Equals(_target.assemblySha256,
                PatchTargetResolver.ComputeSha256(_image), StringComparison.OrdinalIgnoreCase))
                throw Error("The selected Mono SHA-256 target does not match the original assembly.");
            using TargetAssemblyResolver resolver = new(_assemblyDirectory);
            using MemoryStream source = new(_image, false);
            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(source,
                new ReaderParameters { InMemory = true, ReadSymbols = false, AssemblyResolver = resolver });
            ModuleDefinition module = assembly.MainModule;
            if (assembly.Modules.Count != 1 || (module.Attributes & ModuleAttributes.ILOnly) == 0)
                throw Error("Only single-module, IL-only Mono assemblies are supported.");
            if (assembly.Name.HasPublicKey || (module.Attributes & ModuleAttributes.StrongNameSigned) != 0)
                throw Error("Signed Mono assemblies require an explicit signing workflow.");
            if (_target.methods.patchMethods.Length == 0)
                return (byte[])_image.Clone();

            MethodDefinition[] methods = AllTypes(module.Types).SelectMany(type => type.Methods).ToArray();
            List<EditPlan> plans = new();
            List<PatchOutcome> outcomes = new();
            foreach (PatchMethodInfo patch in _target.methods.patchMethods)
            {
                if (string.IsNullOrWhiteSpace(patch.name)) throw Error("A managed patch requires a full method signature.");
                if (patch.ilPattern == null || patch.ilPattern.Length == 0 || patch.ilEdit == null)
                    throw Error($"Patch ({patch.name}) requires ilPattern and ilEdit.");
                if (!patch.ilPattern.Any(instruction => instruction != null && instruction.op != "*"))
                    throw Error($"Patch ({patch.name}) requires at least one concrete opcode in its pattern.");
                foreach (MonoInstructionInfo instruction in patch.ilPattern) ValidatePattern(instruction);
                if (patch.patchPattern != null || patch.patchOffset != null || patch.patchSize != null ||
                    patch.patternOffset != 0 || !string.IsNullOrEmpty(patch.patchData))
                    throw Error($"Patch ({patch.name}) mixes managed and native edits.");
                if (patch.patchifnotexists != null && methods.Any(method => method.FullName == patch.patchifnotexists))
                {
                    outcomes.Add(new PatchOutcome(patch.name, "skipped-fallback", "The preferred method exists."));
                    continue;
                }
                if (patch.patchifexists != null && !methods.Any(method => method.FullName == patch.patchifexists))
                {
                    outcomes.Add(new PatchOutcome(patch.name, "skipped-condition", "The required method does not exist."));
                    continue;
                }
                MethodDefinition[] candidates = methods.Where(method => method.FullName == patch.name).ToArray();
                if (candidates.Length == 0 && patch.nowarning)
                {
                    outcomes.Add(new PatchOutcome(patch.name, "skipped-missing", "The optional method does not exist."));
                    continue;
                }
                if (candidates.Length != 1 || !candidates[0].HasBody)
                    throw Error($"Patch ({patch.name}) requires exactly one method with a managed body.");
                MethodDefinition method = candidates[0];
                List<(int Index, Dictionary<string, Instruction> Labels)> matches = new();
                for (int i = 0; i <= method.Body.Instructions.Count - patch.ilPattern.Length; i++)
                    if (Matches(method, i, patch.ilPattern, out Dictionary<string, Instruction> labels))
                        matches.Add((i, labels));
                if (matches.Count != 1)
                    throw Error($"Patch ({patch.name}) matched {matches.Count} instruction sequences; exactly one is required.");
                EditPlan plan = Plan(module, method, patch, matches[0].Index, matches[0].Labels);
                foreach (EditPlan other in plans.Where(other => other.Method == method))
                    if (plan.WholeBody || other.WholeBody || plan.Start < other.Start + other.Count && other.Start < plan.Start + plan.Count ||
                        plan.Start == other.Start)
                        throw Error($"Patches ({patch.name}) overlap in the same method.");
                plans.Add(plan);
                outcomes.Add(new PatchOutcome(patch.name, "applied", "The managed IL edit was applied."));
            }
            if (plans.Count == 0)
                throw Error("No managed IL edits were selected.");

            // All matching and operand resolution use the original assembly before any instruction changes.
            foreach (EditPlan plan in plans.OrderByDescending(plan => plan.Start))
                Apply(plan);
            string[] changed = plans.Select(plan => plan.Method.FullName).Distinct().ToArray();
            foreach (MethodDefinition method in plans.Select(plan => plan.Method).Distinct())
            {
                ExpandBranches(method);
                Validate(method);
            }
            using MemoryStream output = new();
            assembly.Write(output, new WriterParameters { WriteSymbols = false });
            byte[] result = output.ToArray();
            using MemoryStream reread = new(result, false);
            using AssemblyDefinition verified = AssemblyDefinition.ReadAssembly(reread,
                new ReaderParameters { InMemory = true, ReadSymbols = false, AssemblyResolver = resolver });
            if (verified.Name.FullName != assembly.Name.FullName || verified.MainModule.Mvid != module.Mvid)
                throw Error("The rewritten assembly identity changed.");
            MethodDefinition[] verifiedMethods = AllTypes(verified.MainModule.Types).SelectMany(type => type.Methods).ToArray();
            foreach (string name in changed)
                Validate(verifiedMethods.Single(method => method.FullName == name));
            _outcomes.AddRange(outcomes);
            return result;
        }

        static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (TypeDefinition type in types)
            {
                yield return type;
                foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
            }
        }

        static bool Matches(MethodDefinition method, int start, MonoInstructionInfo[] pattern,
            out Dictionary<string, Instruction> labels)
        {
            labels = new(StringComparer.Ordinal);
            for (int i = 0; i < pattern.Length; i++)
                if (pattern[i].label != null && !Bind(labels, pattern[i].label!, method.Body.Instructions[start + i]))
                    throw Error("Pattern labels must identify one instruction each.");
            for (int i = 0; i < pattern.Length; i++)
            {
                MonoInstructionInfo expected = pattern[i];
                Instruction actual = method.Body.Instructions[start + i];
                if (expected.op == "*")
                {
                    if (expected.operand != null && !MatchOperand(expected.operand, actual.Operand, labels)) return false;
                    continue;
                }
                OpCode expectedOpcode = GetOpcode(expected.op);
                var canonical = Canonical(actual.OpCode, actual.Operand);
                var expectedCanonical = Canonical(expectedOpcode, null);
                if (canonical.Op != expectedCanonical.Op) return false;
                JToken? expectedOperand = expected.operand;
                if (expectedOperand == null && expectedCanonical.Operand != null)
                    expectedOperand = JToken.FromObject(expectedCanonical.Operand);
                if (expectedOperand != null && !MatchOperand(expectedOperand, canonical.Operand, labels)) return false;
            }
            return true;
        }

        static (string Op, object? Operand) Canonical(OpCode op, object? operand)
        {
            string name = op.Name;
            if (name.StartsWith("ldc.i4.", StringComparison.Ordinal) && name != "ldc.i4.s")
                return ("ldc.i4", name == "ldc.i4.m1" ? -1 : int.Parse(name.Substring(7), CultureInfo.InvariantCulture));
            foreach (string prefix in new[] { "ldarg.", "ldloc.", "stloc." })
                if (name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name.Substring(prefix.Length), out int index))
                    return (prefix.TrimEnd('.'), index);
            if (name.EndsWith(".s", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);
            if (operand is ParameterDefinition parameter)
                operand = parameter.Index + (parameter.Method.HasThis ? 1 : 0);
            else if (operand is VariableDefinition variable) operand = variable.Index;
            return (name, operand);
        }

        static bool MatchOperand(JToken expected, object? actual, Dictionary<string, Instruction> labels)
        {
            if (expected.Type == JTokenType.String)
            {
                string text = expected.Value<string>()!;
                if (text == "*") return true;
                if (actual is Instruction target && text.StartsWith("$", StringComparison.Ordinal)) return Bind(labels, text, target);
                if (actual is MemberReference member) return member.FullName == text;
                return actual is string literal && literal == text;
            }
            if (expected is JArray array && actual is Instruction[] targets)
                return array.Count == targets.Length && array.Select((value, i) => MatchOperand(value, targets[i], labels)).All(match => match);
            if (expected.Type == JTokenType.Null) return actual == null;
            if (expected.Type is JTokenType.Integer or JTokenType.Float)
            {
                if (actual is float single) return single.Equals(expected.Value<float>());
                if (actual is double real) return real.Equals(expected.Value<double>());
            }
            if (actual is sbyte or byte or short or ushort or int or uint or long or ulong or float or double)
                return expected.Type is JTokenType.Integer or JTokenType.Float &&
                    Convert.ToDecimal(actual, CultureInfo.InvariantCulture) == expected.Value<decimal>();
            return false;
        }

        static void ValidatePattern(MonoInstructionInfo? instruction)
        {
            if (instruction == null) throw Error("Pattern instructions cannot be null.");
            if (instruction.label != null) LabelName(instruction.label);
            if (instruction.op == "*")
            {
                if (instruction.operand != null && instruction.operand.Type is JTokenType.Object or JTokenType.Property)
                    throw Error("A wildcard instruction cannot use an object operand.");
                return;
            }
            OpCode op = GetOpcode(instruction.op);
            JToken? operand = instruction.operand;
            if (operand == null || operand.Type == JTokenType.Null) return;
            if (op.OperandType == OperandType.InlineNone)
                throw Error($"Pattern instruction ({op.Name}) does not accept an operand.");
            if (operand.Type == JTokenType.String && operand.Value<string>() == "*") return;
            bool valid = op.OperandType switch
            {
                OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok or
                    OperandType.InlineString => operand.Type == JTokenType.String,
                OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget => operand.Type == JTokenType.String &&
                    operand.Value<string>()!.StartsWith("$", StringComparison.Ordinal) && LabelName(operand.Value<string>()!).Length != 0,
                OperandType.InlineSwitch => operand is JArray labels && labels.All(label => label.Type == JTokenType.String &&
                    (label.Value<string>() == "*" || label.Value<string>()!.StartsWith("$", StringComparison.Ordinal) && LabelName(label.Value<string>()!).Length != 0)),
                OperandType.InlineI or OperandType.ShortInlineI or OperandType.InlineI8 or OperandType.InlineVar or OperandType.ShortInlineVar or
                    OperandType.InlineArg or OperandType.ShortInlineArg =>
                    operand.Type == JTokenType.Integer,
                OperandType.ShortInlineR or OperandType.InlineR => operand.Type is JTokenType.Integer or JTokenType.Float,
                _ => false
            };
            if (!valid) throw Error($"Pattern instruction ({op.Name}) has an invalid operand.");
        }

        static bool Bind(Dictionary<string, Instruction> labels, string label, Instruction target)
        {
            label = LabelName(label);
            if (labels.TryGetValue(label, out Instruction? existing)) return existing == target;
            labels.Add(label, target);
            return true;
        }

        static EditPlan Plan(ModuleDefinition module, MethodDefinition method, PatchMethodInfo patch, int start,
            Dictionary<string, Instruction> labels)
        {
            MonoEditInfo edit = patch.ilEdit!;
            string operation = edit.operation;
            bool wholeBody = operation is "replaceBody" or "return";
            if (operation is not ("nop" or "replace" or "insertBefore" or "insertAfter" or "replaceBody" or "return"))
                throw Error($"Unknown managed edit operation ({operation}).");
            int count = edit.count ?? patch.ilPattern!.Length;
            if (count <= 0 || count > patch.ilPattern!.Length)
                throw Error("The edit count must be positive and fit inside its matched pattern.");
            if (wholeBody && edit.count.HasValue)
                throw Error("Whole-body edits do not accept count.");
            MonoInstructionInfo[] definitions = edit.instructions ?? Array.Empty<MonoInstructionInfo>();
            if (operation == "return")
            {
                if (method.ReturnType.MetadataType != MetadataType.Void)
                    throw Error("The return operation requires a void method; use replaceBody for explicit return values.");
                if (method.IsConstructor && !method.IsStatic)
                    throw Error("An instance constructor cannot be replaced with an uninitialized return.");
                if (definitions.Length != 0)
                    throw Error("The return operation does not accept instructions; use replaceBody for an explicit body.");
                definitions = new[] { new MonoInstructionInfo { op = "ret" } };
            }
            if (operation == "nop" && definitions.Length != 0)
                throw Error("A nop edit cannot supply replacement instructions.");
            if (operation != "nop" && definitions.Length == 0)
                throw Error("Managed instruction edits require nonempty instructions.");
            Instruction[] instructions = definitions.Select(definition => Instruction.Create(OpCodes.Nop)).ToArray();
            for (int i = 0; i < definitions.Length; i++)
                if (definitions[i].label != null && !labels.TryAdd(LabelName(definitions[i].label!), instructions[i]))
                    throw Error("Replacement labels must be unique and cannot shadow captured labels.");
            for (int i = 0; i < definitions.Length; i++)
            {
                OpCode op = GetOpcode(definitions[i].op);
                instructions[i].OpCode = op;
                instructions[i].Operand = ResolveOperand(module, method, op, definitions[i].operand, labels);
            }
            if (wholeBody) { start = 0; count = method.Body.Instructions.Count; }
            return new EditPlan(method, start, count, operation, instructions, wholeBody);
        }

        static object? ResolveOperand(ModuleDefinition module, MethodDefinition method, OpCode op, JToken? token,
            Dictionary<string, Instruction> labels)
        {
            if (op.OperandType == OperandType.InlineNone)
            {
                if (token != null && token.Type != JTokenType.Null) throw Error($"Instruction {op.Name} has no operand.");
                return null;
            }
            if (token == null || token.Type == JTokenType.Null) throw Error($"Instruction {op.Name} requires an operand.");
            switch (op.OperandType)
            {
                case OperandType.ShortInlineBrTarget:
                case OperandType.InlineBrTarget: return ResolveLabel(token, labels);
                case OperandType.InlineSwitch:
                    if (token is not JArray array) throw Error("A switch operand must be an array of labels.");
                    return array.Select(label => ResolveLabel(label, labels)).ToArray();
                case OperandType.InlineString:
                    if (token.Type != JTokenType.String) throw Error("An ldstr operand must be a string.");
                    return token.Value<string>()!;
                case OperandType.InlineI: return checked((int)Integer(token));
                case OperandType.ShortInlineI:
                    if (op.Code == Code.Unaligned) return checked((byte)Integer(token));
                    return checked((sbyte)Integer(token));
                case OperandType.InlineI8: return Integer(token);
                case OperandType.ShortInlineR: return checked((float)Real(token));
                case OperandType.InlineR: return Real(token);
                case OperandType.InlineVar:
                case OperandType.ShortInlineVar:
                case OperandType.InlineArg:
                case OperandType.ShortInlineArg:
                    int index = checked((int)Integer(token));
                    if (op.Name.Contains("loc", StringComparison.Ordinal))
                    {
                        if (index < 0 || index >= method.Body.Variables.Count) throw Error("The local index is outside the method.");
                        return method.Body.Variables[index];
                    }
                    if (index < 0 || index >= method.Parameters.Count + (method.HasThis ? 1 : 0))
                        throw Error("The argument index is outside the method.");
                    return method.HasThis && index == 0 ? method.Body.ThisParameter : method.Parameters[index - (method.HasThis ? 1 : 0)];
                case OperandType.InlineField: return FindReference<FieldReference>(module, token);
                case OperandType.InlineMethod: return FindReference<MethodReference>(module, token);
                case OperandType.InlineType: return FindReference<TypeReference>(module, token);
                case OperandType.InlineTok: return FindReference<MemberReference>(module, token);
                default: throw Error($"Unsupported managed operand type ({op.OperandType}).");
            }
        }

        static T FindReference<T>(ModuleDefinition module, JToken token) where T : MemberReference
        {
            if (token.Type != JTokenType.String) throw Error("A member operand must be its full Cecil signature.");
            string name = token.Value<string>()!;
            IEnumerable<MemberReference> definitions = AllTypes(module.Types).SelectMany(type =>
                new MemberReference[] { type }.Concat(type.Methods).Concat(type.Fields));
            IEnumerable<MemberReference> operands = AllTypes(module.Types).SelectMany(type => type.Methods).Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions).Select(instruction => instruction.Operand).OfType<MemberReference>();
            T[] matches = definitions.Concat(module.GetMemberReferences()).Concat(module.GetTypeReferences()).Concat(operands)
                .OfType<T>().Where(member => member.FullName == name)
                .GroupBy(member => member is TypeReference type ? type.Scope?.ToString() : member.DeclaringType?.Scope?.ToString())
                .Select(group => group.First()).ToArray();
            if (matches.Length != 1) throw Error($"Member ({name}) must identify one existing target reference.");
            return matches[0];
        }

        static Instruction ResolveLabel(JToken token, Dictionary<string, Instruction> labels)
        {
            if (token.Type != JTokenType.String || !token.Value<string>()!.StartsWith("$", StringComparison.Ordinal) ||
                !labels.TryGetValue(LabelName(token.Value<string>()!), out Instruction? instruction))
                throw Error("A branch operand must reference a captured or replacement $label.");
            return instruction;
        }

        static long Integer(JToken token)
        {
            if (token.Type != JTokenType.Integer) throw Error("An integer operand is required.");
            return token.Value<long>();
        }
        static double Real(JToken token)
        {
            if (token.Type is not (JTokenType.Integer or JTokenType.Float)) throw Error("A numeric operand is required.");
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value)) throw Error("A finite operand is required.");
            return value;
        }

        static void Apply(EditPlan plan)
        {
            Mono.Cecil.Cil.MethodBody body = plan.Method.Body;
            if (plan.Operation == "nop")
            {
                for (int i = plan.Start; i < plan.Start + plan.Count; i++)
                { body.Instructions[i].OpCode = OpCodes.Nop; body.Instructions[i].Operand = null; }
                return;
            }
            if (plan.WholeBody)
            {
                body.ExceptionHandlers.Clear();
                body.Instructions.Clear();
                foreach (Instruction instruction in plan.Instructions) body.Instructions.Add(instruction);
                return;
            }
            int insert = plan.Operation == "insertAfter" ? plan.Start + plan.Count : plan.Start;
            if (plan.Operation == "replace")
            {
                Instruction[] removed = body.Instructions.Skip(plan.Start).Take(plan.Count).ToArray();
                HashSet<Instruction> interior = new(removed.Skip(1));
                if (body.Instructions.Concat(plan.Instructions).Any(instruction => Targets(instruction).Any(interior.Contains)) ||
                    body.ExceptionHandlers.Any(handler => Boundaries(handler).Any(interior.Contains)))
                    throw Error($"Patch ({plan.Method.FullName}) removes an interior branch or exception boundary.");
                foreach (Instruction instruction in body.Instructions.Concat(plan.Instructions))
                {
                    if (instruction.Operand is Instruction target && target == removed[0]) instruction.Operand = plan.Instructions[0];
                    else if (instruction.Operand is Instruction[] targets)
                        instruction.Operand = targets.Select(target => target == removed[0] ? plan.Instructions[0] : target).ToArray();
                }
                foreach (ExceptionHandler handler in body.ExceptionHandlers) RemapBoundary(handler, removed[0], plan.Instructions[0]);
                foreach (Instruction instruction in removed) body.Instructions.Remove(instruction);
            }
            for (int i = 0; i < plan.Instructions.Length; i++) body.Instructions.Insert(insert + i, plan.Instructions[i]);
        }

        static IEnumerable<Instruction> Targets(Instruction instruction) => instruction.Operand switch
        { Instruction target => new[] { target }, Instruction[] targets => targets, _ => Array.Empty<Instruction>() };
        static IEnumerable<Instruction> Boundaries(ExceptionHandler handler) =>
            new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart }.Where(i => i != null)!;
        static void RemapBoundary(ExceptionHandler handler, Instruction from, Instruction to)
        {
            if (handler.TryStart == from) handler.TryStart = to;
            if (handler.TryEnd == from) handler.TryEnd = to;
            if (handler.HandlerStart == from) handler.HandlerStart = to;
            if (handler.HandlerEnd == from) handler.HandlerEnd = to;
            if (handler.FilterStart == from) handler.FilterStart = to;
        }

        static void ExpandBranches(MethodDefinition method)
        {
            foreach (Instruction instruction in method.Body.Instructions)
                if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                    instruction.OpCode = GetOpcode(instruction.OpCode.Name.Substring(0, instruction.OpCode.Name.Length - 2));
        }

        static void Validate(MethodDefinition method)
        {
            var body = method.Body;
            if (body.Instructions.Count == 0) throw Error($"Method ({method.FullName}) has an empty body.");
            Dictionary<Instruction, int> indices = body.Instructions.Select((instruction, index) => (instruction, index))
                .ToDictionary(item => item.instruction, item => item.index);
            foreach (Instruction instruction in body.Instructions)
            {
                if (Targets(instruction).Any(target => !indices.ContainsKey(target))) throw Error("A branch targets a removed instruction.");
                ValidateOperand(method, instruction);
                if (instruction.OpCode.OpCodeType == OpCodeType.Prefix && instruction.Next == null)
                    throw Error("A CIL prefix must precede another instruction.");
            }
            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                if (Boundaries(handler).Any(boundary => !indices.ContainsKey(boundary)) || handler.TryStart == null || handler.HandlerStart == null ||
                    indices[handler.TryStart] >= EndIndex(handler.TryEnd, indices) || indices[handler.HandlerStart] >= EndIndex(handler.HandlerEnd, indices) ||
                    handler.HandlerType == ExceptionHandlerType.Filter && (handler.FilterStart == null || indices[handler.FilterStart] >= indices[handler.HandlerStart]))
                    throw Error("The method has an invalid exception region.");
                if (handler.HandlerType == ExceptionHandlerType.Catch && handler.CatchType == null)
                    throw Error("An exception catch requires a catch type.");
            }
            ValidateRegions(method, indices);

            // Check stack shape and known primitive/reference kinds without resolving Unity assemblies.
            // This deliberately does not claim full ECMA verification or inheritance assignability.
            Dictionary<Instruction, StackKind[]> stacks = new();
            Queue<Instruction> pending = new();
            void Enqueue(Instruction instruction, StackKind[] stack)
            {
                if (stacks.TryGetValue(instruction, out StackKind[]? existing))
                {
                    if (stack.Length != existing.Length) throw Error($"Method ({method.FullName}) has incompatible evaluation stacks at a control-flow merge.");
                    bool changed = false;
                    for (int i = 0; i < stack.Length; i++)
                    {
                        if (!Compatible(existing[i], stack[i])) throw Error("A control-flow merge combines incompatible stack kinds.");
                        if (existing[i] != StackKind.Unknown && stack[i] == StackKind.Unknown) { existing[i] = StackKind.Unknown; changed = true; }
                    }
                    if (changed) pending.Enqueue(instruction);
                }
                else { stacks.Add(instruction, stack); pending.Enqueue(instruction); }
            }
            Enqueue(body.Instructions[0], Array.Empty<StackKind>());
            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                Enqueue(handler.HandlerStart, handler.HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter ?
                    new[] { StackKind.Reference } : Array.Empty<StackKind>());
                if (handler.FilterStart != null) Enqueue(handler.FilterStart, new[] { StackKind.Reference });
            }
            int maximum = 0;
            while (pending.Count != 0)
            {
                Instruction instruction = pending.Dequeue();
                StackKind[] before = stacks[instruction];
                int pop = PopCount(method, instruction);
                if (before.Length < pop) throw Error($"Method ({method.FullName}) has an evaluation-stack underflow at {instruction.OpCode.Name}.");
                ValidateKinds(method, instruction, before, pop);
                StackKind[] after = before.Take(before.Length - pop).Concat(PushedKinds(method, instruction, before, pop)).ToArray();
                maximum = Math.Max(maximum, Math.Max(before.Length, after.Length));
                if (instruction.OpCode.Code is Code.Leave or Code.Leave_S) after = Array.Empty<StackKind>();
                if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw || instruction.OpCode.Code is Code.Jmp or Code.Endfinally or Code.Endfilter)
                {
                    if (after.Length != 0) throw Error("A terminal instruction leaves values on the evaluation stack.");
                    continue;
                }
                foreach (Instruction target in Targets(instruction)) Enqueue(target, after);
                if (instruction.OpCode.FlowControl == FlowControl.Branch) continue;
                int next = indices[instruction] + 1;
                if (next == body.Instructions.Count) throw Error("Control flow falls past the end of a method.");
                Enqueue(body.Instructions[next], after);
            }
            body.MaxStackSize = Math.Max(1, maximum);
        }

        enum StackKind { Unknown, Int32, Int64, Float, Native, Reference, Pointer, Value }

        static StackKind Kind(TypeReference type) => type.MetadataType switch
        {
            MetadataType.Boolean or MetadataType.Char or MetadataType.SByte or MetadataType.Byte or MetadataType.Int16 or
                MetadataType.UInt16 or MetadataType.Int32 or MetadataType.UInt32 => StackKind.Int32,
            MetadataType.Int64 or MetadataType.UInt64 => StackKind.Int64,
            MetadataType.Single or MetadataType.Double => StackKind.Float,
            MetadataType.IntPtr or MetadataType.UIntPtr or MetadataType.Pointer or MetadataType.FunctionPointer => StackKind.Native,
            MetadataType.ByReference => StackKind.Pointer,
            MetadataType.String or MetadataType.Object or MetadataType.Class or MetadataType.Array => StackKind.Reference,
            MetadataType.TypedByReference => StackKind.Value,
            // Enums and generic instances need target dependencies to classify accurately.
            _ => StackKind.Unknown
        };

        static bool Compatible(StackKind expected, StackKind actual) => expected == StackKind.Unknown || actual == StackKind.Unknown || expected == actual;

        static TypeReference ArgumentType(MethodDefinition method, int index) => method.HasThis && index == 0 ?
            (method.DeclaringType.IsValueType ? new ByReferenceType(method.DeclaringType) : method.DeclaringType) :
            method.Parameters[index - (method.HasThis ? 1 : 0)].ParameterType;

        static void ValidateKinds(MethodDefinition method, Instruction instruction, StackKind[] stack, int pop)
        {
            void Require(int index, StackKind expected)
            {
                if (!Compatible(expected, stack[index]))
                    throw Error($"Instruction ({instruction.OpCode.Name}) in ({method.FullName}) consumes an incompatible stack kind.");
            }
            Code code = instruction.OpCode.Code;
            if (code == Code.Ret && method.ReturnType.MetadataType != MetadataType.Void)
                Require(stack.Length - 1, Kind(method.ReturnType));
            if (instruction.Operand is MethodReference called && code is Code.Call or Code.Callvirt or Code.Newobj)
            {
                int first = stack.Length - pop;
                bool receiver = called.HasThis && code != Code.Newobj;
                if (receiver && stack[first] is not (StackKind.Unknown or StackKind.Reference or StackKind.Pointer))
                    throw Error("An instance call requires an object or managed-address receiver.");
                for (int i = 0; i < called.Parameters.Count; i++)
                    Require(first + (receiver ? 1 : 0) + i, Kind(called.Parameters[i].ParameterType));
            }
            var canonical = Canonical(instruction.OpCode, instruction.Operand);
            if (canonical.Op == "stloc" && canonical.Operand is int local) Require(stack.Length - 1, Kind(method.Body.Variables[local].VariableType));
            if (canonical.Op == "starg" && canonical.Operand is int argument) Require(stack.Length - 1, Kind(ArgumentType(method, argument)));
            if (instruction.Operand is FieldReference field && code is Code.Stfld or Code.Stsfld) Require(stack.Length - 1, Kind(field.FieldType));
            if (code == Code.Throw) Require(stack.Length - 1, StackKind.Reference);
            if (code == Code.Endfilter) Require(stack.Length - 1, StackKind.Int32);
        }

        static IEnumerable<StackKind> PushedKinds(MethodDefinition method, Instruction instruction, StackKind[] before, int pop)
        {
            int count = PushCount(instruction);
            if (count == 0) return Array.Empty<StackKind>();
            if (instruction.OpCode.Code == Code.Dup) return new[] { before[^1], before[^1] };
            var canonical = Canonical(instruction.OpCode, instruction.Operand);
            StackKind kind = StackKind.Unknown;
            if (canonical.Op == "ldc.i4") kind = StackKind.Int32;
            else if (canonical.Op == "ldc.i8") kind = StackKind.Int64;
            else if (canonical.Op is "ldc.r4" or "ldc.r8") kind = StackKind.Float;
            else if (canonical.Op == "ldarg" && canonical.Operand is int argument) kind = Kind(ArgumentType(method, argument));
            else if (canonical.Op == "ldloc" && canonical.Operand is int local) kind = Kind(method.Body.Variables[local].VariableType);
            else if (canonical.Op is "ldloca" or "ldarga" or "ldflda" or "ldsflda" or "ldelema" or "unbox" or "refanyval") kind = StackKind.Pointer;
            else if (canonical.Op is "ldnull" or "ldstr" or "castclass" or "isinst" or "newarr" or "box" or "ldind.ref" or "ldelem.ref") kind = StackKind.Reference;
            else if (instruction.Operand is FieldReference field && instruction.OpCode.Code is Code.Ldfld or Code.Ldsfld) kind = Kind(field.FieldType);
            else if (instruction.Operand is MethodReference called && instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                kind = Kind(instruction.OpCode.Code == Code.Newobj ? called.DeclaringType : called.ReturnType);
            else if (instruction.Operand is TypeReference type && canonical.Op is "ldobj" or "ldelem.any" or "unbox.any") kind = Kind(type);
            else if (canonical.Op is "ldlen" or "ldftn" or "ldvirtftn" or "localloc" or "ldind.i" or "ldelem.i") kind = StackKind.Native;
            else if (canonical.Op.StartsWith("conv.", StringComparison.Ordinal))
            {
                string conversion = canonical.Op.Substring(5).Replace("ovf.", string.Empty).Replace(".un", string.Empty);
                kind = conversion is "i8" or "u8" ? StackKind.Int64 : conversion is "r4" or "r8" or "r" ? StackKind.Float :
                    conversion is "i" or "u" ? StackKind.Native : StackKind.Int32;
            }
            else if (canonical.Op is "ceq" or "cgt" or "cgt.un" or "clt" or "clt.un" or "sizeof" ||
                canonical.Op.StartsWith("ldind.i", StringComparison.Ordinal) || canonical.Op.StartsWith("ldind.u", StringComparison.Ordinal) ||
                canonical.Op.StartsWith("ldelem.i", StringComparison.Ordinal) || canonical.Op.StartsWith("ldelem.u", StringComparison.Ordinal))
                kind = canonical.Op.EndsWith("i8", StringComparison.Ordinal) ? StackKind.Int64 : StackKind.Int32;
            else if (canonical.Op.StartsWith("ldind.r", StringComparison.Ordinal) || canonical.Op.StartsWith("ldelem.r", StringComparison.Ordinal)) kind = StackKind.Float;
            else if (instruction.OpCode.StackBehaviourPush == StackBehaviour.Push1 && pop > 0 &&
                before.Skip(before.Length - pop).Distinct().Count() == 1) kind = before[^1];
            return Enumerable.Repeat(kind, count);
        }

        static int EndIndex(Instruction? end, Dictionary<Instruction, int> indices) => end == null ? indices.Count : indices[end];

        static void ValidateRegions(MethodDefinition method, Dictionary<Instruction, int> indices)
        {
            var handlers = method.Body.ExceptionHandlers;
            HashSet<(int Handler, string Kind)> Regions(Instruction instruction)
            {
                int index = indices[instruction];
                HashSet<(int, string)> regions = new();
                for (int i = 0; i < handlers.Count; i++)
                {
                    ExceptionHandler handler = handlers[i];
                    if (index >= indices[handler.TryStart] && index < EndIndex(handler.TryEnd, indices)) regions.Add((i, "try"));
                    if (index >= indices[handler.HandlerStart] && index < EndIndex(handler.HandlerEnd, indices)) regions.Add((i, "handler"));
                    if (handler.FilterStart != null && index >= indices[handler.FilterStart] && index < indices[handler.HandlerStart]) regions.Add((i, "filter"));
                }
                return regions;
            }
            foreach (Instruction instruction in method.Body.Instructions)
            {
                var source = Regions(instruction);
                Code code = instruction.OpCode.Code;
                if (code == Code.Ret && source.Count != 0)
                    throw Error("A ret instruction cannot exit a protected exception region.");
                if (instruction.OpCode.OpCodeType == OpCodeType.Prefix && instruction.Next != null)
                {
                    Code following = instruction.Next.OpCode.Code;
                    if (code == Code.Constrained && following != Code.Callvirt || code == Code.Readonly && following != Code.Ldelema ||
                        code == Code.Tail && following is not (Code.Call or Code.Callvirt or Code.Calli))
                        throw Error("A CIL prefix is followed by an incompatible instruction.");
                }
                if (code == Code.Endfinally && !source.Any(region => region.Kind == "handler" &&
                    handlers[region.Handler].HandlerType is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault))
                    throw Error("endfinally must be inside a finally or fault handler.");
                if (code == Code.Endfilter && !source.Any(region => region.Kind == "filter"))
                    throw Error("endfilter must be inside an exception filter.");
                if (code == Code.Rethrow && !source.Any(region => region.Kind == "handler" &&
                    handlers[region.Handler].HandlerType is ExceptionHandlerType.Catch or ExceptionHandlerType.Filter))
                    throw Error("rethrow must be inside a catch handler.");
                foreach (Instruction target in Targets(instruction))
                {
                    if (target.Previous != null && target.Previous.OpCode.OpCodeType == OpCodeType.Prefix)
                        throw Error("A branch cannot enter the middle of a prefixed instruction.");
                    var destination = Regions(target);
                    if (code is Code.Leave or Code.Leave_S)
                    {
                        if (!destination.IsSubsetOf(source) || source.Any(region => region.Kind == "filter" || region.Kind == "handler" &&
                            handlers[region.Handler].HandlerType is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault))
                            throw Error("A leave instruction crosses an invalid exception boundary.");
                    }
                    else if (!source.SetEquals(destination))
                        throw Error("A branch crosses an exception boundary without leave.");
                }
                if (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw ||
                    code is Code.Jmp or Code.Endfinally or Code.Endfilter || instruction.Next == null) continue;
                var next = Regions(instruction.Next);
                if (!source.IsSubsetOf(next) || next.Except(source).Any(region => region.Kind != "try" || handlers[region.Handler].TryStart != instruction.Next))
                    throw Error("Control flow falls across an exception boundary.");
            }
        }

        static void ValidateOperand(MethodDefinition method, Instruction instruction)
        {
            object? operand = instruction.Operand;
            bool valid = instruction.OpCode.OperandType switch
            {
                OperandType.InlineNone => operand == null,
                OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget => operand is Instruction,
                OperandType.InlineSwitch => operand is Instruction[],
                OperandType.InlineField => operand is FieldReference,
                OperandType.InlineMethod => operand is MethodReference,
                OperandType.InlineType => operand is TypeReference,
                OperandType.InlineTok => operand is TypeReference or MethodReference or FieldReference,
                OperandType.InlineString => operand is string,
                OperandType.InlineI => operand is int,
                OperandType.ShortInlineI => operand is sbyte || instruction.OpCode.Code == Code.Unaligned && operand is byte,
                OperandType.InlineI8 => operand is long,
                OperandType.ShortInlineR => operand is float,
                OperandType.InlineR => operand is double,
                OperandType.InlineVar or OperandType.ShortInlineVar => operand is VariableDefinition variable && method.Body.Variables.Contains(variable),
                OperandType.InlineArg or OperandType.ShortInlineArg => operand is ParameterDefinition parameter &&
                    (method.Parameters.Contains(parameter) || method.HasThis && parameter == method.Body.ThisParameter),
                OperandType.InlineSig => operand is CallSite,
                _ => false
            };
            if (!valid) throw Error($"Instruction ({instruction.OpCode.Name}) has an invalid operand.");
            var canonical = Canonical(instruction.OpCode, operand);
            if (canonical.Op is "ldarg" or "ldarga" or "starg" && canonical.Operand is int arg &&
                (arg < 0 || arg >= method.Parameters.Count + (method.HasThis ? 1 : 0))) throw Error("An argument macro is outside the method signature.");
            if (canonical.Op is "ldloc" or "ldloca" or "stloc" && canonical.Operand is int local &&
                (local < 0 || local >= method.Body.Variables.Count)) throw Error("A local macro is outside the method body.");
        }

        static int PopCount(MethodDefinition method, Instruction instruction)
        {
            if (instruction.OpCode.Code == Code.Ret) return method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
            if (instruction.OpCode.Code == Code.Calli && instruction.Operand is CallSite site)
                return site.Parameters.Count + (site.HasThis ? 1 : 0) + 1;
            if (instruction.Operand is MethodReference called && instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
                return called.Parameters.Count + (called.HasThis && instruction.OpCode.Code != Code.Newobj ? 1 : 0);
            return instruction.OpCode.StackBehaviourPop switch
            {
                StackBehaviour.Pop0 => 0,
                StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
                StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or
                    StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
                StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or
                    StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
                StackBehaviour.PopAll => 0,
                _ => throw Error($"Unsupported variable stack behavior ({instruction.OpCode.Name}).")
            };
        }

        static int PushCount(Instruction instruction)
        {
            if (instruction.OpCode.Code == Code.Newobj) return 1;
            if (instruction.OpCode.Code == Code.Calli && instruction.Operand is CallSite site)
                return site.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
            if (instruction.Operand is MethodReference called && instruction.OpCode.Code is Code.Call or Code.Callvirt)
                return called.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
            return instruction.OpCode.StackBehaviourPush switch
            {
                StackBehaviour.Push0 => 0,
                StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
                StackBehaviour.Push1_push1 => 2,
                _ => throw Error($"Unsupported variable stack behavior ({instruction.OpCode.Name}).")
            };
        }

        static string LabelName(string label)
        {
            string name = label.StartsWith("$", StringComparison.Ordinal) ? label.Substring(1) : label;
            if (string.IsNullOrWhiteSpace(name)) throw Error("Instruction labels cannot be empty.");
            return name;
        }
        static OpCode GetOpcode(string name) => name != null && Opcodes.TryGetValue(name, out OpCode op) ? op : throw Error($"Unknown CIL opcode ({name}).");
        static InvalidDataException Error(string message) => new(message);
        sealed record EditPlan(MethodDefinition Method, int Start, int Count, string Operation, Instruction[] Instructions, bool WholeBody);

        /// <summary>Never substitute patcher/host framework libraries for the game's managed dependencies.</summary>
        sealed class TargetAssemblyResolver : IAssemblyResolver
        {
            readonly string? _directory;
            readonly Dictionary<string, AssemblyDefinition> _assemblies = new(StringComparer.Ordinal);
            public TargetAssemblyResolver(string? directory) => _directory = directory;
            public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
            public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
            {
                if (_assemblies.TryGetValue(name.FullName, out AssemblyDefinition? assembly)) return assembly;
                if (_directory == null) throw Error($"Managed dependency ({name.FullName}) requires the matching game's Managed directory.");
                if (Path.GetFileName(name.Name) != name.Name || name.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw Error("A managed dependency name contains an invalid path.");
                string? path = new[] { Path.Combine(_directory, name.Name + ".dll"), Path.Combine(_directory, name.Name + ".exe") }.FirstOrDefault(File.Exists);
                if (path == null) throw Error($"Matching managed dependency ({name.FullName}) was not found in ({_directory}).");
                assembly = AssemblyDefinition.ReadAssembly(path,
                    new ReaderParameters { InMemory = true, ReadSymbols = false, AssemblyResolver = this });
                if (assembly.Name.FullName != name.FullName)
                {
                    assembly.Dispose();
                    throw Error($"Managed dependency ({name.FullName}) has a different assembly identity.");
                }
                _assemblies.Add(name.FullName, assembly);
                return assembly;
            }
            public void Dispose()
            {
                foreach (AssemblyDefinition assembly in _assemblies.Values) assembly.Dispose();
            }
        }
    }
}
