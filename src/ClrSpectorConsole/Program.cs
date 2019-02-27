using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using ClrObject = ClrSpector.ClrObject;

namespace ClrSpectorConsole
{
    [StructLayout(LayoutKind.Sequential)]
    public struct TestStruct
    {
        public readonly byte Test1;
        public readonly byte Test2;
        public readonly byte Test3;
        public readonly byte Test4;
    }

    class Program
    {
        static void Main(string[] args)
        {
            var coreAssemblyInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location);
            Console.WriteLine($"Hello World from Core {coreAssemblyInfo.ProductVersion}");
            Console.WriteLine($"The location is {typeof(object).Assembly.Location}");

            var sampleClass = new SampleClass(2);

            if (!Debugger.IsAttached)
            {
                Debugger.Launch();
            }

            Debugger.Break();

            ClrObject clrObject;
            //clrObject = ClrObject.From<TestStruct>();
            clrObject = ClrObject.From<SampleClass>();

            /*using (var dataTarget = DataTarget.CreateSnapshotAndAttach(Process.GetCurrentProcess().Id))
            {
                var runtime = dataTarget.ClrVersions[0].CreateRuntime();
                var module = runtime.Modules[1];
                var type = module.GetTypeByName("ClrSpectorConsole.SampleClass");

                var methodTableLoc = new UIntPtr(type.MethodTable);
                var method = type.Methods[0];
                var methodDescLoc = new UIntPtr(method.MethodDesc);
            }*/

        }
    }
}
