using System;

namespace ClrSpectorConsole
{
    public class SampleClass : IDisposable, IComparable<SampleClass>
    {
        public int IntValue1;
        public int IntValue2Prop { get; set; }
        public readonly int IntValue2;

        [ThreadStatic]
        public static bool BoolThreadStatic;

        static SampleClass()
        {
            SampleClass.BoolThreadStatic = true;
        }

        public SampleClass(int intValue2)
        {
            this.IntValue2 = intValue2;
        }

        ~SampleClass()
        {
            // Just do something to prevent optimization
            SampleClass.BoolThreadStatic = false;
        }

        public bool Method1()
        {
            return true;
        }

        public bool Method2()
        {
            return false;
        }
        public static bool Method3()
        {
            return false;
        }

        public override string ToString()
        {
            return "SamleString";
        }

        public void Dispose()
        {
        }

        public int CompareTo(SampleClass other)
        {
            if (ReferenceEquals(this, other)) return 0;
            if (ReferenceEquals(null, other)) return 1;
            var intValue1Comparison = this.IntValue1.CompareTo(other.IntValue1);
            if (intValue1Comparison != 0) return intValue1Comparison;
            var intValue2Comparison = this.IntValue2.CompareTo(other.IntValue2);
            if (intValue2Comparison != 0) return intValue2Comparison;
            return this.IntValue2Prop.CompareTo(other.IntValue2Prop);
        }
    }
}
