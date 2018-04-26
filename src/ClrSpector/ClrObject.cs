namespace ClrSpector
{
    public class ClrObject
    {
        public ClrMethodTable MethodTable { get; set; }

        public static ClrObject From<T>()
        {
            var type = typeof(T);
            var reader = new MemoryReader(type.TypeHandle.Value);

            var clrObject = new ClrObject();
            clrObject.MethodTable = ClrMethodTable.Create(reader);

            return clrObject;
        }
    }
}
