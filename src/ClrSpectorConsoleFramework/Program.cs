using System;
using System.Diagnostics;
using ClrSpectorConsole;
using Microsoft.Diagnostics.Runtime;
using ClrObject = ClrSpector.ClrObject;

namespace ClrSpectorConsoleFramework
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var dataTarget = DataTarget.CreateSnapshotAndAttach(Process.GetCurrentProcess().Id))
            {
                var runtime = dataTarget.ClrVersions[0].CreateRuntime();
                var module = runtime.Modules[1];
                var type = module.GetTypeByName("ClrSpectorConsole.SampleClass");

                var methodTableLoc = new UIntPtr(type.MethodTable);
                var methodDescLoc = new UIntPtr(type.Methods[0].MethodDesc);

                var clrObject = ClrObject.From<SampleClass>();
            }
        }
    }
}
