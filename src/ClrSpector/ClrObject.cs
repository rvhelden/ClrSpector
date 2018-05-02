using System.Runtime.CompilerServices;

namespace ClrSpector
{
    public class ClrObject
    {
        public ClrMethodTable MethodTable { get; set; }

        public static ClrObject From<T>()
        {
            var type = typeof(T);

            foreach (var info in type.GetMethods())
                RuntimeHelpers.PrepareMethod(info.MethodHandle);

            foreach (var info in type.GetConstructors())
                RuntimeHelpers.PrepareMethod(info.MethodHandle);

            var reader = new MemoryReader(type.TypeHandle.Value);

            var clrObject = new ClrObject();
            clrObject.MethodTable = ClrMethodTable.Create(reader);

            return clrObject;
        }
    }
}
