using ClrSpector;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrSpectorTests
{
    [TestClass]
    public class ClrObjectTests
    {
        [TestMethod]
        public void TestMethodTable()
        {
            var clrObject = ClrObject.From<SampleClass>();

        }
    }
}
