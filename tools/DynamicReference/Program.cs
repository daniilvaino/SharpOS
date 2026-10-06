// The reference for `dynamic` (step193): the battery's DynamicCases.cs on
// desktop .NET with the real Microsoft.CSharp binder. Every case states the
// result C# gives; here that statement is checked, SharpOS must agree.
//
//   dotnet run -c Release      (exit 0: all cases as stated)

using System;
using System.Globalization;
using System.Threading;

internal static class Program
{
    private static int Main()
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int pass = AotTests.DynamicCases.Run(Console.WriteLine, out int total);
        Console.WriteLine("==== " + pass + "/" + total + " passed ====");
        return pass == total ? 0 : 1;
    }
}
