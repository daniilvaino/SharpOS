using System;
using System.Threading.Tasks;

namespace AotTests
{
    // [ThreadStatic]: each thread sees its own copy, a reference held only by a
    // thread static survives a collection, and a generic type's thread statics
    // (which ILC does not inline into the module's block) work the same way.
    internal static unsafe partial class AppEntry
    {
        private sealed class Box
        {
            public int Value;
        }

        private static class PerThread
        {
            [ThreadStatic] public static int Number;
            [ThreadStatic] public static Box Held;
        }

        private static class PerThreadOf<T>
        {
            [ThreadStatic] public static T Value;
        }

        // A thread static on a type with a static constructor: ILC reaches it
        // through ClassConstructorRunner's thread-static flavor.
        private static class WithCctor
        {
            public static int Ran;
            [ThreadStatic] public static int Value;

            static WithCctor() { Ran = 1; }
        }

        private static void CheckThreadStatics()
        {
            PerThread.Number = 11;
            PerThread.Held = new Box { Value = 12 };
            PerThreadOf<int>.Value = 13;
            PerThreadOf<string>.Value = "fourteen";

            int otherNumber = -1, otherGeneric = -1, otherRandom = -1;
            bool otherHeldEmpty = false, otherStringEmpty = false;
            int ownRandom = Random.Shared.Next(1000);
            if (SharpOS.AppSdk.AppThreads.IsAvailable)
            {
                Task.Run(() =>
                {
                    otherNumber = PerThread.Number;
                    otherHeldEmpty = PerThread.Held == null;
                    otherGeneric = PerThreadOf<int>.Value;
                    otherStringEmpty = PerThreadOf<string>.Value == null;
                    otherRandom = Random.Shared.Next(1000);
                    PerThread.Number = 21;
                    PerThreadOf<int>.Value = 23;
                }).Wait();
            }

            GC.Collect();

            Check("thread static: own value", PerThread.Number == 11);
            Check("thread static: reference survives collection",
                  PerThread.Held != null && PerThread.Held.Value == 12);
            Check("thread static: generic", PerThreadOf<int>.Value == 13
                  && PerThreadOf<string>.Value == "fourteen");

            int before = WithCctor.Value;
            WithCctor.Value = 15;
            Check("thread static: type with a static constructor",
                  before == 0 && WithCctor.Value == 15 && WithCctor.Ran == 1);
            if (SharpOS.AppSdk.AppThreads.IsAvailable)
            {
                Check("thread static: other thread starts empty",
                      otherNumber == 0 && otherHeldEmpty && otherGeneric == 0 && otherStringEmpty);

                // Random.Shared keeps a generator per thread (upstream again,
                // step209): both threads get one, in range.
                Check("Random.Shared on two threads",
                      (uint)ownRandom < 1000 && (uint)otherRandom < 1000);
            }
        }
    }
}
