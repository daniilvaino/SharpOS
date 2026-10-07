// Fifty [Message] types for a mixed stream (step195, test 6): generated, the shapes cycle through six field kinds.
using SharpOS.Std.Pipes;

namespace PipeApps
{
    public abstract class Mixed
    {
        public abstract bool Check(int seq);
    }

    [Message]
    public sealed class M0 : Mixed
    {
        public int F0;
        public long F1;
        public static M0 Make(int seq) => new M0
        {
            F0 = seq * 1,
            F1 = (long)seq << 0,
        };
        public override bool Check(int seq) => F0 == seq * 1 && F1 == (long)seq << 0;
    }

    [Message]
    public sealed class M1 : Mixed
    {
        public long F0;
        public string F1;
        public double F2;
        public static M1 Make(int seq) => new M1
        {
            F0 = (long)seq << 1,
            F1 = "m1." + seq.ToString(),
            F2 = seq + 1.5,
        };
        public override bool Check(int seq) => F0 == (long)seq << 1 && F1 == "m1." + seq.ToString() && F2 == seq + 1.5;
    }

    [Message]
    public sealed class M2 : Mixed
    {
        public string F0;
        public double F1;
        public int[] F2;
        public string[] F3;
        public static M2 Make(int seq) => new M2
        {
            F0 = "m2." + seq.ToString(),
            F1 = seq + 2.5,
            F2 = new[] { seq, 2 },
            F3 = new[] { "a2", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == "m2." + seq.ToString() && F1 == seq + 2.5 && F2.Length == 2 && F2[0] == seq && F2[1] == 2 && F3.Length == 2 && F3[0] == "a2" && F3[1] == seq.ToString();
    }

    [Message]
    public sealed class M3 : Mixed
    {
        public double F0;
        public int[] F1;
        public string[] F2;
        public int F3;
        public long F4;
        public static M3 Make(int seq) => new M3
        {
            F0 = seq + 3.5,
            F1 = new[] { seq, 3 },
            F2 = new[] { "a3", seq.ToString() },
            F3 = seq * 4,
            F4 = (long)seq << 3,
        };
        public override bool Check(int seq) => F0 == seq + 3.5 && F1.Length == 2 && F1[0] == seq && F1[1] == 3 && F2.Length == 2 && F2[0] == "a3" && F2[1] == seq.ToString() && F3 == seq * 4 && F4 == (long)seq << 3;
    }

    [Message]
    public sealed class M4 : Mixed
    {
        public int[] F0;
        public string[] F1;
        public static M4 Make(int seq) => new M4
        {
            F0 = new[] { seq, 4 },
            F1 = new[] { "a4", seq.ToString() },
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == seq && F0[1] == 4 && F1.Length == 2 && F1[0] == "a4" && F1[1] == seq.ToString();
    }

    [Message]
    public sealed class M5 : Mixed
    {
        public string[] F0;
        public int F1;
        public long F2;
        public static M5 Make(int seq) => new M5
        {
            F0 = new[] { "a5", seq.ToString() },
            F1 = seq * 6,
            F2 = (long)seq << 5,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == "a5" && F0[1] == seq.ToString() && F1 == seq * 6 && F2 == (long)seq << 5;
    }

    [Message]
    public sealed class M6 : Mixed
    {
        public int F0;
        public long F1;
        public string F2;
        public double F3;
        public static M6 Make(int seq) => new M6
        {
            F0 = seq * 7,
            F1 = (long)seq << 6,
            F2 = "m6." + seq.ToString(),
            F3 = seq + 6.5,
        };
        public override bool Check(int seq) => F0 == seq * 7 && F1 == (long)seq << 6 && F2 == "m6." + seq.ToString() && F3 == seq + 6.5;
    }

    [Message]
    public sealed class M7 : Mixed
    {
        public long F0;
        public string F1;
        public double F2;
        public int[] F3;
        public string[] F4;
        public static M7 Make(int seq) => new M7
        {
            F0 = (long)seq << 7,
            F1 = "m7." + seq.ToString(),
            F2 = seq + 7.5,
            F3 = new[] { seq, 7 },
            F4 = new[] { "a7", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == (long)seq << 7 && F1 == "m7." + seq.ToString() && F2 == seq + 7.5 && F3.Length == 2 && F3[0] == seq && F3[1] == 7 && F4.Length == 2 && F4[0] == "a7" && F4[1] == seq.ToString();
    }

    [Message]
    public sealed class M8 : Mixed
    {
        public string F0;
        public double F1;
        public static M8 Make(int seq) => new M8
        {
            F0 = "m8." + seq.ToString(),
            F1 = seq + 8.5,
        };
        public override bool Check(int seq) => F0 == "m8." + seq.ToString() && F1 == seq + 8.5;
    }

    [Message]
    public sealed class M9 : Mixed
    {
        public double F0;
        public int[] F1;
        public string[] F2;
        public static M9 Make(int seq) => new M9
        {
            F0 = seq + 9.5,
            F1 = new[] { seq, 9 },
            F2 = new[] { "a9", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == seq + 9.5 && F1.Length == 2 && F1[0] == seq && F1[1] == 9 && F2.Length == 2 && F2[0] == "a9" && F2[1] == seq.ToString();
    }

    [Message]
    public sealed class M10 : Mixed
    {
        public int[] F0;
        public string[] F1;
        public int F2;
        public long F3;
        public static M10 Make(int seq) => new M10
        {
            F0 = new[] { seq, 10 },
            F1 = new[] { "a10", seq.ToString() },
            F2 = seq * 11,
            F3 = (long)seq << 10,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == seq && F0[1] == 10 && F1.Length == 2 && F1[0] == "a10" && F1[1] == seq.ToString() && F2 == seq * 11 && F3 == (long)seq << 10;
    }

    [Message]
    public sealed class M11 : Mixed
    {
        public string[] F0;
        public int F1;
        public long F2;
        public string F3;
        public double F4;
        public static M11 Make(int seq) => new M11
        {
            F0 = new[] { "a11", seq.ToString() },
            F1 = seq * 12,
            F2 = (long)seq << 11,
            F3 = "m11." + seq.ToString(),
            F4 = seq + 11.5,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == "a11" && F0[1] == seq.ToString() && F1 == seq * 12 && F2 == (long)seq << 11 && F3 == "m11." + seq.ToString() && F4 == seq + 11.5;
    }

    [Message]
    public sealed class M12 : Mixed
    {
        public int F0;
        public long F1;
        public static M12 Make(int seq) => new M12
        {
            F0 = seq * 13,
            F1 = (long)seq << 12,
        };
        public override bool Check(int seq) => F0 == seq * 13 && F1 == (long)seq << 12;
    }

    [Message]
    public sealed class M13 : Mixed
    {
        public long F0;
        public string F1;
        public double F2;
        public static M13 Make(int seq) => new M13
        {
            F0 = (long)seq << 13,
            F1 = "m13." + seq.ToString(),
            F2 = seq + 13.5,
        };
        public override bool Check(int seq) => F0 == (long)seq << 13 && F1 == "m13." + seq.ToString() && F2 == seq + 13.5;
    }

    [Message]
    public sealed class M14 : Mixed
    {
        public string F0;
        public double F1;
        public int[] F2;
        public string[] F3;
        public static M14 Make(int seq) => new M14
        {
            F0 = "m14." + seq.ToString(),
            F1 = seq + 14.5,
            F2 = new[] { seq, 14 },
            F3 = new[] { "a14", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == "m14." + seq.ToString() && F1 == seq + 14.5 && F2.Length == 2 && F2[0] == seq && F2[1] == 14 && F3.Length == 2 && F3[0] == "a14" && F3[1] == seq.ToString();
    }

    [Message]
    public sealed class M15 : Mixed
    {
        public double F0;
        public int[] F1;
        public string[] F2;
        public int F3;
        public long F4;
        public static M15 Make(int seq) => new M15
        {
            F0 = seq + 15.5,
            F1 = new[] { seq, 15 },
            F2 = new[] { "a15", seq.ToString() },
            F3 = seq * 16,
            F4 = (long)seq << 15,
        };
        public override bool Check(int seq) => F0 == seq + 15.5 && F1.Length == 2 && F1[0] == seq && F1[1] == 15 && F2.Length == 2 && F2[0] == "a15" && F2[1] == seq.ToString() && F3 == seq * 16 && F4 == (long)seq << 15;
    }

    [Message]
    public sealed class M16 : Mixed
    {
        public int[] F0;
        public string[] F1;
        public static M16 Make(int seq) => new M16
        {
            F0 = new[] { seq, 16 },
            F1 = new[] { "a16", seq.ToString() },
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == seq && F0[1] == 16 && F1.Length == 2 && F1[0] == "a16" && F1[1] == seq.ToString();
    }

    [Message]
    public sealed class M17 : Mixed
    {
        public string[] F0;
        public int F1;
        public long F2;
        public static M17 Make(int seq) => new M17
        {
            F0 = new[] { "a17", seq.ToString() },
            F1 = seq * 18,
            F2 = (long)seq << 17,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == "a17" && F0[1] == seq.ToString() && F1 == seq * 18 && F2 == (long)seq << 17;
    }

    [Message]
    public sealed class M18 : Mixed
    {
        public int F0;
        public long F1;
        public string F2;
        public double F3;
        public static M18 Make(int seq) => new M18
        {
            F0 = seq * 19,
            F1 = (long)seq << 18,
            F2 = "m18." + seq.ToString(),
            F3 = seq + 18.5,
        };
        public override bool Check(int seq) => F0 == seq * 19 && F1 == (long)seq << 18 && F2 == "m18." + seq.ToString() && F3 == seq + 18.5;
    }

    [Message]
    public sealed class M19 : Mixed
    {
        public long F0;
        public string F1;
        public double F2;
        public int[] F3;
        public string[] F4;
        public static M19 Make(int seq) => new M19
        {
            F0 = (long)seq << 19,
            F1 = "m19." + seq.ToString(),
            F2 = seq + 19.5,
            F3 = new[] { seq, 19 },
            F4 = new[] { "a19", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == (long)seq << 19 && F1 == "m19." + seq.ToString() && F2 == seq + 19.5 && F3.Length == 2 && F3[0] == seq && F3[1] == 19 && F4.Length == 2 && F4[0] == "a19" && F4[1] == seq.ToString();
    }

    [Message]
    public sealed class M20 : Mixed
    {
        public string F0;
        public double F1;
        public static M20 Make(int seq) => new M20
        {
            F0 = "m20." + seq.ToString(),
            F1 = seq + 20.5,
        };
        public override bool Check(int seq) => F0 == "m20." + seq.ToString() && F1 == seq + 20.5;
    }

    [Message]
    public sealed class M21 : Mixed
    {
        public double F0;
        public int[] F1;
        public string[] F2;
        public static M21 Make(int seq) => new M21
        {
            F0 = seq + 21.5,
            F1 = new[] { seq, 21 },
            F2 = new[] { "a21", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == seq + 21.5 && F1.Length == 2 && F1[0] == seq && F1[1] == 21 && F2.Length == 2 && F2[0] == "a21" && F2[1] == seq.ToString();
    }

    [Message]
    public sealed class M22 : Mixed
    {
        public int[] F0;
        public string[] F1;
        public int F2;
        public long F3;
        public static M22 Make(int seq) => new M22
        {
            F0 = new[] { seq, 22 },
            F1 = new[] { "a22", seq.ToString() },
            F2 = seq * 23,
            F3 = (long)seq << 22,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == seq && F0[1] == 22 && F1.Length == 2 && F1[0] == "a22" && F1[1] == seq.ToString() && F2 == seq * 23 && F3 == (long)seq << 22;
    }

    [Message]
    public sealed class M23 : Mixed
    {
        public string[] F0;
        public int F1;
        public long F2;
        public string F3;
        public double F4;
        public static M23 Make(int seq) => new M23
        {
            F0 = new[] { "a23", seq.ToString() },
            F1 = seq * 24,
            F2 = (long)seq << 23,
            F3 = "m23." + seq.ToString(),
            F4 = seq + 23.5,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == "a23" && F0[1] == seq.ToString() && F1 == seq * 24 && F2 == (long)seq << 23 && F3 == "m23." + seq.ToString() && F4 == seq + 23.5;
    }

    [Message]
    public sealed class M24 : Mixed
    {
        public int F0;
        public long F1;
        public static M24 Make(int seq) => new M24
        {
            F0 = seq * 25,
            F1 = (long)seq << 24,
        };
        public override bool Check(int seq) => F0 == seq * 25 && F1 == (long)seq << 24;
    }

    [Message]
    public sealed class M25 : Mixed
    {
        public long F0;
        public string F1;
        public double F2;
        public static M25 Make(int seq) => new M25
        {
            F0 = (long)seq << 25,
            F1 = "m25." + seq.ToString(),
            F2 = seq + 25.5,
        };
        public override bool Check(int seq) => F0 == (long)seq << 25 && F1 == "m25." + seq.ToString() && F2 == seq + 25.5;
    }

    [Message]
    public sealed class M26 : Mixed
    {
        public string F0;
        public double F1;
        public int[] F2;
        public string[] F3;
        public static M26 Make(int seq) => new M26
        {
            F0 = "m26." + seq.ToString(),
            F1 = seq + 26.5,
            F2 = new[] { seq, 26 },
            F3 = new[] { "a26", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == "m26." + seq.ToString() && F1 == seq + 26.5 && F2.Length == 2 && F2[0] == seq && F2[1] == 26 && F3.Length == 2 && F3[0] == "a26" && F3[1] == seq.ToString();
    }

    [Message]
    public sealed class M27 : Mixed
    {
        public double F0;
        public int[] F1;
        public string[] F2;
        public int F3;
        public long F4;
        public static M27 Make(int seq) => new M27
        {
            F0 = seq + 27.5,
            F1 = new[] { seq, 27 },
            F2 = new[] { "a27", seq.ToString() },
            F3 = seq * 28,
            F4 = (long)seq << 27,
        };
        public override bool Check(int seq) => F0 == seq + 27.5 && F1.Length == 2 && F1[0] == seq && F1[1] == 27 && F2.Length == 2 && F2[0] == "a27" && F2[1] == seq.ToString() && F3 == seq * 28 && F4 == (long)seq << 27;
    }

    [Message]
    public sealed class M28 : Mixed
    {
        public int[] F0;
        public string[] F1;
        public static M28 Make(int seq) => new M28
        {
            F0 = new[] { seq, 28 },
            F1 = new[] { "a28", seq.ToString() },
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == seq && F0[1] == 28 && F1.Length == 2 && F1[0] == "a28" && F1[1] == seq.ToString();
    }

    [Message]
    public sealed class M29 : Mixed
    {
        public string[] F0;
        public int F1;
        public long F2;
        public static M29 Make(int seq) => new M29
        {
            F0 = new[] { "a29", seq.ToString() },
            F1 = seq * 30,
            F2 = (long)seq << 29,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == "a29" && F0[1] == seq.ToString() && F1 == seq * 30 && F2 == (long)seq << 29;
    }

    [Message]
    public sealed class M30 : Mixed
    {
        public int F0;
        public long F1;
        public string F2;
        public double F3;
        public static M30 Make(int seq) => new M30
        {
            F0 = seq * 31,
            F1 = (long)seq << 0,
            F2 = "m30." + seq.ToString(),
            F3 = seq + 30.5,
        };
        public override bool Check(int seq) => F0 == seq * 31 && F1 == (long)seq << 0 && F2 == "m30." + seq.ToString() && F3 == seq + 30.5;
    }

    [Message]
    public sealed class M31 : Mixed
    {
        public long F0;
        public string F1;
        public double F2;
        public int[] F3;
        public string[] F4;
        public static M31 Make(int seq) => new M31
        {
            F0 = (long)seq << 1,
            F1 = "m31." + seq.ToString(),
            F2 = seq + 31.5,
            F3 = new[] { seq, 31 },
            F4 = new[] { "a31", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == (long)seq << 1 && F1 == "m31." + seq.ToString() && F2 == seq + 31.5 && F3.Length == 2 && F3[0] == seq && F3[1] == 31 && F4.Length == 2 && F4[0] == "a31" && F4[1] == seq.ToString();
    }

    [Message]
    public sealed class M32 : Mixed
    {
        public string F0;
        public double F1;
        public static M32 Make(int seq) => new M32
        {
            F0 = "m32." + seq.ToString(),
            F1 = seq + 32.5,
        };
        public override bool Check(int seq) => F0 == "m32." + seq.ToString() && F1 == seq + 32.5;
    }

    [Message]
    public sealed class M33 : Mixed
    {
        public double F0;
        public int[] F1;
        public string[] F2;
        public static M33 Make(int seq) => new M33
        {
            F0 = seq + 33.5,
            F1 = new[] { seq, 33 },
            F2 = new[] { "a33", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == seq + 33.5 && F1.Length == 2 && F1[0] == seq && F1[1] == 33 && F2.Length == 2 && F2[0] == "a33" && F2[1] == seq.ToString();
    }

    [Message]
    public sealed class M34 : Mixed
    {
        public int[] F0;
        public string[] F1;
        public int F2;
        public long F3;
        public static M34 Make(int seq) => new M34
        {
            F0 = new[] { seq, 34 },
            F1 = new[] { "a34", seq.ToString() },
            F2 = seq * 35,
            F3 = (long)seq << 4,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == seq && F0[1] == 34 && F1.Length == 2 && F1[0] == "a34" && F1[1] == seq.ToString() && F2 == seq * 35 && F3 == (long)seq << 4;
    }

    [Message]
    public sealed class M35 : Mixed
    {
        public string[] F0;
        public int F1;
        public long F2;
        public string F3;
        public double F4;
        public static M35 Make(int seq) => new M35
        {
            F0 = new[] { "a35", seq.ToString() },
            F1 = seq * 36,
            F2 = (long)seq << 5,
            F3 = "m35." + seq.ToString(),
            F4 = seq + 35.5,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == "a35" && F0[1] == seq.ToString() && F1 == seq * 36 && F2 == (long)seq << 5 && F3 == "m35." + seq.ToString() && F4 == seq + 35.5;
    }

    [Message]
    public sealed class M36 : Mixed
    {
        public int F0;
        public long F1;
        public static M36 Make(int seq) => new M36
        {
            F0 = seq * 37,
            F1 = (long)seq << 6,
        };
        public override bool Check(int seq) => F0 == seq * 37 && F1 == (long)seq << 6;
    }

    [Message]
    public sealed class M37 : Mixed
    {
        public long F0;
        public string F1;
        public double F2;
        public static M37 Make(int seq) => new M37
        {
            F0 = (long)seq << 7,
            F1 = "m37." + seq.ToString(),
            F2 = seq + 37.5,
        };
        public override bool Check(int seq) => F0 == (long)seq << 7 && F1 == "m37." + seq.ToString() && F2 == seq + 37.5;
    }

    [Message]
    public sealed class M38 : Mixed
    {
        public string F0;
        public double F1;
        public int[] F2;
        public string[] F3;
        public static M38 Make(int seq) => new M38
        {
            F0 = "m38." + seq.ToString(),
            F1 = seq + 38.5,
            F2 = new[] { seq, 38 },
            F3 = new[] { "a38", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == "m38." + seq.ToString() && F1 == seq + 38.5 && F2.Length == 2 && F2[0] == seq && F2[1] == 38 && F3.Length == 2 && F3[0] == "a38" && F3[1] == seq.ToString();
    }

    [Message]
    public sealed class M39 : Mixed
    {
        public double F0;
        public int[] F1;
        public string[] F2;
        public int F3;
        public long F4;
        public static M39 Make(int seq) => new M39
        {
            F0 = seq + 39.5,
            F1 = new[] { seq, 39 },
            F2 = new[] { "a39", seq.ToString() },
            F3 = seq * 40,
            F4 = (long)seq << 9,
        };
        public override bool Check(int seq) => F0 == seq + 39.5 && F1.Length == 2 && F1[0] == seq && F1[1] == 39 && F2.Length == 2 && F2[0] == "a39" && F2[1] == seq.ToString() && F3 == seq * 40 && F4 == (long)seq << 9;
    }

    [Message]
    public sealed class M40 : Mixed
    {
        public int[] F0;
        public string[] F1;
        public static M40 Make(int seq) => new M40
        {
            F0 = new[] { seq, 40 },
            F1 = new[] { "a40", seq.ToString() },
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == seq && F0[1] == 40 && F1.Length == 2 && F1[0] == "a40" && F1[1] == seq.ToString();
    }

    [Message]
    public sealed class M41 : Mixed
    {
        public string[] F0;
        public int F1;
        public long F2;
        public static M41 Make(int seq) => new M41
        {
            F0 = new[] { "a41", seq.ToString() },
            F1 = seq * 42,
            F2 = (long)seq << 11,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == "a41" && F0[1] == seq.ToString() && F1 == seq * 42 && F2 == (long)seq << 11;
    }

    [Message]
    public sealed class M42 : Mixed
    {
        public int F0;
        public long F1;
        public string F2;
        public double F3;
        public static M42 Make(int seq) => new M42
        {
            F0 = seq * 43,
            F1 = (long)seq << 12,
            F2 = "m42." + seq.ToString(),
            F3 = seq + 42.5,
        };
        public override bool Check(int seq) => F0 == seq * 43 && F1 == (long)seq << 12 && F2 == "m42." + seq.ToString() && F3 == seq + 42.5;
    }

    [Message]
    public sealed class M43 : Mixed
    {
        public long F0;
        public string F1;
        public double F2;
        public int[] F3;
        public string[] F4;
        public static M43 Make(int seq) => new M43
        {
            F0 = (long)seq << 13,
            F1 = "m43." + seq.ToString(),
            F2 = seq + 43.5,
            F3 = new[] { seq, 43 },
            F4 = new[] { "a43", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == (long)seq << 13 && F1 == "m43." + seq.ToString() && F2 == seq + 43.5 && F3.Length == 2 && F3[0] == seq && F3[1] == 43 && F4.Length == 2 && F4[0] == "a43" && F4[1] == seq.ToString();
    }

    [Message]
    public sealed class M44 : Mixed
    {
        public string F0;
        public double F1;
        public static M44 Make(int seq) => new M44
        {
            F0 = "m44." + seq.ToString(),
            F1 = seq + 44.5,
        };
        public override bool Check(int seq) => F0 == "m44." + seq.ToString() && F1 == seq + 44.5;
    }

    [Message]
    public sealed class M45 : Mixed
    {
        public double F0;
        public int[] F1;
        public string[] F2;
        public static M45 Make(int seq) => new M45
        {
            F0 = seq + 45.5,
            F1 = new[] { seq, 45 },
            F2 = new[] { "a45", seq.ToString() },
        };
        public override bool Check(int seq) => F0 == seq + 45.5 && F1.Length == 2 && F1[0] == seq && F1[1] == 45 && F2.Length == 2 && F2[0] == "a45" && F2[1] == seq.ToString();
    }

    [Message]
    public sealed class M46 : Mixed
    {
        public int[] F0;
        public string[] F1;
        public int F2;
        public long F3;
        public static M46 Make(int seq) => new M46
        {
            F0 = new[] { seq, 46 },
            F1 = new[] { "a46", seq.ToString() },
            F2 = seq * 47,
            F3 = (long)seq << 16,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == seq && F0[1] == 46 && F1.Length == 2 && F1[0] == "a46" && F1[1] == seq.ToString() && F2 == seq * 47 && F3 == (long)seq << 16;
    }

    [Message]
    public sealed class M47 : Mixed
    {
        public string[] F0;
        public int F1;
        public long F2;
        public string F3;
        public double F4;
        public static M47 Make(int seq) => new M47
        {
            F0 = new[] { "a47", seq.ToString() },
            F1 = seq * 48,
            F2 = (long)seq << 17,
            F3 = "m47." + seq.ToString(),
            F4 = seq + 47.5,
        };
        public override bool Check(int seq) => F0.Length == 2 && F0[0] == "a47" && F0[1] == seq.ToString() && F1 == seq * 48 && F2 == (long)seq << 17 && F3 == "m47." + seq.ToString() && F4 == seq + 47.5;
    }

    [Message]
    public sealed class M48 : Mixed
    {
        public int F0;
        public long F1;
        public static M48 Make(int seq) => new M48
        {
            F0 = seq * 49,
            F1 = (long)seq << 18,
        };
        public override bool Check(int seq) => F0 == seq * 49 && F1 == (long)seq << 18;
    }

    [Message]
    public sealed class M49 : Mixed
    {
        public long F0;
        public string F1;
        public double F2;
        public static M49 Make(int seq) => new M49
        {
            F0 = (long)seq << 19,
            F1 = "m49." + seq.ToString(),
            F2 = seq + 49.5,
        };
        public override bool Check(int seq) => F0 == (long)seq << 19 && F1 == "m49." + seq.ToString() && F2 == seq + 49.5;
    }

    public static class MixedTypes
    {
        public static Mixed Make(int type, int seq)
        {
            switch (type)
            {
                case 0: return M0.Make(seq);
                case 1: return M1.Make(seq);
                case 2: return M2.Make(seq);
                case 3: return M3.Make(seq);
                case 4: return M4.Make(seq);
                case 5: return M5.Make(seq);
                case 6: return M6.Make(seq);
                case 7: return M7.Make(seq);
                case 8: return M8.Make(seq);
                case 9: return M9.Make(seq);
                case 10: return M10.Make(seq);
                case 11: return M11.Make(seq);
                case 12: return M12.Make(seq);
                case 13: return M13.Make(seq);
                case 14: return M14.Make(seq);
                case 15: return M15.Make(seq);
                case 16: return M16.Make(seq);
                case 17: return M17.Make(seq);
                case 18: return M18.Make(seq);
                case 19: return M19.Make(seq);
                case 20: return M20.Make(seq);
                case 21: return M21.Make(seq);
                case 22: return M22.Make(seq);
                case 23: return M23.Make(seq);
                case 24: return M24.Make(seq);
                case 25: return M25.Make(seq);
                case 26: return M26.Make(seq);
                case 27: return M27.Make(seq);
                case 28: return M28.Make(seq);
                case 29: return M29.Make(seq);
                case 30: return M30.Make(seq);
                case 31: return M31.Make(seq);
                case 32: return M32.Make(seq);
                case 33: return M33.Make(seq);
                case 34: return M34.Make(seq);
                case 35: return M35.Make(seq);
                case 36: return M36.Make(seq);
                case 37: return M37.Make(seq);
                case 38: return M38.Make(seq);
                case 39: return M39.Make(seq);
                case 40: return M40.Make(seq);
                case 41: return M41.Make(seq);
                case 42: return M42.Make(seq);
                case 43: return M43.Make(seq);
                case 44: return M44.Make(seq);
                case 45: return M45.Make(seq);
                case 46: return M46.Make(seq);
                case 47: return M47.Make(seq);
                case 48: return M48.Make(seq);
                case 49: return M49.Make(seq);
                default: return null;
            }
        }
    }
}
