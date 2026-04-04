namespace SharpOS.AppSdk
{
    internal static unsafe class StringExperimentSuite
    {
        public static uint RunSelected(out uint testId)
        {
#if EXP_TEST_01
            testId = 1;
            return Test01_Length();
#elif EXP_TEST_02
            testId = 2;
            return Test02_Indexer_FirstChar();
#elif EXP_TEST_03
            testId = 3;
            return Test03_Indexer_LoopSum();
#elif EXP_TEST_07
            testId = 7;
            return Test07_StringEqualsLiteral();
#elif EXP_TEST_08
            testId = 8;
            return Test08_StringNotEquals();
#elif EXP_TEST_09
            testId = 9;
            return Test09_AsciiEncode_Indexer();
#elif EXP_TEST_10
            testId = 10;
            return Test10_Utf16LeEncode_Indexer();
#elif EXP_TEST_11
            testId = 11;
            return Test11_Utf8Encode_Bmp_NoPin();
#elif EXP_TEST_12
            testId = 12;
            return Test12_FixedString();
#elif EXP_TEST_13
            testId = 13;
            return Test13_GetPinnableReference();
#elif EXP_TEST_16
            testId = 16;
            return Test16_NewStringRepeatChar();
#elif EXP_TEST_18
            testId = 18;
            return Test18_ConcatVariableLiteral();
#elif EXP_TEST_90
            testId = 90;
            return Test90_StringLayoutDiagnostics();
#else
            testId = 0;
            return 0;
#endif
        }

#if EXP_TEST_01
        private static uint Test01_Length()
        {
            string s = "abc";
            return (uint)s.Length;
        }
#endif

#if EXP_TEST_02
        private static uint Test02_Indexer_FirstChar()
        {
            string s = "abc";
            return s[0];
        }
#endif

#if EXP_TEST_03
        private static uint Test03_Indexer_LoopSum()
        {
            string s = "abc";
            uint sum = 0;

            for (int i = 0; i < s.Length; i++)
                sum += s[i];

            return sum;
        }
#endif

#if EXP_TEST_07
        private static uint Test07_StringEqualsLiteral()
        {
            string s = "abc";
            return s == "abc" ? 1u : 0u;
        }
#endif

#if EXP_TEST_08
        private static uint Test08_StringNotEquals()
        {
            string s = "abc";
            return s != "abd" ? 1u : 0u;
        }
#endif

#if EXP_TEST_09
        private static uint Test09_AsciiEncode_Indexer()
        {
            string s = "ABC";
            byte* buffer = stackalloc byte[s.Length];

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                buffer[i] = c <= 0x7F ? (byte)c : (byte)'?';
            }

            uint sum = 0;
            for (int i = 0; i < s.Length; i++)
                sum += buffer[i];

            return sum;
        }
#endif

#if EXP_TEST_10
        private static uint Test10_Utf16LeEncode_Indexer()
        {
            string s = "AB";
            byte* buffer = stackalloc byte[s.Length * 2];
            int writeIndex = 0;

            for (int i = 0; i < s.Length; i++)
            {
                ushort value = s[i];
                buffer[writeIndex++] = (byte)(value & 0xFF);
                buffer[writeIndex++] = (byte)(value >> 8);
            }

            uint sum = 0;
            for (int i = 0; i < writeIndex; i++)
                sum += buffer[i];

            return sum;
        }
#endif

#if EXP_TEST_11
        private static uint Test11_Utf8Encode_Bmp_NoPin()
        {
            string s = "AЖ";
            byte* buffer = stackalloc byte[16];
            int writeIndex = 0;

            for (int i = 0; i < s.Length; i++)
            {
                uint c = s[i];
                if (c <= 0x7F)
                {
                    buffer[writeIndex++] = (byte)c;
                }
                else if (c <= 0x7FF)
                {
                    buffer[writeIndex++] = (byte)(0xC0 | (c >> 6));
                    buffer[writeIndex++] = (byte)(0x80 | (c & 0x3F));
                }
                else
                {
                    buffer[writeIndex++] = (byte)(0xE0 | (c >> 12));
                    buffer[writeIndex++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                    buffer[writeIndex++] = (byte)(0x80 | (c & 0x3F));
                }
            }

            uint sum = 0;
            for (int i = 0; i < writeIndex; i++)
                sum += buffer[i];

            return sum;
        }
#endif

#if EXP_TEST_12
        private static uint Test12_FixedString()
        {
            string s = "abc";
            uint sum = 0;

            fixed (char* p = s)
            {
                for (int i = 0; i < s.Length; i++)
                    sum += p[i];
            }

            return sum;
        }
#endif

#if EXP_TEST_13
        private static uint Test13_GetPinnableReference()
        {
            string s = "abc";
            fixed (char* p = &s.GetPinnableReference())
            {
                return p[0];
            }
        }
#endif

#if EXP_TEST_16
        private static uint Test16_NewStringRepeatChar()
        {
            string s = new string('A', 4);
            if (s.Length != 4)
                return 100;

            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != 'A')
                    return (uint)(110 + i);
            }

            fixed (char* p = s)
            {
                if (p[4] != '\0')
                    return 120;
            }

            return 4;
        }
