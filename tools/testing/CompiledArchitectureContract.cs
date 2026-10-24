using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SAAIA.Testing;

/// <summary>
/// Inspects compiled dependencies/literals, including async state machines. These
/// are architecture guards, not SQL execution tests; integration tests cover SQL behavior.
/// </summary>
internal static class CompiledArchitectureContract
{
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.FieldType == typeof(OpCode))
        .Select(static field => (OpCode)field.GetValue(null)!)
        .ToDictionary(static code => code.Value);

    public static IReadOnlyList<string> Strings(Type type)
        => Types(type).SelectMany(static current => Methods(current).SelectMany(Read)
            .Select(static item => item.Value).OfType<string>()
            .Concat(current.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(static field => (string)field.GetRawConstantValue()!))).ToArray();

    public static IReadOnlyList<string> StringsForMethod(Type type, string name)
        => InstructionsForMethod(type, name).Select(static item => item.Value).OfType<string>().ToArray();

    public static IReadOnlyList<MethodBase> CallsForMethod(Type type, string name)
        => InstructionsForMethod(type, name).Where(static item => item.Code.FlowControl == FlowControl.Call)
            .Select(static item => item.Value).OfType<MethodBase>().ToArray();

    private static IEnumerable<(OpCode Code, object? Value)> InstructionsForMethod(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Contract method not found: {type.Name}.{name}");
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        return Read(method).Concat(stateMachine is null ? [] : Methods(stateMachine).SelectMany(Read));
    }

    public static IReadOnlyList<MethodBase> Calls(Type type)
        => Types(type).SelectMany(Methods).SelectMany(Read)
            .Where(static item => item.Code.FlowControl == FlowControl.Call)
            .Select(static item => item.Value).OfType<MethodBase>().ToArray();

    private static IEnumerable<Type> Types(Type type)
        => new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(Types));

    private static IEnumerable<MethodBase> Methods(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance));

    private static IEnumerable<(OpCode Code, object? Value)> Read(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        for (var offset = 0; offset < bytes.Length;)
        {
            short key = bytes[offset++];
            if (key == 0xfe) key = (short)(0xfe00 | bytes[offset++]);
            var code = Codes[key];
            object? value = null;
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4
            };
            if (code.OperandType == OperandType.InlineString)
                value = method.Module.ResolveString(BitConverter.ToInt32(bytes, offset));
            else if (code.OperandType == OperandType.InlineMethod)
                value = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
            yield return (code, value);
            offset += size;
        }
    }
}
