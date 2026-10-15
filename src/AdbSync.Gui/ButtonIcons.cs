using System.Drawing.Drawing2D;

namespace AdbSync.Gui;

/// <summary>
/// Icone dei pulsanti principali disegnate al volo (nessun file immagine da distribuire), nitide a ogni DPI.
/// Metafora del registratore: lente = analizza, ▶ = avvia, ■ = ferma.
/// </summary>
internal static class ButtonIcons
{
    /// <summary>Lente d'ingrandimento: "Confronta" (analizza telefono e PC, non scrive nulla).</summary>
    public static Bitmap Compare(int size) => Draw(size, (g, s) =>
    {
        float r = s * 0.30f, cx = s * 0.40f, cy = s * 0.40f;
        using var pen = new Pen(Color.FromArgb(0, 90, 170), Math.Max(2f, s * 0.12f));
        g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
        using var handle = new Pen(Color.FromArgb(0, 90, 170), Math.Max(2.5f, s * 0.16f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float d = r * 0.72f;
        g.DrawLine(handle, cx + d, cy + d, s * 0.88f, s * 0.88f);
    });

    /// <summary>Triangolo verde "play": "Sincronizza" (avvia la copia).</summary>
    public static Bitmap Play(int size) => Draw(size, (g, s) =>
    {
        var pts = new[] { new PointF(s * 0.22f, s * 0.12f), new PointF(s * 0.88f, s * 0.50f), new PointF(s * 0.22f, s * 0.88f) };
        using var b = new SolidBrush(Color.FromArgb(0, 150, 60));
        g.FillPolygon(b, pts);
    });

    /// <summary>Quadrato rosso "stop": "Annulla".</summary>
    public static Bitmap Stop(int size) => Draw(size, (g, s) =>
    {
        using var b = new SolidBrush(Color.FromArgb(200, 40, 40));
        float m = s * 0.20f;
        g.FillRectangle(b, m, m, s - 2 * m, s - 2 * m);
    });

    private static Bitmap Draw(int size, Action<Graphics, float> paint)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        paint(g, size);
        return bmp;
    }
}