#endif

#if EXP_TEST_18
        private static uint Test18_ConcatVariableLiteral()
        {
            string s = "a";
            string t = s + "b";
            return (uint)t.Length;
        }
#endif

#if EXP_TEST_90
        private static uint Test90_StringLayoutDiagnostics()
        {
            string s = "abc";
            string t = "xyz";

            const string LabelObjS = "obj_s=";
            const string LabelObjT = "obj_t=";
            const string LabelPtrS = "ptr_s=";
            const string LabelPtrT = "ptr_t=";
            const string LabelLenProp = "len_prop=";
            const string LabelLenRaw = "len_raw=";
            const string LabelOffset = "offset=";
            const string LabelRaw0 = "raw0=";
            const string LabelRaw1 = "raw1=";
            const string LabelRaw2 = "raw2=";
            const string LabelC0 = "c0=";
            const string LabelC1 = "c1=";
            const string LabelC2 = "c2=";
            const string NewLine = "\n";

            fixed (char* ps = s)
            fixed (char* pt = t)
            {
                int offsetToData = System.Runtime.CompilerServices.RuntimeHelpers.OffsetToStringData;
                byte* objS = ((byte*)ps) - offsetToData;
                byte* objT = ((byte*)pt) - offsetToData;
                int rawLen = *((int*)(objS + 8));
                ulong raw0 = *((ulong*)(objS + 0));
                ulong raw1 = *((ulong*)(objS + 8));
                ulong raw2 = *((ulong*)(objS + 16));

                AppHost.WriteString(LabelObjS);
                AppHost.WriteHex((ulong)objS);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelObjT);
                AppHost.WriteHex((ulong)objT);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelPtrS);
                AppHost.WriteHex((ulong)ps);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelPtrT);
                AppHost.WriteHex((ulong)pt);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelLenProp);
                AppHost.WriteUInt((uint)s.Length);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelLenRaw);
                AppHost.WriteUInt((uint)rawLen);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelOffset);
                AppHost.WriteUInt((uint)offsetToData);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelRaw0);
                AppHost.WriteHex(raw0);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelRaw1);
                AppHost.WriteHex(raw1);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelRaw2);
                AppHost.WriteHex(raw2);
                AppHost.WriteString(NewLine);

                uint c0 = rawLen > 0 ? ps[0] : 0u;
                uint c1 = rawLen > 1 ? ps[1] : 0u;
                uint c2 = rawLen > 2 ? ps[2] : 0u;

                AppHost.WriteString(LabelC0);
                AppHost.WriteUInt(c0);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelC1);
                AppHost.WriteUInt(c1);
                AppHost.WriteString(NewLine);

                AppHost.WriteString(LabelC2);
                AppHost.WriteUInt(c2);
                AppHost.WriteString(NewLine);

                if (s.Length == 3 && rawLen == 3 && c0 == (uint)'a' && c1 == (uint)'b' && c2 == (uint)'c')
                    return 1;

                return 0;
            }
        }
#endif
    }
}
