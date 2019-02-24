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
            if (!Debugger.IsAttached)
            {
                Debugger.Launch();
            }

            var coreAssemblyInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location);
            Console.WriteLine($"Hello World from Core {coreAssemblyInfo.ProductVersion}");
            Console.WriteLine($"The location is {typeof(object).Assembly.Location}");

            ClrObject clrObject;
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
