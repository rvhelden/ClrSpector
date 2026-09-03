using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ClrSpector.Detours;

namespace ClrSpector
{
    /// <summary>
    /// Builds a real method from decoded IL, so a method body can be replaced with a different
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body cannot be written back over the original. Its IL lives in a read-only mapped
    /// module image, the method is very likely already jitted, and the supported route to new IL
    /// - a profiler's ReJIT - is not reachable in-process. So the new body is <b>emitted as a
    /// method of its own</b> and the target's dispatch slots are pointed at it, which is the same
    /// mechanism <see cref="MethodDetour"/> already uses and is reversible in the same way.
    /// </para>
    /// <para>
    /// The generated method is an <b>instance</b> method whenever the target is one, so the
    /// receiver stays in argument slot zero and the hidden return buffer keeps its place behind
    /// it. Its declared receiver type is the generated host type rather than the target's, which
    /// the JIT never checks - the body's <c>ldarg.0</c> yields the real object.
    /// </para>
    /// <para>
    /// Tokens are why this rebuilds the body instead of copying the bytes. A metadata token only
    /// means anything in the module it came from, so raw IL moved elsewhere would reference the
    /// wrong members. Re-emitting through an <see cref="ILGenerator"/> lets each operand be handed
    /// over as the resolved <see cref="MemberInfo"/>, and the emitter issues a token for the new
    /// module.
    /// </para>
    /// </remarks>
    internal static class MethodIlEmitter
    {
        /// <summary>
        /// Short branches are re-emitted as their long forms. Re-emission moves instructions
        /// relative to each other, and a one-byte displacement that fitted before need not fit
        /// after; the long form always reaches, at the cost of three bytes.
        /// </summary>
        private static readonly Dictionary<short, OpCode> LongBranchForms = new Dictionary<short, OpCode>
        {
            { OpCodes.Br_S.Value, OpCodes.Br },
            { OpCodes.Brfalse_S.Value, OpCodes.Brfalse },
            { OpCodes.Brtrue_S.Value, OpCodes.Brtrue },
            { OpCodes.Beq_S.Value, OpCodes.Beq },
            { OpCodes.Bge_S.Value, OpCodes.Bge },
            { OpCodes.Bgt_S.Value, OpCodes.Bgt },
            { OpCodes.Ble_S.Value, OpCodes.Ble },
            { OpCodes.Blt_S.Value, OpCodes.Blt },
            { OpCodes.Bne_Un_S.Value, OpCodes.Bne_Un },
            { OpCodes.Bge_Un_S.Value, OpCodes.Bge_Un },
            { OpCodes.Bgt_Un_S.Value, OpCodes.Bgt_Un },
            { OpCodes.Ble_Un_S.Value, OpCodes.Ble_Un },
            { OpCodes.Blt_Un_S.Value, OpCodes.Blt_Un },
            { OpCodes.Leave_S.Value, OpCodes.Leave }
        };

        /// <summary>
        /// Emits a method with <paramref name="target"/>'s calling shape whose body is written by
        /// <paramref name="body"/>.
        /// </summary>
        public static MethodInfo Emit(MethodBase target, Action<ILGenerator> body)
        {
            var type = EmittedCode.Module.DefineType(
                EmittedCode.UniqueName("IlPatch", target),
                TypeAttributes.Public | TypeAttributes.Class);

            var isInstance = !target.IsStatic;

            var method = type.DefineMethod(
                "Invoke",
                isInstance ? MethodAttributes.Public : MethodAttributes.Public | MethodAttributes.Static,
                isInstance ? CallingConventions.HasThis : CallingConventions.Standard,
                EmittedCode.Erase(ReturnTypeOf(target)),
                EmittedCode.Erase(ParameterTypesOf(target)));

            method.SetImplementationFlags(
                MethodImplAttributes.NoInlining | MethodImplAttributes.AggressiveOptimization);

            body(method.GetILGenerator());

            return type.CreateType().GetMethod("Invoke");
        }

        /// <summary>
        /// Emits a method whose body is <paramref name="instructions"/>, with
        /// <paramref name="locals"/> declared in slot order.
        /// </summary>
        public static MethodInfo Emit(
            MethodBase target, IReadOnlyList<ClrIlInstruction> instructions, IReadOnlyList<Type> locals)
        {
            if (instructions == null || instructions.Count == 0)
                throw new MethodDetourException(
                    "The replacement body has no instructions. A method body must at least return.");

            return Emit(target, il => Write(il, instructions, locals));
        }

        private static void Write(
            ILGenerator il, IReadOnlyList<ClrIlInstruction> instructions, IReadOnlyList<Type> locals)
        {
            if (locals != null)
            {
                foreach (var local in locals)
                    il.DeclareLocal(local);
            }

            var offsets = new HashSet<int>();
            foreach (var instruction in instructions)
                offsets.Add(instruction.Offset);

            // Branches are re-pointed by label rather than by offset, so every offset something
            // jumps to needs a label first - and has to be a real instruction. A label that is
            // never marked fails deep inside the emitter with nothing to say about which branch
            // was wrong, so the targets are checked here where the message can be useful.
            var labels = new Dictionary<int, Label>();

            foreach (var instruction in instructions)
            {
                foreach (var target in TargetsOf(instruction))
                {
                    if (!offsets.Contains(target))
                        throw new MethodDetourException(
                            $"The {instruction.OpCode.Name} at IL_{instruction.Offset:x4} goes to " +
                            $"IL_{target:x4}, which is not the offset of any instruction in the " +
                            "body. A branch has to name an instruction that is present.");

                    if (!labels.ContainsKey(target))
                        labels[target] = il.DefineLabel();
                }
            }

            foreach (var instruction in instructions)
            {
                if (labels.TryGetValue(instruction.Offset, out var label))
                    il.MarkLabel(label);

                WriteOne(il, instruction, labels);
            }
        }

