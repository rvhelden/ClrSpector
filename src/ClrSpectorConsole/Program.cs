using System;
using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;
using ClrObject = ClrSpector.ClrObject;

namespace ClrSpectorConsole
{
    class Program
    {
        static void Main(string[] args)
        {
            ClrObject clrObject;
            clrObject = ClrObject.From<SampleClass>();
            
            using (var dataTarget = DataTarget.CreateSnapshotAndAttach(Process.GetCurrentProcess().Id))
            {
                var runtime = dataTarget.ClrVersions[0].CreateRuntime();
                var module = runtime.Modules[1];
                var type = module.GetTypeByName("ClrSpectorConsole.SampleClass");

                var methodTableLoc = new UIntPtr(type.MethodTable);
                var method = type.Methods[0];
                var methodDescLoc = new UIntPtr(method.MethodDesc);

                clrObject = ClrObject.From<SampleClass>();
            }

        }
    }
}
