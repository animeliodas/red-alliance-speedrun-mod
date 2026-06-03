// Polyfills for attributes the Roslyn compiler emits on `async` / `yield` / `params`
// methods but which don't exist in Unity 2017.4's .NET 3.5 mscorlib.
// Defining them in our own assembly lets Mono resolve the references at load time.

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class IteratorStateMachineAttribute : StateMachineAttribute
    {
        public IteratorStateMachineAttribute(Type stateMachineType) : base(stateMachineType) { }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class AsyncStateMachineAttribute : StateMachineAttribute
    {
        public AsyncStateMachineAttribute(Type stateMachineType) : base(stateMachineType) { }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal class StateMachineAttribute : Attribute
    {
        public Type StateMachineType { get; private set; }
        public StateMachineAttribute(Type stateMachineType) { StateMachineType = stateMachineType; }
    }
}