        private static IEnumerable<int> TargetsOf(ClrIlInstruction instruction)
        {
            switch (instruction.Operand)
            {
                case ClrIlBranchTarget branch:
                    yield return branch.Target;

                    break;

                case int[] targets:
                    foreach (var target in targets)
                        yield return target;

                    break;
            }
        }

        /// <summary>
        /// Emits one instruction, dispatching on the opcode's declared operand type rather than
        /// on what the operand happens to be.
        /// </summary>
        /// <remarks>
        /// The opcode is the authority on what its operand must be, and going the other way lets
        /// a wrong operand through: an unresolved metadata token reads back as a plain
        /// <see cref="int"/>, which would emit as <c>call 0x06000001</c> - a token naming
        /// something else entirely in the generated module, and an invalid program by the time
        /// anyone finds out. Every mismatch is refused here instead.
        /// </remarks>
        private static void WriteOne(
            ILGenerator il, ClrIlInstruction instruction, Dictionary<int, Label> labels)
        {
            var opCode = instruction.OpCode;

            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    il.Emit(opCode);
                    return;

                case OperandType.ShortInlineBrTarget:
                case OperandType.InlineBrTarget:
                    il.Emit(LongForm(opCode), labels[Expect<ClrIlBranchTarget>(instruction).Target]);
                    return;

                case OperandType.InlineSwitch:
                {
                    var targets = Expect<int[]>(instruction);
                    var switchLabels = new Label[targets.Length];

                    for (var i = 0; i < targets.Length; i++)
                        switchLabels[i] = labels[targets[i]];

                    il.Emit(opCode, switchLabels);
                    return;
                }

                case OperandType.InlineString:
                    il.Emit(opCode, Expect<string>(instruction));
                    return;

                case OperandType.InlineType:
                    il.Emit(opCode, Expect<Type>(instruction));
                    return;

                case OperandType.InlineField:
                    il.Emit(opCode, Expect<FieldInfo>(instruction));
                    return;

                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                    WriteMemberOperand(il, instruction);
                    return;

                case OperandType.ShortInlineI:
                    il.Emit(opCode, Convert.ToSByte(Number(instruction)));
                    return;

                case OperandType.InlineI:
                    il.Emit(opCode, Convert.ToInt32(Number(instruction)));
                    return;

                case OperandType.InlineI8:
                    il.Emit(opCode, Convert.ToInt64(Number(instruction)));
                    return;

                case OperandType.ShortInlineR:
                    il.Emit(opCode, Convert.ToSingle(Number(instruction)));
                    return;

                case OperandType.InlineR:
                    il.Emit(opCode, Convert.ToDouble(Number(instruction)));
                    return;

                case OperandType.ShortInlineVar:
                    il.Emit(opCode, Convert.ToByte(Number(instruction)));
                    return;

                case OperandType.InlineVar:
                    il.Emit(opCode, Convert.ToInt16(Number(instruction)));
                    return;

                default:
                    throw new MethodDetourException(
                        $"The instruction at IL_{instruction.Offset:x4} ({opCode.Name}) has operand " +
                        $"type {opCode.OperandType}, which cannot be re-emitted. A standalone " +
                        "signature has no equivalent outside its original module.");
            }
        }

        private static void WriteMemberOperand(ILGenerator il, ClrIlInstruction instruction)
        {
            var opCode = instruction.OpCode;
            var isToken = opCode.OperandType == OperandType.InlineTok;

            switch (instruction.Operand)
            {
                case ConstructorInfo constructor:
                    il.Emit(opCode, constructor);
                    return;

                case MethodInfo method:
                    il.Emit(opCode, method);
                    return;

                case FieldInfo field when isToken:
                    il.Emit(opCode, field);
                    return;

                case Type type when isToken:
                    il.Emit(opCode, type);
                    return;

                default:
                    throw Mismatch(instruction, isToken
                        ? "a resolved method, field or type"
                        : "a resolved method");
            }
        }

        private static OpCode LongForm(OpCode opCode)
        {
            return LongBranchForms.TryGetValue(opCode.Value, out var longForm) ? longForm : opCode;
        }

        private static T Expect<T>(ClrIlInstruction instruction) where T : class
        {
            return instruction.Operand as T ?? throw Mismatch(instruction, typeof(T).Name);
        }

        private static object Number(ClrIlInstruction instruction)
        {
            if (instruction.Operand is IConvertible && !(instruction.Operand is string))
                return instruction.Operand;

            throw Mismatch(instruction, "a number");
        }

        private static MethodDetourException Mismatch(ClrIlInstruction instruction, string wanted)
        {
            var actual = instruction.Operand?.GetType().Name ?? "nothing";

            return new MethodDetourException(
                $"The instruction at IL_{instruction.Offset:x4} ({instruction.OpCode.Name}) needs " +
                $"{wanted} as its operand, but has {actual}. An unresolved metadata token reads " +
                "back as a plain integer and means nothing in the generated module, so it is " +
                "refused rather than emitted against whatever member happens to share that number.");
        }

        private static Type ReturnTypeOf(MethodBase method)
        {
            return method is MethodInfo info ? info.ReturnType : typeof(void);
        }

        private static Type[] ParameterTypesOf(MethodBase method)
        {
            var parameters = method.GetParameters();
            var types = new Type[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
                types[i] = parameters[i].ParameterType;

            return types;
        }
    }
}