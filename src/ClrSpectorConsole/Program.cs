using System;
using ClrSpector;

namespace ClrSpectorConsole
{
    class Program
    {
        static void Main(string[] args)
        {
            var clrObject = ClrObject.From<SampleClass>();
        }
    }
}
