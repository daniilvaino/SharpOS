// StarlingProbe — проба движка Starling на CoreCLR-hosted ярусе.
//
// Что доказывает: конвейер HTML -> DOM -> CSS -> раскладка -> display-list
// отрабатывает на bare metal SharpOS. Верхние слои Starling (Engine, Bindings)
// не участвуют: они требуют net11, Wasmtime и сеть. Здесь только низ стека,
// весь net10 и без единой нативной зависимости.
//
// Вывод идёт в Console -> COM3 (last_app.log).
using System.Diagnostics;
using System.Runtime.InteropServices;
using Starling.Css.Cascade;
using Starling.Css.Values;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Text;
using Starling.Paint.DisplayList;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace SharpOS.StarlingProbe;

internal static class Program
{
    private const int W = 800, H = 600;

    private const string Page = """
        <body style="margin:0;background-color:rgb(250,250,252)">
          <div style="margin:0;height:70px;background-color:rgb(32,58,102)"></div>
          <div style="margin:16px;height:120px;background-color:rgb(220,90,70)"></div>
          <div style="margin:16px;height:90px;background-color:rgb(70,160,120)"></div>
          <div style="margin:16px;height:200px;background-color:rgb(240,200,80)"></div>
        </body>
        """;

    private static int Main()
    {
        Console.WriteLine("[starling] проба движка на hosted-ярусе");
        var total = Stopwatch.StartNew();

        PaintList dl;
        try
        {
            var sw = Stopwatch.StartNew();
            var document = HtmlParser.Parse(Page);
            Console.WriteLine($"[starling] разбор HTML     : {sw.ElapsedMilliseconds} мс");

            sw.Restart();
            var style  = new StyleEngine();
            var layout = new LayoutEngine(style, DefaultTextMeasurer.Instance);
            var root   = layout.LayoutDocument(document, new Size(W, H));
            Console.WriteLine($"[starling] каскад+раскладка: {sw.ElapsedMilliseconds} мс, " +
                              $"root.Frame = {root.Frame.Width}x{root.Frame.Height}");

            sw.Restart();
            dl = new DisplayListBuilder().Build(root, new Rect(0, 0, W, H));
            Console.WriteLine($"[starling] display-list    : {sw.ElapsedMilliseconds} мс, " +
                              $"{dl.Items.Count} элементов");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[starling] ПРОВАЛ на конвейере: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }

        foreach (var item in dl.Items)
            Console.WriteLine($"[starling]   {item.GetType().Name}");

        var rgba = Render(dl, new Rect(0, 0, W, H));
        Console.WriteLine($"[starling] растр           : {W}x{H}, контрольная сумма {Checksum(rgba):X8}");

        TryBlit(rgba);

        Console.WriteLine($"[starling] ГОТОВО за {total.ElapsedMilliseconds} мс");
        return 0;
    }

    private static byte[] Render(PaintList list, Rect vp)
    {
        var rgba = new byte[W * H * 4];
        Array.Fill(rgba, (byte)255);
        foreach (var item in list.Items)
        {
            switch (item)
            {
                case FillRect f:        Fill(rgba, f.Bounds, vp, f.Color); break;
                case FillRoundedRect r: Fill(rgba, r.Bounds, vp, r.Color); break;
                case StrokeRect s:      Fill(rgba, s.Bounds, vp, s.Color); break;
            }
        }
        return rgba;
    }

    private static void Fill(byte[] rgba, Rect b, Rect vp, CssColor color)
    {
        var s = color.ToSrgb();
        int x0 = Math.Clamp((int)(b.X - vp.X), 0, W);
        int y0 = Math.Clamp((int)(b.Y - vp.Y), 0, H);
        int x1 = Math.Clamp((int)Math.Ceiling(b.X - vp.X + b.Width), 0, W);
        int y1 = Math.Clamp((int)Math.Ceiling(b.Y - vp.Y + b.Height), 0, H);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * W + x) * 4;
                rgba[i] = s.R; rgba[i + 1] = s.G; rgba[i + 2] = s.B; rgba[i + 3] = s.A;
            }
    }

    private static uint Checksum(byte[] data)
    {
        uint h = 2166136261u;
        foreach (var b in data) { h ^= b; h *= 16777619u; }
        return h;
    }

    // Эксперимент: дотянется ли hosted-программа до фреймбуфера.
    // Экспорт SharpOSHost_GetFramebuffer в ядре есть (OS/src/PAL/SharpOSHost/
    // Framebuffer.cs), но из hosted-яруса к нему пока никто не ходил — только
    // AOT-программы через AppHost.TryGetFramebuffer. Провал здесь не ломает
    // пробу: конвейер уже доказан выше.
    [DllImport("*", EntryPoint = "SharpOSHost_GetFramebuffer")]
    private static unsafe extern int GetFramebuffer(
        ulong* baseVa, uint* width, uint* height, uint* stride, uint* format);

    private static unsafe void TryBlit(byte[] rgba)
    {
        ulong baseVa = 0; uint fbW = 0, fbH = 0, stride = 0, format = 0;
        try
        {
            if (GetFramebuffer(&baseVa, &fbW, &fbH, &stride, &format) == 0 || baseVa == 0)
            {
                Console.WriteLine("[starling] фреймбуфер      : ядро говорит «недоступен»");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[starling] фреймбуфер      : не дотянулись ({ex.GetType().Name}) — ожидаемо");
            return;
        }

        Console.WriteLine($"[starling] фреймбуфер      : {fbW}x{fbH}, шаг {stride}, формат {format}");
        var fb = (byte*)baseVa;
        int cols = (int)Math.Min((uint)W, fbW), rows = (int)Math.Min((uint)H, fbH);
        for (int y = 0; y < rows; y++)
        {
            byte* dst = fb + (long)y * stride;
            for (int x = 0; x < cols; x++)
            {
                int i = (y * W + x) * 4;
                dst[x * 4 + 0] = rgba[i + 2];   // B
                dst[x * 4 + 1] = rgba[i + 1];   // G
                dst[x * 4 + 2] = rgba[i + 0];   // R
                dst[x * 4 + 3] = 255;
            }
        }
        Console.WriteLine("[starling] КАРТИНКА НА ЭКРАНЕ");
    }
}
