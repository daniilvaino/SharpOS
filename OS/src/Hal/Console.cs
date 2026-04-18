using OS.Kernel.Memory;
using SharpOS.Std.NoRuntime;

namespace OS.Hal
{
    internal static unsafe class Console
    {
        // Default API — идёт через NumberFormatting (managed-путь).
        // Для раннего boot (до KernelHeap.Init) автоматически откатывается на
        // stackalloc fallback (*Raw), чтобы числа в логе не терялись.
        //
        // *Raw варианты — для кода, который не может аллоцировать во время работы
        // (HeapDiagnostics, итерирующий список блоков heap). Вызов managed-пути
        // в таких контекстах создаст новый блок в том же list и приведёт к
        // бесконечной итерации.
        //
        // ВАЖНО: без Runtime.WorkstationGC.lib (step 28 phase 3.1) frozen-string
        // literals (включая string.Empty) не инициализируются на старте. Поэтому
        // CAN'T безопасно проверять s.Length на возврат из NumberFormatting —
        // если heap не готов, FastAllocateString вернёт невалидный string.Empty
        // и s.Length крашит на чтении [s+8]. Проверяем готовность heap ЯВНО.

        public static void Write(string text) => Platform.Write(text);

        public static void WriteLine(string text) => Platform.WriteLine(text);

        public static void WriteChar(char value) => Platform.WriteChar(value);

        public static void WriteInt(int value)
        {
            if (!KernelHeap.IsInitialized)
            {
                WriteIntRaw(value);
                return;
            }
            string s = NumberFormatting.IntToString(value);
            if (s.Length > 0)
            {
                Write(s);
                return;
            }
            WriteIntRaw(value);
        }

        public static void WriteUInt(uint value)
        {
            if (!KernelHeap.IsInitialized)
            {
                WriteUIntRaw(value);
                return;
            }
            string s = NumberFormatting.UIntToString(value);
            if (s.Length > 0)
            {
                Write(s);
                return;
            }
            WriteUIntRaw(value);
        }

        public static void WriteULong(ulong value)
        {
            if (!KernelHeap.IsInitialized)
            {
                WriteULongRaw(value);
                return;
            }
            string s = NumberFormatting.ULongToString(value);
            if (s.Length > 0)
            {
                Write(s);
                return;
            }
            WriteULongRaw(value);
        }

        public static void WriteHex(ulong value)
        {
            WriteHex(value, 1);
        }

        public static void WriteHex(ulong value, int minDigits)
        {
            if (!KernelHeap.IsInitialized)
            {
                WriteHexRaw(value, minDigits);
                return;
            }
            string s = NumberFormatting.ULongToHex(value, minDigits);
            if (s.Length > 0)
            {
                Write(s);
                return;
            }
            WriteHexRaw(value, minDigits);
        }

        // ---- Raw (stackalloc) path ----
        // Публичные для кода, который обязан избегать аллокаций heap во время
        // своей работы — например, итератор HeapBlock* → block.Next.
        // Обычный Console.* в таком контексте аллоцировал бы новые блоки в тот
        // же linked-list, и итерация ушла бы в бесконечность.

        public static void WriteIntRaw(int value)
        {
            if (value == 0)
            {
                WriteChar('0');
                return;
            }

            if (value < 0)
            {
                if (value == -2147483648)
                {
                    Write("-2147483648");
                    return;
                }

                WriteChar('-');
                value = -value;
            }

            WriteUIntRaw((uint)value);
        }

        public static void WriteUIntRaw(uint value)
        {
            if (value == 0)
            {
                WriteChar('0');
                return;
            }

            char* digits = stackalloc char[10];
            int len = 0;

            while (value > 0)
            {
                uint digit = value % 10;
                digits[len++] = (char)('0' + digit);
                value /= 10;
            }

            for (int i = len - 1; i >= 0; i--)
                WriteChar(digits[i]);
        }

        public static void WriteULongRaw(ulong value)
        {
            if (value == 0)
            {
                WriteChar('0');
                return;
            }

            char* digits = stackalloc char[20];
            int len = 0;

            while (value > 0)
            {
                ulong digit = value % 10;
                digits[len++] = (char)('0' + digit);
                value /= 10;
            }

            for (int i = len - 1; i >= 0; i--)
                WriteChar(digits[i]);
        }

        public static void WriteHexRaw(ulong value, int minDigits)
        {
            if (minDigits < 1)
                minDigits = 1;
            else if (minDigits > 16)
                minDigits = 16;

            char* digits = stackalloc char[16];
            int len = 0;

            do
            {
                int nibble = (int)(value & 0xFUL);
                digits[len++] = (char)(nibble < 10 ? ('0' + nibble) : ('A' + (nibble - 10)));
                value >>= 4;
            } while (value != 0);

            while (len < minDigits)
                digits[len++] = '0';

            for (int i = len - 1; i >= 0; i--)
                WriteChar(digits[i]);
        }
    }
}
